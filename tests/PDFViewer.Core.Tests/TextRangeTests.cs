using PDFViewer.Core.Layout;
using PDFViewer.Core.Text;

namespace PDFViewer.Core.Tests;

/// <summary>여러 페이지에 걸친 텍스트 선택과 단어 선택.</summary>
public sealed class TextRangeTests
{
    // 한 줄짜리 페이지. 글자 폭 10, 높이 10.
    private static PageText Line(string s) =>
        new([.. s.Select((c, i) => new TextChar(c, new LayoutRect(i * 10, 0, 10, 10), false))]);

    [Fact]
    public void StartAndEnd_AreOrderedAcrossPages()
    {
        var range = new TextRange(new TextPosition(2, 1), new TextPosition(0, 5));

        Assert.Equal(new TextPosition(0, 5), range.Start);
        Assert.Equal(new TextPosition(2, 1), range.End);
        Assert.Equal([0, 1, 2], range.Pages);
    }

    [Fact]
    public void OnPage_SplitsIntoFirstMiddleAndLastPage()
    {
        var range = new TextRange(new TextPosition(1, 3), new TextPosition(3, 2));

        Assert.Null(range.OnPage(0, 10));
        Assert.Equal((3, 9), range.OnPage(1, 10)); // 첫 페이지: 시작 글자부터 끝까지
        Assert.Equal((0, 4), range.OnPage(2, 5)); // 가운데 페이지: 전체
        Assert.Equal((0, 2), range.OnPage(3, 10)); // 마지막 페이지: 처음부터 끝 글자까지
        Assert.Null(range.OnPage(4, 10));
        Assert.Null(range.OnPage(2, 0)); // 글자 없는 페이지
    }

    [Fact]
    public void GetText_JoinsPagesWithLineBreaks()
    {
        var pages = new[] { Line("Hello"), PageText.Empty, Line("World"), Line("Again") };
        var range = new TextRange(new TextPosition(0, 1), new TextPosition(3, 2));

        Assert.Equal("ello\r\nWorld\r\nAga", range.GetText(p => pages[p]));
    }

    [Fact]
    public void All_CoversWholeDocument()
    {
        var pages = new[] { Line("ab"), Line("cde") };
        var all = TextRange.All(pages.Length);

        Assert.Equal((0, 2), all.OnPage(1, 3)); // 마지막 페이지의 끝 글자로 맞춰진다
        Assert.Equal("ab\r\ncde", all.GetText(p => pages[p]));
    }

    [Theory]
    [InlineData(0, 0, 4)] // "Hello"
    [InlineData(3, 0, 4)]
    [InlineData(5, 5, 5)] // 공백은 그 글자 하나
    [InlineData(8, 6, 10)] // "안녕123" (한글과 숫자도 단어)
    [InlineData(11, 11, 11)] // 문장 부호
    public void WordAt_ExpandsToLetterAndDigitBoundaries(int index, int start, int end)
    {
        var text = Line("Hello 안녕123!");

        Assert.Equal((start, end), text.WordAt(index));
    }

    [Fact]
    public void EmptyPage_ReturnsNoRectsOrText()
    {
        Assert.Empty(PageText.Empty.GetSelectionRects(0, 5));
        Assert.Equal("", PageText.Empty.GetText(0, 5));
    }
}
