using PDFViewer.Core.Layout;
using PDFViewer.Core.Pdf;

namespace PDFViewer.Core.Tests;

/// <summary>페이지 단위 보기의 세로 스크롤바 범위 (줄마다 한 칸).</summary>
public sealed class PageScrollMapTests
{
    private static readonly PageSize Portrait = new(72, 144); // 배율 1에서 96 x 192 DIP

    private static DocumentLayout Layout(int pages, PageLayout pageLayout = PageLayout.SinglePage) =>
        DocumentLayout.Create(Enumerable.Repeat(Portrait, pages).ToList(), 1,
            new LayoutOptions { PageLayout = pageLayout });

    [Fact]
    public void RowsThatFitTheScreen_TakeOneScreenEach()
    {
        // 줄 높이 192 + 여백 → 화면(500)보다 작으므로 칸마다 500
        var map = new PageScrollMap(Layout(4), 20, 500);

        Assert.Equal(2000, map.Extent);
        Assert.Equal(1000, map.StartOf(2));
        Assert.Equal((0, 0.0), map.Locate(0));
        Assert.Equal((1, 0.0), map.Locate(500));
        Assert.Equal((2, 0.0), map.Locate(1000));
        Assert.Equal((3, 0.0), map.Locate(5000)); // 끝을 넘으면 마지막 줄
    }

    [Fact]
    public void RowsTallerThanTheScreen_CanScrollInsideTheRow()
    {
        // 줄 높이 192 + 여백 20*2 = 232, 화면 100 → 칸 232, 줄 안에서 132까지 스크롤
        var map = new PageScrollMap(Layout(3), 20, 100);

        Assert.Equal(696, map.Extent);
        Assert.Equal((0, 50.0), map.Locate(50));
        Assert.Equal((0, 132.0), map.Locate(200)); // 칸의 끝쪽은 줄 맨 아래로
        Assert.Equal((1, 18.0), map.Locate(250));
        Assert.Equal((2, 132.0), map.Locate(10_000));
    }

    [Fact]
    public void TwoPageView_CountsRowsNotPages()
    {
        var layout = Layout(5, PageLayout.TwoPage); // 2-2-1
        var map = new PageScrollMap(layout, 20, 500);

        Assert.Equal(3, layout.Rows.Count);
        Assert.Equal(1500, map.Extent);
        Assert.Equal(2, layout.GetRowIndexOfPage(4));
        Assert.Equal(1, layout.GetRowIndexOfPage(3));
    }

    [Fact]
    public void EmptyDocument()
    {
        var map = new PageScrollMap(DocumentLayout.Empty, 20, 500);

        Assert.Equal(0, map.Extent);
        Assert.Equal((0, 0.0), map.Locate(100));
    }
}
