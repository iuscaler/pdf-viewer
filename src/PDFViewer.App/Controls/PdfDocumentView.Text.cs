using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PDFViewer.App.Annotations;
using PDFViewer.App.Diagnostics;
using PDFViewer.Core.Annotations;
using PDFViewer.Core.Layout;
using PDFViewer.Core.Pdf;
using PDFViewer.Core.Text;

namespace PDFViewer.App.Controls;

/// <summary>
/// 텍스트 선택(선택 도구)과 텍스트 형광펜(형광펜 도구).
/// </summary>
/// <remarks>
/// 선택/형광펜 도구일 때 화면에 보이는 페이지의 글자 위치를 미리 읽어 둔다. 텍스트 위를 누르면 Preview 이벤트에서
/// 먼저 받아 InkCanvas로 가지 않게 하고 글자 단위로 선택한다. 텍스트가 없는 곳은 형광펜 도구라면 InkCanvas가
/// 받아 자유 형광펜이 된다. 선택은 여러 페이지에 걸칠 수 있다: 드래그하면 마우스에서 가장 가까운 페이지의 글자까지
/// 넓히고, 화면 위아래 밖으로 끌면 그쪽으로 스크롤한다. 더블클릭은 단어를, Ctrl+A는 문서 전체를 선택한다.
/// </remarks>
public sealed partial class PdfDocumentView
{
    // 글자 정보를 들고 있을 최대 페이지 수. 넘으면 화면 밖 페이지부터 버린다.
    private const int MaxCachedPageTexts = 64;

    // 선택하면서 화면 밖으로 끌 때 자동 스크롤 간격과 한 번에 스크롤하는 최대 거리 (DIP)
    private static readonly TimeSpan SelectionScrollInterval = TimeSpan.FromMilliseconds(30);
    private const double MaxSelectionScrollStep = 60;

    private static readonly Brush SelectionBrush = CreateFrozenBrush(Color.FromArgb(0x50, 0x00, 0x67, 0xC0));

    public static readonly DependencyProperty HighlighterColorProperty = DependencyProperty.Register(
        nameof(HighlighterColor), typeof(Color), typeof(PdfDocumentView),
        new PropertyMetadata(Color.FromRgb(0xFF, 0xEB, 0x3B), (d, _) => ((PdfDocumentView)d).UpdateInkCanvasAttributes()));

    public static readonly DependencyProperty HighlighterWidthProperty = DependencyProperty.Register(
        nameof(HighlighterWidth), typeof(double), typeof(PdfDocumentView),
        new PropertyMetadata(12.0, (d, _) => ((PdfDocumentView)d).UpdateInkCanvasAttributes()));

    // 페이지별 글자 정보. 값이 null이면 읽는 중이다.
    private readonly Dictionary<int, PageText?> _pageTexts = new();

    private TextRange? _selection;
    private bool _textSelecting;
    private bool _stylusSelecting;

    // 더블클릭으로 시작한 선택이면 처음 단어. 드래그하면 단어 단위로 넓힌다.
    private TextRange? _wordAnchor;

    // 선택 중 마지막 포인터 위치 (뷰 좌표). 자동 스크롤과 스크롤 뒤 선택 갱신에 쓴다.
    private Point _selectionPoint;
    private DispatcherTimer? _selectionScrollTimer;

    public Color HighlighterColor
    {
        get => (Color)GetValue(HighlighterColorProperty);
        set => SetValue(HighlighterColorProperty, value);
    }

    /// <summary>자유 형광펜 굵기 (포인트).</summary>
    public double HighlighterWidth
    {
        get => (double)GetValue(HighlighterWidthProperty);
        set => SetValue(HighlighterWidthProperty, value);
    }

    /// <summary>선택 도구로 고른 텍스트가 있는지 여부.</summary>
    public bool HasTextSelection => _selection is not null && !_textSelecting;

    private bool UsesText => ActiveTool is AnnotationTool.Select or AnnotationTool.Highlighter;

    #region 글자 정보 읽기

    private void ResetPageTexts()
    {
        _pageTexts.Clear();
        _selection = null;
        _wordAnchor = null;
        _textSelecting = false;
    }

    /// <summary>선택/형광펜 도구일 때 화면에 보이는 페이지의 글자 정보를 읽어 둔다.</summary>
    private void EnsureVisibleTextLoaded()
    {
        if (!UsesText || Document is not { } document || _layout.PageCount == 0)
        {
            return;
        }

        var (first, last) = VisiblePages;
        if (_pageTexts.Count > MaxCachedPageTexts)
        {
            foreach (var page in _pageTexts.Keys.Where(p => p < first || p > last).ToList())
            {
                _pageTexts.Remove(page);
            }
        }

        for (var i = first; i <= last; i++)
        {
            if (!_pageTexts.ContainsKey(i))
            {
                _pageTexts[i] = null;
                _ = LoadPageTextAsync(document, i, _documentVersion);
            }
        }
    }

    private async Task LoadPageTextAsync(PdfDocument document, int pageIndex, int version)
    {
        var text = await ReadPageTextAsync(document, pageIndex);

        // UI 스레드에서 이어서 실행된다. 그 사이 다른 문서를 열었으면 버린다.
        if (version == _documentVersion)
        {
            _pageTexts[pageIndex] = text;
            if (_selection?.ContainsPage(pageIndex) == true)
            {
                InvalidateVisual(); // 전체 선택 뒤 스크롤해서 새로 보인 페이지 등
            }
        }
    }

    private static async Task<PageText> ReadPageTextAsync(PdfDocument document, int pageIndex)
    {
        try
        {
            return await document.GetTextAsync(pageIndex);
        }
        catch (Exception)
        {
            // 글자를 읽지 못하는 페이지는 텍스트가 없는 페이지로 취급한다.
            return PageText.Empty;
        }
    }

    #endregion

    #region 선택

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (e.StylusDevice is not null)
        {
            return; // 펜 입력은 Stylus 이벤트에서 처리한다.
        }

        var position = e.GetPosition(this);
        if (TryBeginTextSelection(position, byWord: e.ClickCount == 2,
                extend: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) && CaptureMouse())
        {
            e.Handled = true;
        }
        else if (TryBeginFreehand(position, MousePressure) && CaptureMouse())
        {
            // 펜, 또는 형광펜 도구에서 텍스트가 아닌 곳: 직접 그리기 (PdfDocumentView.Freehand.cs)
            e.Handled = true;
        }
        else if (ActiveTool == AnnotationTool.Select)
        {
            ClearTextSelection();
        }
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        var position = e.GetPosition(this);
        if (_textSelecting && !_stylusSelecting && e.StylusDevice is null)
        {
            UpdateTextSelection(position);
        }
        else if (_liveStroke is not null && !_stylusDrawing && e.StylusDevice is null)
        {
            if (InputLog.Enabled)
            {
                InputLog.MouseMove(this, e, ViewToLiveStrokePage(position));
            }

            AddFreehandPoint(position, MousePressure);
        }

        UpdateTextCursor(position);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_liveStroke is not null && !_stylusDrawing && e.StylusDevice is null)
        {
            // 캡처를 풀 때 OnLostMouseCapture에서 그린 획이 주석으로 들어간다.
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_textSelecting && !_stylusSelecting && e.StylusDevice is null)
        {
            // 캡처를 풀 때 OnLostMouseCapture에서도 EndTextSelection이 불리지만, 한 번만 처리된다.
            ReleaseMouseCapture();
            EndTextSelection();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 선택/형광펜 도구에서 글자 위를 눌렀으면 선택을 시작한다.
    /// byWord면 누른 단어를 선택하고(더블클릭), extend면 지금 선택의 시작점은 두고 누른 곳까지 넓힌다(Shift+클릭).
    /// </summary>
    private bool TryBeginTextSelection(Point viewPosition, bool byWord = false, bool extend = false)
    {
        if (!UsesText || Document is null || Annotations is null)
        {
            return false;
        }

        var (x, y) = ViewToDocument(viewPosition);
        if (FindPageAt(x, y, 0) is not { } page || _pageTexts.GetValueOrDefault(page) is not { } text)
        {
            return false;
        }

        var (px, py) = DocumentToPage(page, x, y);
        if (extend && ActiveTool == AnnotationTool.Select && _selection is { } current)
        {
            if (text.NearestChar(px, py) is not { } focus)
            {
                return false;
            }

            _selection = current with { Focus = new TextPosition(page, focus) };
            _wordAnchor = null;
        }
        else
        {
            if (text.CharAt(px, py) is not { } index)
            {
                return false;
            }

            if (byWord)
            {
                var (start, end) = text.WordAt(index);
                _wordAnchor = _selection = new TextRange(new TextPosition(page, start), new TextPosition(page, end));
            }
            else
            {
                _wordAnchor = null;
                _selection = new TextRange(new TextPosition(page, index), new TextPosition(page, index));
            }
        }

        _textSelecting = true;
        _selectionPoint = viewPosition;
        _selectionScrollTimer ??= new DispatcherTimer(SelectionScrollInterval, DispatcherPriority.Input,
            OnSelectionScrollTick, Dispatcher);
        _selectionScrollTimer.Start();
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// 드래그 중. 포인터에서 가장 가까운 글자(다른 페이지여도)까지 넓힌다. 더블클릭으로 시작했으면 단어 단위로 넓힌다.
    /// </summary>
    private void UpdateTextSelection(Point viewPosition)
    {
        _selectionPoint = viewPosition;
        if (_selection is not { } selection || NearestTextPosition(viewPosition) is not { } focus)
        {
            return;
        }

        var updated = selection with { Focus = focus };
        if (_wordAnchor is { } word && _pageTexts.GetValueOrDefault(focus.Page) is { } text)
        {
            var (start, end) = text.WordAt(focus.Char);
            updated = focus.CompareTo(word.Start) >= 0
                ? new TextRange(word.Start, focus with { Char = end })
                : new TextRange(word.End, focus with { Char = start });
        }

        if (updated != selection)
        {
            _selection = updated;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// 뷰 좌표에서 가장 가까운 글자. 화면에 보이는 페이지 중 그 점에 가장 가까운 페이지(점이 페이지 안이면 그 페이지)에서
    /// 찾는다. 그 페이지의 글자 정보가 아직 없거나 글자가 없으면 null.
    /// </summary>
    private TextPosition? NearestTextPosition(Point viewPosition)
    {
        var (x, y) = ViewToDocument(viewPosition);
        var (first, last) = VisiblePages;
        int? nearest = null;
        var bestDistance = double.MaxValue;
        for (var i = first; i <= last; i++)
        {
            var r = _layout.PageRects[i];
            var dx = Math.Max(0, Math.Max(r.X - x, x - r.Right));
            var dy = Math.Max(0, Math.Max(r.Y - y, y - r.Bottom));
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                nearest = i;
                bestDistance = distance;
            }
        }

        if (nearest is not { } page || _pageTexts.GetValueOrDefault(page) is not { } text)
        {
            return null;
        }

        var (px, py) = DocumentToPage(page, x, y);
        return text.NearestChar(px, py) is { } index ? new TextPosition(page, index) : null;
    }

    /// <summary>선택 중 포인터가 화면 위나 아래로 벗어나 있으면 그쪽으로 스크롤한다 (벗어난 거리에 비례).</summary>
    private void OnSelectionScrollTick(object? sender, EventArgs e)
    {
        if (!_textSelecting)
        {
            _selectionScrollTimer?.Stop();
            return;
        }

        var y = _selectionPoint.Y;
        var delta = y < 0 ? y : y > ViewportHeight ? y - ViewportHeight : 0;
        if (delta != 0)
        {
            // 스크롤하면 OnViewChanged → UpdateTextSelectionAfterScroll에서 선택도 따라간다.
            SetVerticalOffset(VerticalOffset + Math.Clamp(delta, -MaxSelectionScrollStep, MaxSelectionScrollStep));
        }
    }

    /// <summary>선택하는 동안 스크롤되면(자동 스크롤, 휠) 포인터 아래로 온 글자까지 선택을 넓힌다.</summary>
    private void UpdateTextSelectionAfterScroll()
    {
        if (_textSelecting)
        {
            UpdateTextSelection(_selectionPoint);
        }
    }

    /// <summary>
    /// 드래그가 끝났을 때. 형광펜 도구면 선택한 텍스트를 페이지마다 칠하고 선택을 지운다.
    /// 선택 도구면 선택을 남겨 둔다(Ctrl+C로 복사). 글자 하나만 누르고 뗀 경우는 선택하지 않은 것으로 본다
    /// (더블클릭으로 고른 한 글자 단어는 예외).
    /// </summary>
    private void EndTextSelection()
    {
        if (!_textSelecting)
        {
            return;
        }

        _textSelecting = false;
        _selectionScrollTimer?.Stop();
        var byWord = _wordAnchor is not null;
        _wordAnchor = null;
        if (_selection is not { } selection || (selection.Anchor == selection.Focus && !byWord))
        {
            ClearTextSelection();
            return;
        }

        if (IsHighlighterTool && Annotations is { } annotations)
        {
            var color = InkGeometry.ToArgb(HighlighterColor);
            // 여러 페이지를 칠해도 실행 취소 한 번으로 되돌린다.
            using (annotations.BeginGroup())
            {
                foreach (var page in selection.Pages)
                {
                    if (_pageTexts.GetValueOrDefault(page) is { } text
                        && selection.OnPage(page, text.Chars.Count) is { } range
                        && text.GetSelectionRects(range.From, range.To) is { Count: > 0 } rects)
                    {
                        annotations.Add(new TextHighlight(page, rects, color, text.GetText(range.From, range.To)));
                    }
                }
            }

            ClearTextSelection();
        }

        InvalidateVisual();
    }

    private void ClearTextSelection()
    {
        if (_selection is not null)
        {
            _selection = null;
            InvalidateVisual();
        }
    }

    /// <summary>문서 전체의 텍스트를 선택한다 (Ctrl+A). 선택 도구에서 쓴다.</summary>
    public void SelectAllText()
    {
        if (Document is not { PageCount: > 0 } document)
        {
            return;
        }

        if (_textSelecting)
        {
            ReleaseMouseCapture();
            EndTextSelection();
        }

        _selection = TextRange.All(document.PageCount);
        InvalidateVisual();
    }

    /// <summary>
    /// 선택한 텍스트를 클립보드에 복사한다. 복사했으면 true.
    /// 선택이 아직 글자 정보를 읽지 않은 페이지(전체 선택 등)에 걸쳐 있으면 그 페이지들을 읽은 뒤 복사한다.
    /// </summary>
    public async Task<bool> CopySelectedTextAsync()
    {
        if (_selection is not { } selection || _textSelecting || Document is not { } document)
        {
            return false;
        }

        var version = _documentVersion;
        var texts = new Dictionary<int, PageText>();
        foreach (var page in selection.Pages)
        {
            // 화면에 보이지 않는 페이지는 캐시에 넣지 않는다(전체 선택이면 문서 전체가 되므로).
            texts[page] = _pageTexts.GetValueOrDefault(page) ?? await ReadPageTextAsync(document, page);
            if (version != _documentVersion)
            {
                return false; // 그 사이 다른 문서를 열었다.
            }
        }

        var text = selection.GetText(page => texts[page]);
        if (text.Length == 0)
        {
            return false;
        }

        Clipboard.SetText(text);
        return true;
    }

    /// <summary>선택/형광펜 도구에서 글자 위에 있으면 I자 커서를 보여준다.</summary>
    private void UpdateTextCursor(Point viewPosition)
    {
        var overText = false;
        if (UsesText && Document is not null && !_textSelecting)
        {
            var (x, y) = ViewToDocument(viewPosition);
            if (FindPageAt(x, y, 0) is { } page && _pageTexts.GetValueOrDefault(page) is { } text)
            {
                var (px, py) = DocumentToPage(page, x, y);
                overText = text.CharAt(px, py) is not null;
            }
        }
        else if (_textSelecting)
        {
            overText = true;
        }

        if (ActiveTool == AnnotationTool.Highlighter && UsesMultiply)
        {
            // 형광펜을 이 뷰가 직접 그릴 때: 텍스트 위는 I자, 그 밖은 펜 모양
            Cursor = overText ? Cursors.IBeam : Cursors.Pen;
        }
        else if (ActiveTool == AnnotationTool.Highlighter)
        {
            // InkCanvas가 형광펜 입력을 받을 때는 InkCanvas가 위에 있으므로 InkCanvas의 커서를 바꾼다.
            _inkCanvas.UseCustomCursor = overText;
            _inkCanvas.Cursor = overText ? Cursors.IBeam : null;
        }
        else if (ActiveTool == AnnotationTool.Select)
        {
            Cursor = overText ? Cursors.IBeam : null;
        }
    }

    #endregion

    /// <summary>선택 범위 중 한 페이지에 들어가는 줄 사각형 (페이지 좌표). 그 페이지에 선택이 없으면 null.</summary>
    private IReadOnlyList<LayoutRect>? GetSelectionRectsOnPage(int pageIndex)
    {
        if (_selection is not { } selection || _pageTexts.GetValueOrDefault(pageIndex) is not { } text
            || selection.OnPage(pageIndex, text.Chars.Count) is not { } range)
        {
            return null;
        }

        return text.GetSelectionRects(range.From, range.To);
    }

    private void DrawTextSelection(DrawingContext dc, int pageIndex, Rect pageRect)
    {
        if (IsHighlighterTool && UsesMultiply)
        {
            return; // 형광펜으로 끄는 중인 미리보기는 형광펜 레이어가 곱하기 합성으로 그린다.
        }

        if (GetSelectionRectsOnPage(pageIndex) is not { } rects || Document is not { } document)
        {
            return;
        }

        var brush = IsHighlighterTool
            ? new SolidColorBrush(Color.FromArgb(TextHighlightAlpha, HighlighterColor.R, HighlighterColor.G,
                HighlighterColor.B))
            : SelectionBrush;
        var size = document.PageSizes[pageIndex];
        var sx = pageRect.Width / size.Width;
        var sy = pageRect.Height / size.Height;
        foreach (var r in rects)
        {
            dc.DrawRectangle(brush, null,
                new Rect(pageRect.X + r.X * sx, pageRect.Y + r.Y * sy, r.Width * sx, r.Height * sy));
        }
    }
}
