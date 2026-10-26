using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using PDFViewer.Core.Annotations;
using PDFViewer.Core.Layout;

namespace PDFViewer.App.Controls;

public enum AnnotationTool
{
    /// <summary>주석을 그리지 않는 기본 도구.</summary>
    Select,
    Pen,
    Eraser,
}

/// <summary>
/// 펜 주석 입력과 그리기.
/// </summary>
/// <remarks>
/// 펜 도구일 때는 화면 전체를 덮는 <see cref="InkCanvas"/>가 입력을 받는다. 필압, 곡선 보정, 지연이 적은 실시간 그리기는
/// WPF가 처리한다. 획이 끝나면 페이지 좌표(포인트)로 바꿔 <see cref="AnnotationSet"/>에 넣고 InkCanvas에서는 지운다.
/// 저장된 획은 페이지마다 포인트 단위 Drawing으로 한 번 만들어 두고, 그릴 때 배율과 위치만 변환해서 그린다.
/// </remarks>
public sealed partial class PdfDocumentView
{
    // 지우개 반지름 (화면 DIP). 배율과 상관없이 화면에서 같은 크기로 보인다.
    private const double EraserRadius = 8;

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

    private readonly Dictionary<int, Drawing> _inkDrawings = new();
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
        };
        _inkCanvas.StrokeCollected += OnStrokeCollected;
        AddVisualChild(_inkCanvas);
        UpdateToolState();
    }

    private void OnAnnotationsChanged(AnnotationSet? oldSet, AnnotationSet? newSet)
    {
        if (oldSet is not null)
        {
            oldSet.PageChanged -= OnAnnotationPageChanged;
        }

        if (newSet is not null)
        {
            newSet.PageChanged += OnAnnotationPageChanged;
        }

        _inkDrawings.Clear();
        UpdateToolState();
        InvalidateVisual();
    }

    private void OnAnnotationPageChanged(int pageIndex)
    {
        _inkDrawings.Remove(pageIndex);
        InvalidateVisual();
    }

    private void UpdateToolState()
    {
        var canAnnotate = Annotations is not null && Document is not null;
        _inkCanvas.IsHitTestVisible = canAnnotate && ActiveTool == AnnotationTool.Pen;
        // 지우개는 화면에 원을 직접 그리므로 커서를 숨긴다.
        Cursor = canAnnotate && ActiveTool == AnnotationTool.Eraser ? Cursors.None : null;
        _eraserPosition = null;
        UpdateInkCanvasAttributes();
        InvalidateVisual();
    }

    /// <summary>InkCanvas가 그리는 획의 색과 굵기를 현재 펜 설정과 배율에 맞춘다.</summary>
    private void UpdateInkCanvasAttributes()
    {
        var width = Math.Max(0.5, PenWidth * InkScale);
        var current = _inkCanvas.DefaultDrawingAttributes;
        if (current.Color == PenColor && Math.Abs(current.Width - width) < 0.01)
        {
            return;
        }

        _inkCanvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = PenColor,
            Width = width,
            Height = width,
            FitToCurve = true,
            StylusTip = StylusTip.Ellipse,
        };
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

        annotations.AddStroke(new InkStroke(page, inkPoints, ToArgb(PenColor), PenWidth));
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

    // 펜을 뒤집어 지우개 쪽으로 쓰면 어떤 도구를 선택했든 지운다. InkCanvas보다 먼저 받도록 Preview 이벤트를 쓴다.
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
    }

    protected override void OnPreviewStylusMove(StylusEventArgs e)
    {
        base.OnPreviewStylusMove(e);
        if (_stylusErasing)
        {
            EraseAt(e.GetPosition(this));
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
        annotations.RemoveStrokes(hits);
    }

    #endregion

    #region 그리기

    private void DrawInk(DrawingContext dc, int pageIndex, Rect pageRect)
    {
        if (Annotations?.GetStrokes(pageIndex) is not { Count: > 0 } strokes || Document is not { } document)
        {
            return;
        }

        if (!_inkDrawings.TryGetValue(pageIndex, out var drawing))
        {
            _inkDrawings[pageIndex] = drawing = BuildInkDrawing(strokes);
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

    private static Drawing BuildInkDrawing(IReadOnlyList<InkStroke> strokes)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            foreach (var stroke in strokes)
            {
                ToWpfStroke(stroke).Draw(context);
            }
        }

        group.Freeze();
        return group;
    }

    private static Stroke ToWpfStroke(InkStroke stroke)
    {
        var points = new StylusPointCollection(stroke.Points.Count);
        foreach (var p in stroke.Points)
        {
            points.Add(new StylusPoint(p.X, p.Y, Math.Clamp(p.Pressure, 0f, 1f)));
        }

        return new Stroke(points, new DrawingAttributes
        {
            Color = FromArgb(stroke.Color),
            Width = stroke.Width,
            Height = stroke.Width,
            FitToCurve = true,
            StylusTip = StylusTip.Ellipse,
            IsHighlighter = stroke.IsHighlighter,
        });
    }

    private static uint ToArgb(Color c) => (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);

    private static Color FromArgb(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    #endregion
}
