using PDFViewer.Core.Annotations;
using PDFViewer.Core.Pdf;
using PDFViewer.Core.Rendering;

namespace PDFViewer.Core.Tests;

/// <summary>인쇄: 용지에 맞추는 계산, 그리고 Microsoft Print to PDF로 실제 인쇄한 결과.</summary>
public sealed class PrintTests : IDisposable
{
    private const string PdfPrinter = "Microsoft Print to PDF";

    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void FitToArea_PortraitPageOnPortraitPaper_FillsWidthAndCenters()
    {
        // A4(595x842) 페이지를 4800x6600 픽셀 영역에: 폭 기준 배율 8.067, 높이 6792 > 6600이므로 높이 기준
        var p = PdfDocument.FitToArea(new PageSize(595, 842), 4800, 6600);

        Assert.Equal(0, p.Rotate);
        Assert.Equal(6600, p.Height);
        Assert.InRange(p.Width, 4663, 4665);
        Assert.Equal((4800 - p.Width) / 2, p.X);
        Assert.Equal(0, p.Y);
    }

    [Fact]
    public void FitToArea_LandscapePageOnPortraitPaper_RotatesToFill()
    {
        var p = PdfDocument.FitToArea(new PageSize(842, 595), 4800, 6600);

        Assert.Equal(1, p.Rotate);
        Assert.True(p.Height > p.Width, "돌린 뒤에는 세로로 길어야 한다");
        Assert.Equal(6600, p.Height);
    }

    [Fact]
    public void FitToArea_SquarePage_DoesNotRotate()
    {
        var p = PdfDocument.FitToArea(new PageSize(500, 500), 4800, 6600);

        Assert.Equal(0, p.Rotate);
        Assert.Equal(4800, p.Width);
        Assert.Equal(4800, p.Height);
        Assert.Equal(900, p.Y);
    }

    [Fact]
    public async Task PrintToPdf_PrintsSelectedPagesWithTextAndAnnotations()
    {
        if (!PrinterExists(PdfPrinter))
        {
            return; // 이 PC에 PDF 프린터가 없으면 확인할 수 없다.
        }

        var source = Track(TestPdf.Create(
            new TestPdf.Page(595, 842, "BT /F1 24 Tf 72 700 Td (Hello Print) Tj ET"),
            new TestPdf.Page(595, 842, "BT /F1 24 Tf 72 700 Td (Skipped Page) Tj ET"),
            new TestPdf.Page(842, 595, "BT /F1 24 Tf 72 500 Td (Wide Page) Tj ET")));
        var output = Track(Path.Combine(Path.GetTempPath(), $"pdfviewer-print-{Guid.NewGuid():N}.pdf"));

        await using (var doc = await PdfDocument.OpenAsync(source))
        {
            Assert.True(doc.AllowsPrinting);
            // 첫 페이지에 굵은 빨간 펜 획 (저장하지 않은 주석도 인쇄되어야 한다)
            var stroke = new InkStroke(0, [new InkPoint(100, 300, 0.5f), new InkPoint(400, 300, 0.5f)], 0xFFFF0000, 20);
            await doc.AddInkAnnotationAsync(stroke, Band(stroke));

            var printed = new List<int>();
            await doc.PrintAsync(new PrintJob(PdfPrinter, null, [0, 2], "PDFViewer 인쇄 테스트", output),
                new SyncProgress(printed.Add));
            Assert.Equal([1, 2], printed);
        }

        await WaitForFileAsync(output);
        await using var result = await PdfDocument.OpenAsync(output);
        Assert.Equal(2, result.PageCount);

        // 글자는 PDFium이 윤곽선(벡터)으로 보내서 파일이 작다. 이미지로 인쇄했다면 수백 KB 이상이다.
        Assert.True(new FileInfo(output).Length < 100_000, $"벡터로 인쇄되어야 한다 ({new FileInfo(output).Length} 바이트)");

        // 가로 페이지도 세로 용지에 돌려서 인쇄된다.
        Assert.True(result.PageSizes[1].Height > result.PageSizes[1].Width);

        // 첫 페이지: 검은 글자와 빨간 주석, 둘째 페이지(가로 페이지): 검은 글자
        var (black, red) = await CountColorsAsync(result, 0);
        Assert.True(black > 100, $"글자가 보여야 한다 ({black})");
        Assert.True(red > 100, $"빨간 주석이 보여야 한다 ({red})");
        Assert.True((await CountColorsAsync(result, 1)).Black > 100, "둘째 페이지 글자가 보여야 한다");
    }

    private static async Task<(int Black, int Red)> CountColorsAsync(PdfDocument doc, int page)
    {
        var size = doc.PageSizes[page];
        using var bitmap = await doc.RenderAsync(RenderRequest.FullPage(page, (int)size.Width, (int)size.Height));
        int black = 0, red = 0;
        for (var i = 0; i < bitmap.Buffer.Length; i += 4)
        {
            var (b, g, r) = (bitmap.Buffer[i], bitmap.Buffer[i + 1], bitmap.Buffer[i + 2]);
            if (r < 80 && g < 80 && b < 80)
            {
                black++;
            }
            else if (r > 200 && g < 60 && b < 60)
            {
                red++;
            }
        }

        return (black, red);
    }

    [Fact]
    public async Task EncryptedPdf_ReportsPrintPermission()
    {
        var path = Track(TestPdf.CreateEncrypted("pw", new TestPdf.Page(595, 842)));
        await using var doc = await PdfDocument.OpenAsync(path, "pw");

        Assert.True(doc.AllowsPrinting); // 시험 파일은 /P -4 (모든 권한)
    }

    private string Track(string path)
    {
        _files.Add(path);
        return path;
    }

    private static bool PrinterExists(string name)
    {
        var dc = PDFViewer.Core.Pdf.Native.Gdi32.CreateDC(null, name, null, 0);
        if (dc == 0)
        {
            return false;
        }

        PDFViewer.Core.Pdf.Native.Gdi32.DeleteDC(dc);
        return true;
    }

    // 프린터 드라이버가 파일을 다 쓰고 닫을 때까지 잠깐 걸릴 수 있다.
    private static async Task WaitForFileAsync(string path)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    return;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(100);
        }

        Assert.Fail($"인쇄 결과 파일이 만들어지지 않았습니다: {path}");
    }

    private static IReadOnlyList<IReadOnlyList<PagePoint>> Band(InkStroke stroke)
    {
        var r = stroke.Bounds;
        return [[new PagePoint(r.X, r.Y), new PagePoint(r.Right, r.Y), new PagePoint(r.Right, r.Bottom), new PagePoint(r.X, r.Bottom)]];
    }

    /// <summary>Progress&lt;T&gt;와 달리 보고한 스레드에서 바로 호출한다 (테스트에서 순서를 확인하려고).</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
