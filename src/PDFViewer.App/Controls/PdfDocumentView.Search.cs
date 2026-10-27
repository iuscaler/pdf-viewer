using System.Windows;
using System.Windows.Media;
using PDFViewer.Core.Layout;
using PDFViewer.Core.Text;

namespace PDFViewer.App.Controls;

/// <summary>검색 결과 표시와, 현재 결과로 스크롤하기.</summary>
public sealed partial class PdfDocumentView
{
    private static readonly Brush SearchMatchBrush = CreateFrozenBrush(Color.FromArgb(0x55, 0xFF, 0xA0, 0x00));
    private static readonly Brush CurrentSearchMatchBrush = CreateFrozenBrush(Color.FromArgb(0x80, 0xFF, 0x60, 0x00));
    private static readonly Pen CurrentSearchMatchPen = CreateFrozenPen(Color.FromRgb(0xE0, 0x50, 0x00), 1.5);

    public static readonly DependencyProperty SearchResultsProperty = DependencyProperty.Register(
        nameof(SearchResults), typeof(IReadOnlyList<SearchMatch>), typeof(PdfDocumentView),
        new PropertyMetadata(null, (d, _) => ((PdfDocumentView)d).OnSearchResultsChanged()));

    public static readonly DependencyProperty CurrentSearchMatchProperty = DependencyProperty.Register(
        nameof(CurrentSearchMatch), typeof(SearchMatch), typeof(PdfDocumentView),
        new PropertyMetadata(null, (d, _) => ((PdfDocumentView)d).OnCurrentSearchMatchChanged()));

    // 페이지별 검색 결과
    private ILookup<int, SearchMatch> _searchMatchesByPage = Array.Empty<SearchMatch>().ToLookup(m => m.PageIndex);

    public IReadOnlyList<SearchMatch>? SearchResults
    {
        get => (IReadOnlyList<SearchMatch>?)GetValue(SearchResultsProperty);
        set => SetValue(SearchResultsProperty, value);
    }

    public SearchMatch? CurrentSearchMatch
    {
        get => (SearchMatch?)GetValue(CurrentSearchMatchProperty);
        set => SetValue(CurrentSearchMatchProperty, value);
    }

    private void OnSearchResultsChanged()
    {
        _searchMatchesByPage = (SearchResults ?? []).ToLookup(m => m.PageIndex);
        InvalidateVisual();
    }

    private void OnCurrentSearchMatchChanged()
    {
        if (CurrentSearchMatch is { } match)
        {
            ScrollIntoView(match.PageIndex, Bounds(match.Rects));
        }

        InvalidateVisual();
    }

    /// <summary>
    /// 페이지 안의 영역(포인트)이 화면에 보이도록 스크롤한다. 이미 다 보이면 그대로 두고, 아니면 화면 가운데에 둔다.
    /// 페이지 단위 모드에서는 그 페이지로 넘어간다.
    /// </summary>
    private void ScrollIntoView(int pageIndex, LayoutRect area)
    {
        if (Document is not { } document || pageIndex < 0 || pageIndex >= _layout.PageCount)
        {
            return;
        }

        var (first, last) = WindowPages;
        if (IsPageMode && (pageIndex < first || pageIndex > last))
        {
            ScrollToPage(pageIndex);
        }

        var page = _layout.PageRects[pageIndex];
        var scale = page.Width / document.PageSizes[pageIndex].Width;
        var target = new LayoutRect(page.X + area.X * scale, page.Y + area.Y * scale, area.Width * scale,
            area.Height * scale);
        var visible = VisibleDocumentRect;
        var window = Window;

        if (target.Y < visible.Y || target.Bottom > visible.Bottom)
        {
            _offsetY = target.Y + target.Height / 2 - window.Y + CenterOffsetY - _viewport.Height / 2;
        }

        if (target.X < visible.X || target.Right > visible.Right)
        {
            _offsetX = target.X + target.Width / 2 - window.X + CenterOffsetX - _viewport.Width / 2;
        }

        ClampOffsets();
        OnViewChanged();
    }

    private void DrawSearchMatches(DrawingContext dc, int pageIndex, Rect pageRect)
    {
        if (!_searchMatchesByPage.Contains(pageIndex) || Document is not { } document)
        {
            return;
        }

        var size = document.PageSizes[pageIndex];
        var sx = pageRect.Width / size.Width;
        var sy = pageRect.Height / size.Height;
        foreach (var match in _searchMatchesByPage[pageIndex])
        {
            var isCurrent = ReferenceEquals(match, CurrentSearchMatch);
            foreach (var r in match.Rects)
            {
                var rect = new Rect(pageRect.X + r.X * sx, pageRect.Y + r.Y * sy, r.Width * sx, r.Height * sy);
                dc.DrawRectangle(isCurrent ? CurrentSearchMatchBrush : SearchMatchBrush,
                    isCurrent ? CurrentSearchMatchPen : null, rect);
            }
        }
    }

    private static LayoutRect Bounds(IReadOnlyList<LayoutRect> rects)
    {
        double left = rects.Min(r => r.X), top = rects.Min(r => r.Y);
        return new LayoutRect(left, top, rects.Max(r => r.Right) - left, rects.Max(r => r.Bottom) - top);
    }
}
