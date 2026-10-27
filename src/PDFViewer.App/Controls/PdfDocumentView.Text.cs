using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PDFViewer.App.Annotations;
using PDFViewer.App.Diagnostics;
using PDFViewer.Core.Annotations;
using PDFViewer.Core.Pdf;
using PDFViewer.Core.Text;

namespace PDFViewer.App.Controls;

/// <summary>
/// 텍스트 선택(선택 도구)과 텍스트 형광펜(형광펜 도구).
/// </summary>
/// <remarks>
/// 선택/형광펜 도구일 때 화면에 보이는 페이지의 글자 위치를 미리 읽어 둔다. 텍스트 위를 누르면 Preview 이벤트에서
/// 먼저 받아 InkCanvas로 가지 않게 하고 글자 단위로 선택한다. 텍스트가 없는 곳은 형광펜 도구라면 InkCanvas가
/// 받아 자유 형광펜이 된다.
/// </remarks>
public sealed partial class PdfDocumentView
{
    // 글자 정보를 들고 있을 최대 페이지 수. 넘으면 화면 밖 페이지부터 버린다.
    private const int MaxCachedPageTexts = 64;

    private static readonly Brush SelectionBrush = CreateFrozenBrush(Color.FromArgb(0x50, 0x00, 0x67, 0xC0));

    public static readonly DependencyProperty HighlighterColorProperty = DependencyProperty.Register(
        nameof(HighlighterColor), typeof(Color), typeof(PdfDocumentView),
        new PropertyMetadata(Color.FromRgb(0xFF, 0xEB, 0x3B), (d, _) => ((PdfDocumentView)d).UpdateInkCanvasAttributes()));

    public static readonly DependencyProperty HighlighterWidthProperty = DependencyProperty.Register(
        nameof(HighlighterWidth), typeof(double), typeof(PdfDocumentView),
        new PropertyMetadata(12.0, (d, _) => ((PdfDocumentView)d).UpdateInkCanvasAttributes()));

    // 페이지별 글자 정보. 값이 null이면 읽는 중이다.
    private readonly Dictionary<int, PageText?> _pageTexts = new();

    private TextSelection? _selection;
    private bool _textSelecting;
    private bool _stylusSelecting;

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

    /// <summary>선택한 텍스트. 없으면 빈 문자열.</summary>
    public string SelectedText =>
        _selection is { } s && _pageTexts.GetValueOrDefault(s.Page) is { } text ? text.GetText(s.Anchor, s.Focus) : "";

    private bool UsesText => ActiveTool is AnnotationTool.Select or AnnotationTool.Highlighter;

    #region 글자 정보 읽기

    private void ResetPageTexts()
    {
        _pageTexts.Clear();
        _selection = null;
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
        PageText text;
        try
        {
            text = await document.GetTextAsync(pageIndex);
        }
        catch (Exception)
        {
            // 글자를 읽지 못하는 페이지는 텍스트가 없는 페이지로 취급한다.
            text = PageText.Empty;
        }

        // UI 스레드에서 이어서 실행된다. 그 사이 다른 문서를 열었으면 버린다.
        if (version == _documentVersion)
        {
            _pageTexts[pageIndex] = text;
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
        if (TryBeginTextSelection(position) && CaptureMouse())
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

    /// <summary>선택/형광펜 도구에서 글자 위를 눌렀으면 선택을 시작한다.</summary>
    private bool TryBeginTextSelection(Point viewPosition)
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
        if (text.CharAt(px, py) is not { } index)
        {
            return false;
        }

        _selection = new TextSelection(page, index, index);
        _textSelecting = true;
        InvalidateVisual();
        return true;
    }

    /// <summary>드래그 중. 선택을 시작한 페이지 안에서 가장 가까운 글자까지 넓힌다.</summary>
    private void UpdateTextSelection(Point viewPosition)
    {
        if (_selection is not { } selection || _pageTexts.GetValueOrDefault(selection.Page) is not { } text)
        {
            return;
        }

        var (x, y) = ViewToDocument(viewPosition);
        var (px, py) = DocumentToPage(selection.Page, x, y);
        if (text.NearestChar(px, py) is { } focus && focus != selection.Focus)
        {
            _selection = selection with { Focus = focus };
            InvalidateVisual();
        }
    }

    /// <summary>
    /// 드래그가 끝났을 때. 형광펜 도구면 선택한 텍스트를 칠하고 선택을 지운다.
    /// 선택 도구면 선택을 남겨 둔다(Ctrl+C로 복사). 글자 하나만 누르고 뗀 경우는 선택하지 않은 것으로 본다.
    /// </summary>
    private void EndTextSelection()
    {
        if (!_textSelecting)
        {
            return;
        }

        _textSelecting = false;
        if (_selection is not { } selection || selection.Anchor == selection.Focus)
        {
            ClearTextSelection();
            return;
        }

        if (IsHighlighterTool && Annotations is { } annotations
            && _pageTexts.GetValueOrDefault(selection.Page) is { } text)
        {
            var rects = text.GetSelectionRects(selection.Anchor, selection.Focus);
            if (rects.Count > 0)
            {
                annotations.Add(new TextHighlight(selection.Page, rects, InkGeometry.ToArgb(HighlighterColor),
                    text.GetText(selection.Anchor, selection.Focus)));
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

    /// <summary>선택한 텍스트를 클립보드에 복사한다. 복사했으면 true.</summary>
    public bool CopySelectedText()
    {
        var text = SelectedText;
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

    private void DrawTextSelection(DrawingContext dc, int pageIndex, Rect pageRect)
    {
        if (_selection is not { } selection || selection.Page != pageIndex
            || _pageTexts.GetValueOrDefault(pageIndex) is not { } text || Document is not { } document)
        {
            return;
        }

        if (IsHighlighterTool && UsesMultiply)
        {
            return; // 형광펜으로 끄는 중인 미리보기는 형광펜 레이어가 곱하기 합성으로 그린다.
        }

        var brush = IsHighlighterTool
            ? new SolidColorBrush(Color.FromArgb(TextHighlightAlpha, HighlighterColor.R, HighlighterColor.G,
                HighlighterColor.B))
            : SelectionBrush;
        var size = document.PageSizes[pageIndex];
        var sx = pageRect.Width / size.Width;
        var sy = pageRect.Height / size.Height;
        foreach (var r in text.GetSelectionRects(selection.Anchor, selection.Focus))
        {
            dc.DrawRectangle(brush, null,
                new Rect(pageRect.X + r.X * sx, pageRect.Y + r.Y * sy, r.Width * sx, r.Height * sy));
        }
    }

    /// <summary>선택 범위. Anchor는 처음 누른 글자, Focus는 드래그로 넓힌 쪽 끝 글자.</summary>
    private readonly record struct TextSelection(int Page, int Anchor, int Focus);
}
