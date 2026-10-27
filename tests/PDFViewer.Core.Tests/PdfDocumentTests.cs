using PDFViewer.Core.Pdf;
using PDFViewer.Core.Rendering;

namespace PDFViewer.Core.Tests;

public sealed class PdfDocumentTests : IDisposable
{
    // 1페이지: A4 세로, 왼쪽 아래 (0,0)~(100,100)pt 검은 사각형 / 2페이지: 가로 방향
    private readonly string _path = TestPdf.Create(
        new TestPdf.Page(595, 842, "0 0 0 rg 0 0 100 100 re f"),
        new TestPdf.Page(842, 595));

    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task Open_ReadsPageCountAndSizes()
    {
        await using var doc = await PdfDocument.OpenAsync(_path);

        Assert.Equal(2, doc.PageCount);
        Assert.Equal(new PageSize(595, 842), doc.PageSizes[0]);
        Assert.Equal(new PageSize(842, 595), doc.PageSizes[1]);
    }

    [Fact]
    public async Task Open_MissingFile_ThrowsFileError()
    {
        var ex = await Assert.ThrowsAsync<PdfException>(
            () => PdfDocument.OpenAsync(Path.Combine(Path.GetTempPath(), "no-such-file.pdf")));

        Assert.Equal(PdfError.File, ex.Error);
    }

    [Fact]
    public async Task Open_NotPdf_ThrowsFormatError()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "this is not a pdf");
        try
        {
            var ex = await Assert.ThrowsAsync<PdfException>(() => PdfDocument.OpenAsync(path));
            Assert.Equal(PdfError.Format, ex.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Render_FullPage_DrawsContent()
    {
        await using var doc = await PdfDocument.OpenAsync(_path);

        // 1pt = 1px 로 렌더링. 검은 사각형은 왼쪽 아래 100x100 영역.
        using var bitmap = await doc.RenderAsync(RenderRequest.FullPage(0, 595, 842));

        Assert.Equal(595, bitmap.Width);
        Assert.Equal(842, bitmap.Height);
        Assert.Equal(0, PixelAt(bitmap, 50, 842 - 50)); // 검은 사각형 안
        Assert.Equal(255, PixelAt(bitmap, 300, 300)); // 흰 배경
    }

    [Fact]
    public async Task Render_Region_DrawsOnlyThatPart()
    {
        await using var doc = await PdfDocument.OpenAsync(_path);

        // 페이지 왼쪽 아래 200x200 영역만 렌더링
        var request = new RenderRequest(0, 595, 842, new PixelRect(0, 842 - 200, 200, 200));
        using var bitmap = await doc.RenderAsync(request);

        Assert.Equal(200, bitmap.Width);
        Assert.Equal(200, bitmap.Height);
        Assert.Equal(0, PixelAt(bitmap, 50, 150)); // 사각형 안
        Assert.Equal(255, PixelAt(bitmap, 150, 50)); // 사각형 밖
    }

    [Fact]
    public async Task Render_CanceledBeforeStart_Throws()
    {
        await using var doc = await PdfDocument.OpenAsync(_path);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => doc.RenderAsync(RenderRequest.FullPage(0, 10, 10), cts.Token));
    }

    [Fact]
    public async Task Scheduler_RendersRequestsInOrder()
    {
        await using var doc = await PdfDocument.OpenAsync(_path);
        using var scheduler = new RenderScheduler(doc);
        var done = new List<int>();
        var allDone = new TaskCompletionSource();
        scheduler.Completed += (request, bitmap) =>
        {
            bitmap.Dispose();
            lock (done)
            {
                done.Add(request.PageIndex);
                if (done.Count == 2)
                {
                    allDone.SetResult();
                }
            }
        };

        scheduler.Update([RenderRequest.FullPage(1, 50, 50), RenderRequest.FullPage(0, 50, 50)]);
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([1, 0], done);
    }

    // BGRx 픽셀의 파란색 채널 값
    private static byte PixelAt(PageBitmap bitmap, int x, int y) => bitmap.Buffer[y * bitmap.Stride + x * 4];
}
