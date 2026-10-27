using PDFViewer.Core.Annotations;
using PDFViewer.Core.Layout;
using PDFViewer.Core.Pdf;
using PDFViewer.Core.Text;

namespace PDFViewer.Core.Tests;

public sealed class PageTextTests
{
    // 두 줄짜리 가짜 텍스트: "ab" (y 0~10), "cd" (y 20~30). 글자 폭 10.
    private static readonly PageText TwoLines = new(
    [
        new TextChar('a', new LayoutRect(0, 0, 10, 10), false),
        new TextChar('b', new LayoutRect(10, 0, 10, 10), false),
        new TextChar('\r', default, true),
        new TextChar('\n', default, true),
        new TextChar('c', new LayoutRect(0, 20, 10, 10), false),
        new TextChar('d', new LayoutRect(10, 20, 10, 10), false),
    ]);

    [Theory]
    [InlineData(5, 5, 0)]
    [InlineData(15, 5, 1)]
    [InlineData(15, 25, 5)]
    public void CharAt_FindsCharUnderPoint(double x, double y, int expected)
    {
        Assert.Equal(expected, TwoLines.CharAt(x, y));
    }

    [Fact]
    public void CharAt_ReturnsNullOutsideText()
    {
        Assert.Null(TwoLines.CharAt(50, 50));
        Assert.Null(TwoLines.CharAt(5, 15)); // 줄 사이
    }

    [Theory]
    [InlineData(100, 5, 1)] // 첫 줄 오른쪽 멀리 → 첫 줄 마지막 글자
    [InlineData(-50, 26, 4)] // 둘째 줄 왼쪽 멀리 → 둘째 줄 첫 글자
    [InlineData(15, 12, 1)] // 줄 사이지만 첫 줄에 더 가까움
    public void NearestChar_PrefersSameLine(double x, double y, int expected)
    {
        Assert.Equal(expected, TwoLines.NearestChar(x, y));
    }

    [Fact]
    public void SelectionRects_OnePerLine()
    {
        var rects = TwoLines.GetSelectionRects(1, 4); // b ~ c

        Assert.Equal([new LayoutRect(10, 0, 10, 10), new LayoutRect(0, 20, 10, 10)], rects);
    }

    [Fact]
    public void SelectionRects_ReversedRangeIsSame()
    {
        Assert.Equal(TwoLines.GetSelectionRects(0, 5), TwoLines.GetSelectionRects(5, 0));
        Assert.Equal(2, TwoLines.GetSelectionRects(0, 5).Count);
    }

    [Fact]
    public void GetText_IncludesGeneratedLineBreaks()
    {
        Assert.Equal("ab\r\ncd", TwoLines.GetText(0, 5));
        Assert.Equal("b", TwoLines.GetText(1, 1));
    }

    [Fact]
    public void TextHighlight_HitTestAndBounds()
    {
        var highlight = new TextHighlight(0, TwoLines.GetSelectionRects(0, 5), 0xFFFFFF00, "ab cd");

        Assert.Equal(new LayoutRect(0, 0, 20, 30), highlight.Bounds);
        Assert.True(highlight.HitTest(5, 5, 0));
        Assert.False(highlight.HitTest(5, 15, 0)); // 줄 사이
        Assert.True(highlight.HitTest(5, 15, 6)); // 지우개 반지름으로 닿음
    }

    [Fact]
    public async Task GetTextAsync_ReadsCharsInTopLeftCoordinates()
    {
        // 아래에서 700pt (위에서 142pt) 위치에 12pt 글자
        var path = TestPdf.Create(new TestPdf.Page(595, 842, "BT /F1 12 Tf 72 700 Td (Hello World) Tj ET"));
        try
        {
            await using var doc = await PdfDocument.OpenAsync(path);
            var text = await doc.GetTextAsync(0);

            Assert.Equal("Hello World", text.GetText(0, text.Chars.Count - 1));
            var h = text.Chars[0].Box;
            Assert.InRange(h.X, 71, 73);
            Assert.True(h.Y < 142 && h.Bottom > 140, $"H 상자 {h}");
            Assert.Equal(0, text.CharAt(h.X + h.Width / 2, h.Y + h.Height / 2));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GetTextAsync_AppliesPageRotation()
    {
        // 세로 페이지를 90도 돌리면 화면에서는 가로 842 x 세로 595.
        // 원래 왼쪽 아래 근처의 글자는 돌린 뒤 왼쪽 위 근처에 온다.
        var path = TestPdf.Create(new TestPdf.Page(595, 842, "BT /F1 12 Tf 20 20 Td (A) Tj ET", Rotate: 90));
        try
        {
            await using var doc = await PdfDocument.OpenAsync(path);
            Assert.Equal(new PageSize(842, 595), doc.PageSizes[0]);

            var box = (await doc.GetTextAsync(0)).Chars[0].Box;
            Assert.True(box.X < 40 && box.Y < 40, $"A 상자 {box}");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
