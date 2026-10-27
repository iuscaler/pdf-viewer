using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using PDFViewer.App.Annotations;
using PDFViewer.App.Diagnostics;
using PDFViewer.Core.Annotations;
using PDFViewer.Core.Layout;

namespace PDFViewer.App.Controls;

public enum AnnotationTool
{
    /// <summary>기본 도구. 텍스트를 선택하고 복사할 수 있다.</summary>
    Select,
    Pen,

    /// <summary>텍스트 위에서 끌면 텍스트 형광펜, 그 밖에서는 자유 형광펜.</summary>
    Highlighter,
    Eraser,
}

/// <summary>
/// 펜/자유 형광펜/지우개 입력과 주석 그리기. 텍스트 선택과 텍스트 형광펜은 PdfDocumentView.Text.cs에 있다.
/// </summary>
/// <remarks>
/// 펜 도구일 때는 화면 전체를 덮는 <see cref="InkCanvas"/>가 입력을 받는다. 필압, 곡선 보정, 지연이 적은 실시간 그리기는
/// WPF가 처리한다. 획이 끝나면 페이지 좌표(포인트)로 바꿔 <see cref="AnnotationSet"/>에 넣고 InkCanvas에서는 지운다.
/// <para>
/// 주석 목록이 바뀌면 <see cref="PdfAnnotationSync"/>가 PDF 문서에 실제 PDF 주석으로 넣고, 그 페이지의 리비전을 올려
/// 다시 렌더링한다. 새 비트맵이 오기 전까지는 주석을 WPF로 임시로 그린다(포인트 단위 Drawing을 페이지마다 캐시).
/// 새 비트맵이 오면 PDFium이 그린 주석(형광펜은 곱하기 합성)만 보인다.
/// </para>
/// </remarks>
public sealed partial class PdfDocumentView
{
    // 지우개 반지름 (화면 DIP). 배율과 상관없이 화면에서 같은 크기로 보인다.
    private const double EraserRadius = 8;

    // 다시 렌더링되기 전까지 임시로 보여주는 텍스트 형광펜의 불투명도 (0~255)
    private const byte TextHighlightAlpha = 0x70;

    private static readonly Pen EraserPen = CreateFrozenPen(Color.FromArgb(0xC0, 0x40, 0x40, 0x40), 1);
    private static readonly Brush EraserBrush = CreateFrozenBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    public static readonly DependencyProperty AnnotationsProperty = DependencyProperty.Register(
        nameof(Annotations), typeof(AnnotationSet), typeof(PdfDocumentView),
        new PropertyMetadata(null, (d, e) =>
            ((PdfDocumentView)d).OnAnnotationsChanged((AnnotationSet?)e.OldValue, (AnnotationSet?)e.NewValue)));

    public static readonly DependencyProperty ActiveToolProperty = DependencyProperty.Register(
        nameof(ActiveTool), typeof(AnnotationTool), typeof(PdfDocumentView),
        new PropertyMetadata(AnnotationTool.Select, (d, _) => ((PdfDocumentView)d).UpdateToolState()));

    public static readonly DependencyProperty PenColorProperty = DependencyProperty.Register(
        nameof(PenColor), typeof(Color), typeof(PdfDocumentView),
        new PropertyMetadata(Colors.Black, (d, _) => ((PdfDocumentView)d).UpdateInkCanvasAttributes()));

    public static readonly DependencyProperty PenWidthProperty = DependencyProperty.Register(
        nameof(PenWidth), typeof(double), typeof(PdfDocumentView),
        new PropertyMetadata(1.5, (d, _) => ((PdfDocumentView)d).UpdateInkCanvasAttributes()));

    // 페이지별 임시 펜 획 Drawing. 화면에 보이는 비트맵의 리비전이 바뀌면 다시 만든다.
    private readonly Dictionary<int, PendingDrawing> _inkDrawings = new();

    // 페이지별 주석 리비전. 주석이 바뀔 때마다 올라가고, 렌더링 요청과 결과 비트맵에 붙는다.
    private readonly Dictionary<int, int> _pageRevisions = new();

    // 주석이 PDF에 들어간 페이지 리비전. 이 리비전 이상의 비트맵에는 그 주석이 이미 그려져 있다.
    private readonly Dictionary<Guid, int> _addedRevisions = new();
    private PdfAnnotationSync? _annotationSync;
    private InkCanvas _inkCanvas = null!;

    // 지우개: 화면에 그릴 지우개 위치, 한 번 끄는 동안의 실행 취소 묶음, 펜 뒤쪽으로 지우는 중인지 여부
    private Point? _eraserPosition;
    private IDisposable? _eraseGroup;
    private bool _stylusErasing;

    public AnnotationSet? Annotations
    {
        get => (AnnotationSet?)GetValue(AnnotationsProperty);
        set => SetValue(AnnotationsProperty, value);
    }

    public AnnotationTool ActiveTool
    {
        get => (AnnotationTool)GetValue(ActiveToolProperty);
        set => SetValue(ActiveToolProperty, value);
    }

    public Color PenColor
    {
        get => (Color)GetValue(PenColorProperty);
        set => SetValue(PenColorProperty, value);
    }

    /// <summary>펜 굵기 (포인트). 화면에서는 배율에 맞춰 굵어진다.</summary>
    public double PenWidth
    {
        get => (double)GetValue(PenWidthProperty);
        set => SetValue(PenWidthProperty, value);
    }

    private double InkScale => _layout.Zoom * DocumentLayout.PointsToDip;

    private void InitializeInk()
    {
        _inkCanvas = new InkCanvas
        {
            Background = Brushes.Transparent,
            EditingMode = InkCanvasEditingMode.Ink,
            // 펜 뒤쪽(지우개)은 InkCanvas가 아니라 이 뷰가 처리한다.
            EditingModeInverted = InkCanvasEditingMode.None,
            // 그릴 때 키보드 포커스를 가져가면 InkCanvas 자체의 Ctrl+C(잉크 복사)와 방향키 처리가
            // 문서 스크롤과 텍스트 복사를 가로챈다.
            Focusable = false,
        };
        _inkCanvas.StrokeCollected += OnStrokeCollected;
        AddVisualChild(_inkCanvas);
        UpdateToolState();
    }

    private void OnAnnotationsChanged(AnnotationSet? oldSet, AnnotationSet? newSet)
    {
        if (oldSet is not null)
        {
            oldSet.Changed -= OnAnnotationsEdited;
            oldSet.PageChanged -= OnAnnotationPageChanged;
        }

        if (newSet is not null)
        {
            newSet.Changed += OnAnnotationsEdited;
            newSet.PageChanged += OnAnnotationPageChanged;
        }

        _inkDrawings.Clear();
        _highlightDrawings.Clear();
        _addedRevisions.Clear();
        RecreateAnnotationSync();
        UpdateToolState();
        InvalidateVisual();
    }

    /// <summary>문서나 주석 목록이 바뀌면 PDF 반영 대상을 다시 연결한다.</summary>
    private void RecreateAnnotationSync()
    {
        _annotationSync?.Dispose();
        _annotationSync = Document is { } document && Annotations is { } annotations
            ? new PdfAnnotationSync(document, annotations)
            : null;
    }

    private int PageRevision(int pageIndex) => _pageRevisions.GetValueOrDefault(pageIndex);

    // Changed는 PageChanged보다 먼저 온다. 바로 뒤의 PageChanged에서 페이지 리비전이 1 올라가고,
    // 그 리비전부터의 비트맵에는 추가된 주석이 들어 있다.
    private void OnAnnotationsEdited(IReadOnlyList<Annotation> added, IReadOnlyList<Annotation> removed)
    {
        foreach (var annotation in added)
        {
            _addedRevisions[annotation.Id] = PageRevision(annotation.PageIndex) + 1;
        }
    }

    /// <summary>
    /// 화면에 보이는 비트맵(리비전 shownRevision)에 아직 그려지지 않은 주석들. 임시로 그릴 때는 이것들만 그린다.
    /// 이미 비트맵에 있는 주석까지 그리면 형광펜은 두 번 곱해져 진해졌다가 돌아오며 깜빡인다.
    /// </summary>
    private List<Annotation> PendingAnnotations(int pageIndex, int shownRevision) =>
        Annotations?.GetAnnotations(pageIndex)
            .Where(a => _addedRevisions.GetValueOrDefault(a.Id) > shownRevision)
            .ToList() ?? [];

    private void OnAnnotationPageChanged(int pageIndex)
    {
        // PdfAnnotationSync가 이미 PDF 작업을 큐에 넣었으므로, 지금 요청하는 렌더링에는 변경이 반영된다.
        // 새 비트맵이 올 때까지 기존 비트맵(상세 영역 포함)을 그대로 보여주고 그 위에 임시로 주석을 그린다.
        // 기존 비트맵을 먼저 버리면 흐린 이미지가 잠깐 보였다가 선명해져서 깜빡거린다.
        _pageRevisions[pageIndex] = PageRevision(pageIndex) + 1;
        _inkDrawings.Remove(pageIndex);
        _highlightDrawings.Remove(pageIndex);
        UpdateRenderRequests();
        InvalidateVisual();
    }

    private bool IsHighlighterTool => ActiveTool == AnnotationTool.Highlighter;

    private void UpdateToolState()
    {
        var canAnnotate = Annotations is not null && Document is not null;
        // 펜과 형광펜은 이 뷰가 직접 그린다(PdfDocumentView.Freehand.cs). InkCanvas는 곱하기 합성을 쓸 수 없는
        // 환경의 형광펜에만 쓴다.
        _inkCanvas.IsHitTestVisible = canAnnotate && IsHighlighterTool && !UsesMultiply;
        // 지우개는 화면에 원을 직접 그리므로 커서를 숨긴다.
        Cursor = !canAnnotate ? null
            : ActiveTool == AnnotationTool.Eraser ? Cursors.None
            : ActiveTool == AnnotationTool.Pen ? Cursors.Pen
            : null;
        _eraserPosition = null;
        ClearTextSelection();
        EnsureVisibleTextLoaded();
        UpdateInkCanvasAttributes();
        InvalidateVisual();
    }

    /// <summary>InkCanvas가 그리는 획의 색과 굵기를 현재 도구(펜/형광펜) 설정과 배율에 맞춘다.</summary>
    private void UpdateInkCanvasAttributes()
    {
        var color = IsHighlighterTool ? HighlighterColor : PenColor;
        var width = Math.Max(0.5, (IsHighlighterTool ? HighlighterWidth : PenWidth) * InkScale);
        var current = _inkCanvas.DefaultDrawingAttributes;
        if (current.Color == color && current.IsHighlighter == IsHighlighterTool
            && Math.Abs(current.Width - width) < 0.01)
        {
            return;
        }

        _inkCanvas.DefaultDrawingAttributes = InkGeometry.CreateAttributes(color, width, IsHighlighterTool);
    }

    #region 펜

    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        // 화면 좌표 획은 버리고, 페이지 좌표로 바꿔 주석 목록에 넣는다(그리기는 OnRender가 한다).
        _inkCanvas.Strokes.Remove(e.Stroke);

        var points = e.Stroke.StylusPoints;
        if (Annotations is not { } annotations || Document is not { } document || points.Count == 0)
        {
            return;
        }

        // 획이 시작된 페이지에 속한다. 페이지 밖에서 시작한 획은 버린다.
        var start = ViewToDocument(new Point(points[0].X, points[0].Y));
        if (FindPageAt(start.X, start.Y, 0) is not { } page)
        {
            return;
        }

        var rect = _layout.PageRects[page];
        var size = document.PageSizes[page];
        var sx = size.Width / rect.Width;
        var sy = size.Height / rect.Height;
        var inkPoints = new InkPoint[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var (x, y) = ViewToDocument(new Point(points[i].X, points[i].Y));
            inkPoints[i] = new InkPoint((float)((x - rect.X) * sx), (float)((y - rect.Y) * sy),
                points[i].PressureFactor);
        }

        annotations.Add(IsHighlighterTool
            ? new InkStroke(page, inkPoints, InkGeometry.ToArgb(HighlighterColor), HighlighterWidth, isHighlighter: true)
            : new InkStroke(page, inkPoints, InkGeometry.ToArgb(PenColor), PenWidth));
    }

    /// <summary>문서 좌표를 페이지 왼쪽 위 기준 포인트 좌표로 바꾼다.</summary>
    private (double X, double Y) DocumentToPage(int pageIndex, double x, double y)
    {
        var rect = _layout.PageRects[pageIndex];
        var size = Document!.PageSizes[pageIndex];
        return ((x - rect.X) * size.Width / rect.Width, (y - rect.Y) * size.Height / rect.Height);
    }

    /// <summary>문서 좌표가 들어 있는 페이지 (화면에 보이는 페이지 중에서). tolerance만큼 바깥도 포함한다.</summary>
    private int? FindPageAt(double x, double y, double tolerance)
    {
        var (first, last) = VisiblePages;
        for (var i = first; i <= last; i++)
        {
            var r = _layout.PageRects[i];
            if (x >= r.X - tolerance && x <= r.Right + tolerance && y >= r.Y - tolerance && y <= r.Bottom + tolerance)
            {
                return i;
            }
        }

        return null;
    }

    #endregion

    #region 지우개

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ActiveTool == AnnotationTool.Eraser && Annotations is not null && CaptureMouse())
        {
            BeginErase(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (ActiveTool != AnnotationTool.Eraser)
        {
            return;
        }

        var position = e.GetPosition(this);
        _eraserPosition = position;
        InvalidateVisual();
        if (_eraseGroup is not null && !_stylusErasing)
        {
            EraseAt(position);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndErase();
        EndTextSelection();
        if (!_stylusDrawing)
        {
            EndFreehand();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_eraserPosition is not null)
        {
            _eraserPosition = null;
            InvalidateVisual();
        }
    }

    // 펜 입력은 InkCanvas보다 먼저 받도록 Preview 이벤트로 처리한다.
    // - 펜을 뒤집어 지우개 쪽으로 쓰면 어떤 도구를 선택했든 지운다.
    // - 선택/형광펜 도구에서 텍스트 위를 누르면 텍스트를 선택한다(형광펜이면 칠한다).
    // - 형광펜 도구에서 텍스트가 아닌 곳을 누르면 자유 형광펜을 그린다(곱하기 합성을 쓸 수 있을 때).
    protected override void OnPreviewStylusDown(StylusDownEventArgs e)
    {
        base.OnPreviewStylusDown(e);
        if (e.Inverted && Annotations is not null)
        {
            e.StylusDevice.Capture(this);
            _stylusErasing = true;
            BeginErase(e.GetPosition(this));
            e.Handled = true;
        }
        else if (!e.Inverted && TryBeginTextSelection(e.GetPosition(this)))
        {
            e.StylusDevice.Capture(this);
            _stylusSelecting = true;
            e.Handled = true;
        }
        else if (!e.Inverted && e.GetStylusPoints(this) is { Count: > 0 } downPoints
                 && TryBeginFreehand(new Point(downPoints[0].X, downPoints[0].Y), downPoints[0].PressureFactor,
                     "pen"))
        {
            e.StylusDevice.Capture(this);
            _stylusDrawing = true;
            e.Handled = true;
        }
    }

    protected override void OnPreviewStylusMove(StylusEventArgs e)
    {
        base.OnPreviewStylusMove(e);
        if (_stylusErasing)
        {
            EraseAt(e.GetPosition(this));
            e.Handled = true;
        }
        else if (_stylusSelecting)
        {
            UpdateTextSelection(e.GetPosition(this));
            e.Handled = true;
        }
        else if (_stylusDrawing)
        {
            // 펜은 한 이벤트에 여러 점(필압 포함)이 함께 온다.
            var points = e.GetStylusPoints(this);
            if (InputLog.Enabled)
            {
                InputLog.StylusMove(points, points.Select(p => ViewToLiveStrokePage(new Point(p.X, p.Y))));
            }

            foreach (var p in points)
            {
                AddFreehandPoint(new Point(p.X, p.Y), p.PressureFactor);
            }
            e.Handled = true;
        }
    }

    protected override void OnPreviewStylusUp(StylusEventArgs e)
    {
        base.OnPreviewStylusUp(e);
        if (_stylusErasing)
        {
            _stylusErasing = false;
            e.StylusDevice.Capture(null);
            EndErase();
            e.Handled = true;
        }
        else if (_stylusSelecting)
        {
            _stylusSelecting = false;
            e.StylusDevice.Capture(null);
            EndTextSelection();
            e.Handled = true;
        }
        else if (_stylusDrawing)
        {
            _stylusDrawing = false;
            e.StylusDevice.Capture(null);
            EndFreehand();
            e.Handled = true;
        }
    }

    private void BeginErase(Point position)
    {
        // 한 번 끄는 동안 지운 획은 실행 취소 한 번으로 되돌린다.
        _eraseGroup ??= Annotations?.BeginGroup();
        EraseAt(position);
    }

    private void EndErase()
    {
        _eraseGroup?.Dispose();
        _eraseGroup = null;
    }

    private void EraseAt(Point viewPosition)
    {
        if (Annotations is not { } annotations || Document is not { } document)
        {
            return;
        }

        var (x, y) = ViewToDocument(viewPosition);
        if (FindPageAt(x, y, EraserRadius) is not { } page)
        {
            return;
        }

        var rect = _layout.PageRects[page];
        var scale = document.PageSizes[page].Width / rect.Width;
        var hits = annotations.HitTest(page, (x - rect.X) * scale, (y - rect.Y) * scale, EraserRadius * scale);
        annotations.Remove(hits);
    }

    #endregion

    #region 그리기

    private void DrawInk(DrawingContext dc, int pageIndex, Rect pageRect)
    {
        if (Document is not { } document)
        {
            return;
        }

        // 화면의 비트맵에 아직 없는 주석만 임시로 그린다.
        var shown = ShownRevision(pageIndex);
        if (!_inkDrawings.TryGetValue(pageIndex, out var cached) || cached.ShownRevision != shown)
        {
            // 곱하기 합성을 쓸 수 있으면 형광펜은 형광펜 레이어가 그리므로 여기서는 뺀다.
            var pending = PendingAnnotations(pageIndex, shown);
            _inkDrawings[pageIndex] = cached = new PendingDrawing(shown,
                pending.Count > 0 ? BuildInkDrawing(pending, includeHighlights: !UsesMultiply) : null);
        }

        if (cached.Drawing is not { } drawing)
        {
            return;
        }

        // 포인트 단위로 만든 Drawing을 페이지 위치와 배율에 맞춰 그리고, 페이지 밖으로 나간 부분은 자른다.
        var size = document.PageSizes[pageIndex];
        dc.PushClip(new RectangleGeometry(pageRect));
        dc.PushTransform(new MatrixTransform(pageRect.Width / size.Width, 0, 0, pageRect.Height / size.Height,
            pageRect.X, pageRect.Y));
        dc.DrawDrawing(drawing);
        dc.Pop();
        dc.Pop();
    }

    private void DrawToolOverlay(DrawingContext dc)
    {
        if (ActiveTool == AnnotationTool.Eraser && _eraserPosition is { } position)
        {
            dc.DrawEllipse(EraserBrush, EraserPen, position, EraserRadius, EraserRadius);
        }
    }

    /// <summary>
    /// 페이지의 주석을 포인트 단위 Drawing으로 만든다. 텍스트 형광펜을 먼저 그려서 펜 획이 그 위에 오게 한다.
    /// includeHighlights가 false면 형광펜(텍스트·자유)은 빼고 펜 획만 그린다.
    /// </summary>
    private static Drawing BuildInkDrawing(IReadOnlyList<Annotation> annotations, bool includeHighlights)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            foreach (var highlight in includeHighlights ? annotations.OfType<TextHighlight>() : [])
            {
                // 셰이더를 쓸 수 없을 때: 반투명하게 칠해 아래 글자가 비쳐 보이게 한다.
                var brush = new SolidColorBrush(Color.FromArgb(TextHighlightAlpha,
                    (byte)(highlight.Color >> 16), (byte)(highlight.Color >> 8), (byte)highlight.Color));
                brush.Freeze();
                foreach (var r in highlight.Rects)
                {
                    context.DrawRectangle(brush, null, new Rect(r.X, r.Y, r.Width, r.Height));
                }
            }

            foreach (var stroke in annotations.OfType<InkStroke>().Where(s => includeHighlights || !s.IsHighlighter))
            {
                if (stroke.IsHighlighter)
                {
                    // 셰이더를 쓸 수 없을 때: 형광펜 모양을 반투명하게 칠한다.
                    var brush = new SolidColorBrush(Color.FromArgb(TextHighlightAlpha,
                        (byte)(stroke.Color >> 16), (byte)(stroke.Color >> 8), (byte)stroke.Color));
                    brush.Freeze();
                    context.DrawGeometry(brush, null, InkGeometry.GetGeometry(stroke));
                }
                else
                {
                    // 그리는 중(DrawLivePen)과 같은 모양으로 그려야 손을 뗄 때 모양이 바뀌지 않는다.
                    var brush = new SolidColorBrush(InkGeometry.FromArgb(stroke.Color));
                    brush.Freeze();
                    context.DrawGeometry(brush, null, InkGeometry.GetGeometry(stroke));
                }
            }
        }

        group.Freeze();
        return group;
    }

    #endregion
}
