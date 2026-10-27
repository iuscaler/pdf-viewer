using PDFViewer.Core.Annotations;
using PDFViewer.Core.Layout;
using PDFViewer.Core.Pdf;
using PDFViewer.Core.Rendering;

namespace PDFViewer.Core.Tests;

/// <summary>앱 주석을 PDF 주석으로 넣고, PDFium 렌더링 결과와 저장 결과를 확인한다.</summary>
public sealed class PdfAnnotationTests : IDisposable
{
    // 595 x 842 페이지. 페이지 좌표(왼쪽 위 원점)로 x 0~100, y 742~842 가 검은 사각형이고 나머지는 흰색.
    private readonly string _path = TestPdf.Create(new TestPdf.Page(595, 842, "0 0 0 rg 0 0 100 100 re f"));
    private readonly List<string> _extraFiles = [];

    private const uint Yellow = 0xFFFFEB3B;
    private const uint Red = 0xFFFF0000;

    public void Dispose()
    {
        File.Delete(_path);
        foreach (var file in _extraFiles)
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task InkHighlighter_UsesMultiply_TextUnderneathStaysBlack()
    {
        await using var doc = await PdfDocument.OpenAsync(_path);
        var stroke = HorizontalStroke(isHighlighter: true, Yellow);

        await doc.AddInkAnnotationAsync(stroke, Band(50, 150, 780, 800));
        using var bitmap = await RenderPage(doc);

        Assert.Equal((0, 0, 0), PixelAt(bitmap, 75, 790)); // 검은 부분은 검정 그대로 (곱하기)
        Assert.Equal((0xFF, 0xEB, 0x3B), PixelAt(bitmap, 125, 790)); // 흰 부분은 노랑
    }

    [Fact]
    public async Task InkPen_CoversContent()
    {
        await using var doc = await PdfDocument.OpenAsync(_path);
        var stroke = HorizontalStroke(isHighlighter: false, Red);

        await doc.AddInkAnnotationAsync(stroke, Band(50, 150, 780, 800));
        using var bitmap = await RenderPage(doc);

        Assert.Equal((0xFF, 0, 0), PixelAt(bitmap, 75, 790)); // 검은 부분도 빨강으로 덮임
        Assert.Equal((0xFF, 0, 0), PixelAt(bitmap, 125, 790));
    }

    [Fact]
    public async Task TextHighlight_UsesMultiply()
    {
        await using var doc = await PdfDocument.OpenAsync(_path);
        var highlight = new TextHighlight(0, [new LayoutRect(50, 760, 100, 20)], Yellow, "text");

        await doc.AddTextHighlightAsync(highlight);
        using var bitmap = await RenderPage(doc);

        var onBlack = PixelAt(bitmap, 75, 770);
        Assert.True(onBlack.R < 40 && onBlack.G < 40 && onBlack.B < 40, $"검은 부분 {onBlack}");
        var onWhite = PixelAt(bitmap, 125, 770);
        Assert.True(onWhite.R > 200 && onWhite.G > 180 && onWhite.B < 120, $"흰 부분 {onWhite}");
    }

    [Fact]
    public async Task Remove_TakesAnnotationOut()
    {
        await using var doc = await PdfDocument.OpenAsync(_path);
        var stroke = HorizontalStroke(isHighlighter: false, Red);
        await doc.AddInkAnnotationAsync(stroke, Band(50, 150, 780, 800));

        Assert.True(await doc.RemoveAnnotationAsync(0, stroke.Id));
        Assert.False(await doc.RemoveAnnotationAsync(0, stroke.Id));

        using var bitmap = await RenderPage(doc);
        Assert.Equal((0xFF, 0xFF, 0xFF), PixelAt(bitmap, 125, 790));
        Assert.Empty(await doc.GetAnnotationNamesAsync(0));
    }

    [Fact]
    public async Task SaveAs_WritesAnnotationsAndKeepsOriginal()
    {
        var copy = NewTempPath();
        var stroke = HorizontalStroke(isHighlighter: true, Yellow);
        var highlight = new TextHighlight(0, [new LayoutRect(200, 100, 50, 10)], Yellow, "hi");
        var originalBytes = await File.ReadAllBytesAsync(_path);

        await using (var doc = await PdfDocument.OpenAsync(_path))
        {
            await doc.AddInkAnnotationAsync(stroke, Band(50, 150, 780, 800));
            await doc.AddTextHighlightAsync(highlight);
            await doc.SaveAsync(copy);
            Assert.Equal(copy, doc.FilePath);
        }

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(_path)); // 원본은 그대로

        await using var reopened = await PdfDocument.OpenAsync(copy);
        var names = await reopened.GetAnnotationNamesAsync(0);
        Assert.Contains(PdfDocument.NameOf(stroke.Id), names);
        Assert.Contains(PdfDocument.NameOf(highlight.Id), names);

        using var bitmap = await RenderPage(reopened);
        Assert.Equal((0, 0, 0), PixelAt(bitmap, 75, 790)); // 다시 열어도 곱하기 합성 유지
        Assert.Equal((0xFF, 0xEB, 0x3B), PixelAt(bitmap, 125, 790));
    }

    [Fact]
    public async Task Save_OverwritesOpenFile()
    {
        var stroke = HorizontalStroke(isHighlighter: false, Red);
        await using (var doc = await PdfDocument.OpenAsync(_path))
        {
            await doc.AddInkAnnotationAsync(stroke, Band(50, 150, 780, 800));
            // 문서가 열려 있는 상태에서 같은 파일에 저장 (메모리에서 열었으므로 가능)
            await doc.SaveAsync(_path);

            // 저장한 뒤에도 계속 편집하고 다시 저장할 수 있다.
            Assert.True(await doc.RemoveAnnotationAsync(0, stroke.Id));
            await doc.SaveAsync(_path);
            Assert.Empty(await doc.GetAnnotationNamesAsync(0));
        }

        await using var reopened = await PdfDocument.OpenAsync(_path);
        Assert.Empty(await reopened.GetAnnotationNamesAsync(0));
        using var bitmap = await RenderPage(reopened);
        Assert.Equal((0xFF, 0xFF, 0xFF), PixelAt(bitmap, 125, 790));
    }

    [Fact]
    public async Task Save_ToMissingFolder_Fails()
    {
        await using var doc = await PdfDocument.OpenAsync(_path);

        await Assert.ThrowsAnyAsync<IOException>(
            () => doc.SaveAsync(Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid(), "a.pdf")));
        Assert.Equal(Path.GetFullPath(_path), doc.FilePath);
    }

    private string NewTempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfviewer-test-{Guid.NewGuid():N}.pdf");
        _extraFiles.Add(path);
        return path;
    }

    // y=790 가로선 획 (x 50~150)
    private static InkStroke HorizontalStroke(bool isHighlighter, uint color) =>
        new(0, [new InkPoint(50, 790, 0.5f), new InkPoint(150, 790, 0.5f)], color, 20, isHighlighter);

    // 직사각형 윤곽선 하나
    private static IReadOnlyList<IReadOnlyList<PagePoint>> Band(double left, double right, double top, double bottom) =>
    [
        [new PagePoint(left, top), new PagePoint(right, top), new PagePoint(right, bottom), new PagePoint(left, bottom)],
    ];

    private static Task<PageBitmap> RenderPage(PdfDocument doc) => doc.RenderAsync(RenderRequest.FullPage(0, 595, 842));

    // BGRx 픽셀을 (R, G, B)로
    private static (int R, int G, int B) PixelAt(PageBitmap bitmap, int x, int y)
    {
        var i = y * bitmap.Stride + x * 4;
        return (bitmap.Buffer[i + 2], bitmap.Buffer[i + 1], bitmap.Buffer[i]);
    }
}
