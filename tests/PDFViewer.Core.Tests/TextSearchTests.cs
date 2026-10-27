using PDFViewer.Core.Layout;
using PDFViewer.Core.Pdf;
using PDFViewer.Core.Text;

namespace PDFViewer.Core.Tests;

public sealed class TextSearchTests
{
    // "Hello World" 첫 줄, PDFium이 넣은 줄바꿈, "hello again" 둘째 줄. 글자 폭 10, 줄 높이 10.
    private static PageText Sample()
    {
        var chars = new List<TextChar>();
        void Line(string s, double y)
        {
            for (var i = 0; i < s.Length; i++)
            {
                chars.Add(new TextChar(s[i], new LayoutRect(i * 10, y, 10, 10), false));
            }
        }

        Line("Hello World", 0);
        chars.Add(new TextChar('\r', default, true));
        chars.Add(new TextChar('\n', default, true));
        Line("hello again", 20);
        return new PageText(chars);
    }

    [Fact]
    public void FindsAllMatches_IgnoringCase()
    {
        var matches = TextSearch.Find(Sample(), 3, "HELLO");

        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.Equal(3, m.PageIndex));
        Assert.Equal((0, 4), (matches[0].StartChar, matches[0].EndChar));
        Assert.Equal((13, 17), (matches[1].StartChar, matches[1].EndChar));
        Assert.Equal([new LayoutRect(0, 20, 50, 10)], matches[1].Rects);
    }

    [Fact]
    public void FindsAcrossLineBreak()
    {
        // 줄바꿈(\r\n)을 공백 하나로 본다.
        var matches = TextSearch.Find(Sample(), 0, "world   hello");

        var match = Assert.Single(matches);
        Assert.Equal((6, 17), (match.StartChar, match.EndChar));
        Assert.Equal(2, match.Rects.Count); // 두 줄
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nothing")]
    public void NoMatch(string query)
    {
        Assert.Empty(TextSearch.Find(Sample(), 0, query));
    }

    [Fact]
    public void Korean()
    {
        var chars = "가나다 가나".Select((c, i) => new TextChar(c, new LayoutRect(i * 10, 0, 10, 10), false)).ToList();

        Assert.Equal(2, TextSearch.Find(new PageText(chars), 0, "가나").Count);
    }

    [Fact]
    public async Task FindsInRealPdf()
    {
        var path = TestPdf.Create(new TestPdf.Page(595, 842,
            "BT /F1 12 Tf 72 700 Td (The quick brown fox) Tj 0 -20 Td (jumps over the fox) Tj ET"));
        try
        {
            await using var doc = await PdfDocument.OpenAsync(path);
            var text = await doc.GetTextAsync(0);

            Assert.Equal(2, TextSearch.Find(text, 0, "fox").Count);
            Assert.Single(TextSearch.Find(text, 0, "fox jumps")); // 줄을 넘어감
        }
        finally
        {
            File.Delete(path);
        }
    }
}
