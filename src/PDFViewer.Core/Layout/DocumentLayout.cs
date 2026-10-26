using PDFViewer.Core.Pdf;

namespace PDFViewer.Core.Layout;

public sealed record LayoutOptions
{
    /// <summary>페이지 사이 간격 (DIP). 배율과 상관없이 일정하다.</summary>
    public double PageGap { get; init; } = 8;

    /// <summary>문서 바깥 여백 (DIP).</summary>
    public double Margin { get; init; } = 8;

    public PageLayout PageLayout { get; init; } = PageLayout.SinglePage;

    /// <summary>2페이지 보기에서 첫 페이지(표지)를 혼자 오른쪽에 놓는다. 1페이지 보기에서는 무시한다.</summary>
    public bool ShowCoverSeparately { get; init; }

    /// <summary>오른쪽에서 왼쪽으로 읽는 책 (만화, 세로쓰기). 2페이지 보기에서 앞 페이지가 오른쪽에 온다.</summary>
    public bool RightToLeft { get; init; }

    public bool IsTwoPage => PageLayout == PageLayout.TwoPage;
}

/// <summary>한 줄에 놓이는 페이지 묶음. 1페이지 보기에서는 한 줄에 한 페이지가 놓인다.</summary>
public readonly record struct LayoutRow(int FirstPage, int PageCount, double Top, double Height)
{
    public double Bottom => Top + Height;

    public int LastPage => FirstPage + PageCount - 1;
}

/// <summary>페이지 안의 상대 위치 (0~1). 배율이 바뀌어도 같은 지점을 가리키는 데 쓴다.</summary>
public readonly record struct PageAnchor(int PageIndex, double X, double Y);

/// <summary>
/// 페이지 크기와 배율로 각 페이지가 놓일 위치(문서 좌표, DIP)를 계산한다.
/// UI에 의존하지 않는 순수 로직이다.
/// </summary>
/// <remarks>
/// 모든 줄은 문서 가운데의 세로선(책의 접히는 부분)을 기준으로 놓인다.
/// 1페이지 보기에서는 페이지 가운데가 이 선에 오고, 2페이지 보기에서는 왼쪽 페이지의 오른쪽 끝과
/// 오른쪽 페이지의 왼쪽 끝이 이 선을 사이에 두고 마주 본다. 한 줄에 페이지가 하나뿐이어도
/// (표지, 홀수 페이지 문서의 마지막 페이지) 펼친 책에서의 자기 자리에 놓인다.
/// </remarks>
public sealed class DocumentLayout
{
    /// <summary>1pt(1/72인치)를 DIP(1/96인치)로 바꾸는 비율. 배율 100%일 때의 실제 크기.</summary>
    public const double PointsToDip = 96.0 / 72.0;

    private readonly LayoutRect[] _pageRects;
    private readonly LayoutRow[] _rows;
    private readonly int[] _rowOfPage;

    private DocumentLayout(LayoutRect[] pageRects, LayoutRow[] rows, int[] rowOfPage, double extentWidth,
        double extentHeight, double zoom)
    {
        _pageRects = pageRects;
        _rows = rows;
        _rowOfPage = rowOfPage;
        ExtentWidth = extentWidth;
        ExtentHeight = extentHeight;
        Zoom = zoom;
    }

    public static DocumentLayout Empty { get; } = new([], [], [], 0, 0, 1);

    public double Zoom { get; }

    public double ExtentWidth { get; }

    public double ExtentHeight { get; }

    public IReadOnlyList<LayoutRect> PageRects => _pageRects;

    public IReadOnlyList<LayoutRow> Rows => _rows;

    public int PageCount => _pageRects.Length;

    /// <summary>레이아웃을 만든다.</summary>
    /// <param name="zoom">배율 (1 = 100%)</param>
    public static DocumentLayout Create(IReadOnlyList<PageSize> pageSizes, double zoom, LayoutOptions options)
    {
        if (pageSizes.Count == 0)
        {
            return Empty;
        }

        var scale = zoom * PointsToDip;
        var maxWidth = pageSizes.Max(s => s.Width) * scale;
        var gap = options.PageGap;
        var twoPage = options.IsTwoPage;

        var extentWidth = (twoPage ? maxWidth * 2 + gap : maxWidth) + options.Margin * 2;
        var spine = extentWidth / 2;

        var rects = new LayoutRect[pageSizes.Count];
        var rows = new List<LayoutRow>();
        var rowOfPage = new int[pageSizes.Count];

        var y = options.Margin;
        foreach (var (first, count) in GetRowRanges(pageSizes.Count, options))
        {
            var rowHeight = 0.0;
            for (var i = first; i < first + count; i++)
            {
                rowHeight = Math.Max(rowHeight, pageSizes[i].Height * scale);
            }

            for (var i = first; i < first + count; i++)
            {
                var width = pageSizes[i].Width * scale;
                var height = pageSizes[i].Height * scale;
                double x;
                if (!twoPage)
                {
                    x = spine - width / 2;
                }
                else if (IsLeftPage(i, options))
                {
                    x = spine - gap / 2 - width;
                }
                else
                {
                    x = spine + gap / 2;
                }

                // 크기가 다른 페이지가 한 줄에 있으면 세로 가운데에 맞춘다.
                rects[i] = new LayoutRect(x, y + (rowHeight - height) / 2, width, height);
                rowOfPage[i] = rows.Count;
            }

            rows.Add(new LayoutRow(first, count, y, rowHeight));
            y += rowHeight + gap;
        }

        var extentHeight = y - gap + options.Margin;
        return new DocumentLayout(rects, rows.ToArray(), rowOfPage, extentWidth, extentHeight, zoom);
    }

    /// <summary>
    /// 2페이지 보기에서 페이지가 펼친 책의 왼쪽에 오는지 여부.
    /// 표지를 따로 보면 표지(0)가 오른쪽, 1이 왼쪽, 2가 오른쪽… 순서가 된다. 우→좌면 좌우가 바뀐다.
    /// </summary>
    private static bool IsLeftPage(int pageIndex, LayoutOptions options)
    {
        var leftParity = options.ShowCoverSeparately ? 1 : 0;
        return (pageIndex % 2 == leftParity) != options.RightToLeft;
    }

    /// <summary>각 줄에 놓이는 페이지 범위 (첫 페이지, 페이지 수). 배율과 상관없다.</summary>
    public static IEnumerable<(int First, int Count)> GetRowRanges(int pageCount, LayoutOptions options)
    {
        var i = 0;
        while (i < pageCount)
        {
            var range = GetRowRange(i, pageCount, options);
            yield return range;
            i = range.First + range.Count;
        }
    }

    /// <summary>페이지가 들어 있는 줄의 페이지 범위 (첫 페이지, 페이지 수).</summary>
    public static (int First, int Count) GetRowRange(int pageIndex, int pageCount, LayoutOptions options)
    {
        if (!options.IsTwoPage)
        {
            return (pageIndex, 1);
        }

        int first;
        if (options.ShowCoverSeparately)
        {
            if (pageIndex == 0)
            {
                return (0, 1);
            }

            first = pageIndex - (pageIndex - 1) % 2;
        }
        else
        {
            first = pageIndex - pageIndex % 2;
        }

        return (first, Math.Min(2, pageCount - first));
    }

    /// <summary>
    /// 배율 모드에 맞는 배율. 페이지가 들어 있는 줄 전체가 화면에 맞도록 계산한다.
    /// 사용자 지정 모드면 null.
    /// </summary>
    public static double? GetFitZoom(IReadOnlyList<PageSize> pageSizes, int pageIndex, ZoomMode mode,
        double viewportWidth, double viewportHeight, LayoutOptions options)
    {
        if (mode == ZoomMode.Custom || pageSizes.Count == 0)
        {
            return null;
        }

        var (first, count) = GetRowRange(Math.Clamp(pageIndex, 0, pageSizes.Count - 1), pageSizes.Count, options);
        double width = 0, height = 0;
        for (var i = first; i < first + count; i++)
        {
            width = Math.Max(width, pageSizes[i].Width);
            height = Math.Max(height, pageSizes[i].Height);
        }

        // 2페이지 보기에서는 가운데 선 양쪽에 페이지 자리가 하나씩 있다.
        var columns = options.IsTwoPage ? 2 : 1;
        var gaps = options.IsTwoPage ? options.PageGap : 0;
        var fitWidth = Math.Max(0, viewportWidth - options.Margin * 2 - gaps) / (width * columns * PointsToDip);
        if (mode == ZoomMode.FitWidth)
        {
            return fitWidth;
        }

        var fitHeight = Math.Max(0, viewportHeight - options.Margin * 2) / (height * PointsToDip);
        return Math.Min(fitWidth, fitHeight);
    }

    /// <summary>세로 범위 [top, bottom)과 겹치는 페이지 범위. 없으면 (0, -1).</summary>
    public (int First, int Last) GetPagesInRange(double top, double bottom)
    {
        if (_rows.Length == 0 || bottom <= top)
        {
            return (0, -1);
        }

        var firstRow = FindRowAtOrAfter(top);
        if (firstRow >= _rows.Length || _rows[firstRow].Top >= bottom)
        {
            return (0, -1);
        }

        var lastRow = firstRow;
        while (lastRow + 1 < _rows.Length && _rows[lastRow + 1].Top < bottom)
        {
            lastRow++;
        }

        return (_rows[firstRow].FirstPage, _rows[lastRow].LastPage);
    }

    /// <summary>
    /// 세로 위치 y에 있는 줄의 첫 페이지. 페이지 사이 간격이나 여백이면 가장 가까운 아래쪽 줄을,
    /// 마지막 줄보다 아래면 마지막 줄을 기준으로 한다.
    /// </summary>
    public int GetPageAt(double y)
    {
        if (_rows.Length == 0)
        {
            return -1;
        }

        var row = Math.Min(FindRowAtOrAfter(y), _rows.Length - 1);
        return _rows[row].FirstPage;
    }

    public LayoutRow GetRowOfPage(int pageIndex) => _rows[_rowOfPage[pageIndex]];

    /// <summary>줄에 놓인 페이지들을 모두 감싸는 사각형.</summary>
    public LayoutRect GetRowBounds(LayoutRow row)
    {
        var left = double.MaxValue;
        var right = double.MinValue;
        for (var i = row.FirstPage; i <= row.LastPage; i++)
        {
            left = Math.Min(left, _pageRects[i].X);
            right = Math.Max(right, _pageRects[i].Right);
        }

        return new LayoutRect(left, row.Top, right - left, row.Height);
    }

    /// <summary>문서 좌표를 가장 가까운 페이지 기준 상대 위치로 바꾼다.</summary>
    public PageAnchor GetAnchor(double x, double y)
    {
        var first = GetPageAt(y);
        if (first < 0)
        {
            return new PageAnchor(0, 0, 0);
        }

        // 한 줄에 페이지가 여럿이면 x에 가장 가까운 페이지를 고른다.
        var row = GetRowOfPage(first);
        var page = first;
        var bestDistance = double.MaxValue;
        for (var i = row.FirstPage; i <= row.LastPage; i++)
        {
            var rect = _pageRects[i];
            var distance = x < rect.X ? rect.X - x : x > rect.Right ? x - rect.Right : 0;
            if (distance < bestDistance)
            {
                page = i;
                bestDistance = distance;
            }
        }

        return GetAnchor(page, x, y);
    }

    /// <summary>문서 좌표를 지정한 페이지 기준 상대 위치로 바꾼다. 페이지 밖이면 0~1 범위를 벗어난다.</summary>
    public PageAnchor GetAnchor(int pageIndex, double x, double y)
    {
        var rect = _pageRects[pageIndex];
        return new PageAnchor(pageIndex, (x - rect.X) / rect.Width, (y - rect.Y) / rect.Height);
    }

    /// <summary>페이지 기준 상대 위치를 문서 좌표로 바꾼다.</summary>
    public (double X, double Y) GetPoint(PageAnchor anchor)
    {
        if (_pageRects.Length == 0)
        {
            return (0, 0);
        }

        var rect = _pageRects[Math.Clamp(anchor.PageIndex, 0, _pageRects.Length - 1)];
        return (rect.X + anchor.X * rect.Width, rect.Y + anchor.Y * rect.Height);
    }

    // 아래쪽 끝이 y보다 큰 첫 번째 줄 (이진 탐색)
    private int FindRowAtOrAfter(double y)
    {
        int lo = 0, hi = _rows.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_rows[mid].Bottom <= y)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
