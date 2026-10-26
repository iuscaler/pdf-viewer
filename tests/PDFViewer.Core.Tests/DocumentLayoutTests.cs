using PDFViewer.Core.Layout;
using PDFViewer.Core.Pdf;

namespace PDFViewer.Core.Tests;

public sealed class DocumentLayoutTests
{
    private static readonly LayoutOptions Options = new() { PageGap = 10, Margin = 20 };

    // 72pt = 96 DIP (배율 100%)
    private static readonly PageSize Portrait = new(72, 144);
    private static readonly PageSize Wide = new(144, 72);

    [Fact]
    public void Create_StacksPagesVerticallyWithGap()
    {
        var layout = DocumentLayout.Create([Portrait, Portrait], 1, Options);

        Assert.Equal(new LayoutRect(20, 20, 96, 192), layout.PageRects[0]);
        Assert.Equal(new LayoutRect(20, 20 + 192 + 10, 96, 192), layout.PageRects[1]);
        Assert.Equal(96 + 40, layout.ExtentWidth);
        Assert.Equal(20 + 192 + 10 + 192 + 20, layout.ExtentHeight);
    }

    [Fact]
    public void Create_CentersNarrowPagesHorizontally()
    {
        var layout = DocumentLayout.Create([Wide, Portrait], 1, Options);

        Assert.Equal(20, layout.PageRects[0].X);
        Assert.Equal(20 + (192 - 96) / 2.0, layout.PageRects[1].X);
    }

    [Fact]
    public void Create_ScalesPagesButNotGap()
    {
        var layout = DocumentLayout.Create([Portrait, Portrait], 2, Options);

        Assert.Equal(new LayoutRect(20, 20, 192, 384), layout.PageRects[0]);
        Assert.Equal(20 + 384 + 10, layout.PageRects[1].Y);
    }

    [Fact]
    public void Create_Empty()
    {
        var layout = DocumentLayout.Create([], 1, Options);

        Assert.Equal(0, layout.PageCount);
        Assert.Equal((0, -1), layout.GetPagesInRange(0, 100));
        Assert.Equal(-1, layout.GetPageAt(0));
    }

    [Theory]
    [InlineData(0, 10, 0, -1)] // 위쪽 여백만
    [InlineData(0, 100, 0, 0)]
    [InlineData(100, 230, 0, 1)] // 1페이지 끝과 2페이지 시작에 걸침
    [InlineData(213, 215, 0, -1)] // 페이지 사이 간격만
    [InlineData(300, 10000, 1, 2)]
    public void GetPagesInRange(double top, double bottom, int first, int last)
    {
        // 페이지 y 범위: [20,212) [222,414) [424,616)
        var layout = DocumentLayout.Create([Portrait, Portrait, Portrait], 1, Options);

        Assert.Equal((first, last), layout.GetPagesInRange(top, bottom));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 0)]
    [InlineData(215, 1)] // 간격이면 아래쪽 페이지
    [InlineData(500, 2)]
    [InlineData(99999, 2)]
    public void GetPageAt(double y, int expected)
    {
        var layout = DocumentLayout.Create([Portrait, Portrait, Portrait], 1, Options);

        Assert.Equal(expected, layout.GetPageAt(y));
    }

    [Fact]
    public void Anchor_RoundTripsAcrossZoom()
    {
        var before = DocumentLayout.Create([Portrait, Portrait, Portrait], 1, Options);
        var anchor = before.GetAnchor(20 + 48, 222 + 96); // 2페이지 정중앙

        Assert.Equal(new PageAnchor(1, 0.5, 0.5), anchor);

        var after = DocumentLayout.Create([Portrait, Portrait, Portrait], 3, Options);
        var (x, y) = after.GetPoint(anchor);
        var rect = after.PageRects[1];
        Assert.Equal(rect.X + rect.Width / 2, x, 6);
        Assert.Equal(rect.Y + rect.Height / 2, y, 6);
    }

    [Fact]
    public void AnchorInSpecificPage_CanBeOutsidePage()
    {
        var layout = DocumentLayout.Create([Portrait, Portrait], 1, Options);

        // 2페이지 위쪽 간격의 점을 2페이지 기준으로 표현하면 Y가 음수
        var anchor = layout.GetAnchor(1, 20 + 48, 222 - 5);

        Assert.Equal(1, anchor.PageIndex);
        Assert.Equal(0.5, anchor.X, 6);
        Assert.True(anchor.Y < 0);
    }

    [Fact]
    public void RowBounds_WrapsPagesOfRow()
    {
        var layout = DocumentLayout.Create([Wide, Portrait], 1, Options);

        Assert.Equal(layout.PageRects[1], layout.GetRowBounds(layout.GetRowOfPage(1)));
    }

    [Fact]
    public void FitZoom_SinglePage()
    {
        // 화면 236 x 236, 여백 20 → 사용 가능 영역 196 x 196
        Assert.Equal(196 / 96.0, DocumentLayout.GetFitZoom([Portrait], 0, ZoomMode.FitWidth, 236, 236, Options));
        Assert.Equal(196 / 192.0, DocumentLayout.GetFitZoom([Portrait], 0, ZoomMode.FitPage, 236, 236, Options));
        Assert.Null(DocumentLayout.GetFitZoom([Portrait], 0, ZoomMode.Custom, 236, 236, Options));
    }

    [Fact]
    public void FitZoom_TwoPage_FitsBothPagesAndGap()
    {
        var options = Options with { PageLayout = PageLayout.TwoPage };

        // 사용 가능 너비 196 - 간격 10 = 186 을 두 페이지(96 x 2)가 나눠 쓴다.
        Assert.Equal(186 / 192.0,
            DocumentLayout.GetFitZoom([Portrait, Portrait], 1, ZoomMode.FitWidth, 236, 1000, options));
    }

    // 2페이지 보기 테스트용: 72pt = 96 DIP 정사각형 페이지, 간격 10, 여백 20
    // 문서 너비 = 96*2 + 10 + 40 = 242, 가운데 선 x = 121
    private static readonly PageSize Square = new(72, 72);
    private const double Spine = 121;

    [Fact]
    public void TwoPage_PairsPagesAroundSpine()
    {
        var options = Options with { PageLayout = PageLayout.TwoPage };
        var layout = DocumentLayout.Create([Square, Square, Square], 1, options);

        Assert.Equal(242, layout.ExtentWidth);
        Assert.Equal(2, layout.Rows.Count);
        Assert.Equal(new LayoutRect(Spine - 5 - 96, 20, 96, 96), layout.PageRects[0]); // 왼쪽
        Assert.Equal(new LayoutRect(Spine + 5, 20, 96, 96), layout.PageRects[1]); // 오른쪽
        // 마지막 홀수 페이지는 왼쪽 자리에 혼자
        Assert.Equal(new LayoutRect(Spine - 5 - 96, 20 + 96 + 10, 96, 96), layout.PageRects[2]);
    }

    [Fact]
    public void TwoPage_CoverSeparately_PutsCoverAloneOnRight()
    {
        var options = Options with { PageLayout = PageLayout.TwoPage, ShowCoverSeparately = true };
        var layout = DocumentLayout.Create([Square, Square, Square], 1, options);

        Assert.Equal(new LayoutRow(0, 1, 20, 96), layout.Rows[0]);
        Assert.Equal(new LayoutRow(1, 2, 126, 96), layout.Rows[1]);
        Assert.Equal(Spine + 5, layout.PageRects[0].X); // 표지는 오른쪽
        Assert.Equal(Spine - 5 - 96, layout.PageRects[1].X); // 1페이지는 왼쪽
        Assert.Equal(Spine + 5, layout.PageRects[2].X);
    }

    [Fact]
    public void TwoPage_RightToLeft_MirrorsPages()
    {
        var options = Options with { PageLayout = PageLayout.TwoPage, RightToLeft = true };
        var layout = DocumentLayout.Create([Square, Square], 1, options);

        Assert.Equal(Spine + 5, layout.PageRects[0].X); // 앞 페이지가 오른쪽
        Assert.Equal(Spine - 5 - 96, layout.PageRects[1].X);
    }

    [Fact]
    public void TwoPage_CentersShorterPageVertically()
    {
        var options = Options with { PageLayout = PageLayout.TwoPage };
        var layout = DocumentLayout.Create([Portrait, Square], 1, options); // 높이 192, 96

        Assert.Equal(192, layout.Rows[0].Height);
        Assert.Equal(20 + (192 - 96) / 2.0, layout.PageRects[1].Y);
    }

    [Fact]
    public void TwoPage_AnchorPicksPageUnderPoint()
    {
        var options = Options with { PageLayout = PageLayout.TwoPage };
        var layout = DocumentLayout.Create([Square, Square], 1, options);

        Assert.Equal(0, layout.GetAnchor(Spine - 50, 50).PageIndex);
        Assert.Equal(1, layout.GetAnchor(Spine + 50, 50).PageIndex);
    }

    [Theory]
    [InlineData(false, 0, 0, 2)]
    [InlineData(false, 3, 2, 2)]
    [InlineData(false, 4, 4, 1)] // 5페이지 문서의 마지막 페이지
    [InlineData(true, 0, 0, 1)]
    [InlineData(true, 1, 1, 2)]
    [InlineData(true, 2, 1, 2)]
    [InlineData(true, 4, 3, 2)]
    public void GetRowRange_TwoPage(bool cover, int page, int first, int count)
    {
        var options = Options with { PageLayout = PageLayout.TwoPage, ShowCoverSeparately = cover };

        Assert.Equal((first, count), DocumentLayout.GetRowRange(page, 5, options));
    }

    [Fact]
    public void GetRowRange_SinglePage_IgnoresCover()
    {
        var options = Options with { ShowCoverSeparately = true };

        Assert.Equal((3, 1), DocumentLayout.GetRowRange(3, 5, options));
    }
}
