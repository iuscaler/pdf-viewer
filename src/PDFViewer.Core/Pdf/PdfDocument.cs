using PDFViewer.Core.Caching;
using PDFViewer.Core.Layout;
using PDFViewer.Core.Pdf.Native;
using PDFViewer.Core.Rendering;
using PDFViewer.Core.Text;

namespace PDFViewer.Core.Pdf;

/// <summary>
/// 열린 PDF 문서. 네이티브 핸들은 PDFium 스레드에서만 접근하며,
/// 공개 메서드는 작업을 PDFium 스레드로 넘기는 비동기 메서드다.
/// 주석 편집은 PdfDocument.Annotations.cs, 저장은 PdfDocument.Save.cs에 있다.
/// </summary>
public sealed partial class PdfDocument : IDisposable, IAsyncDisposable
{
    // 최근에 렌더링한 페이지 핸들을 유지해서 페이지를 다시 파싱하지 않도록 한다.
    private const int PageHandleCacheSize = 8;

    // FPDF_PageToDevice의 장치 좌표가 정수라서, 포인트를 100배 한 크기로 변환해 0.01pt 정밀도를 얻는다.
    private const int DevicePrecision = 100;

    private readonly LruCache<int, nint> _pageHandles;
    private readonly FileBuffer _buffer;
    private nint _handle;
    private bool _disposed;

    private PdfDocument(string filePath, nint handle, FileBuffer buffer, PageSize[] pageSizes)
    {
        FilePath = filePath;
        _handle = handle;
        _buffer = buffer;
        PageSizes = pageSizes;
        _pageHandles = new LruCache<int, nint>(
            PageHandleCacheSize, _ => 1, (_, page) => PdfiumNative.FPDF_ClosePage(page));
    }

    /// <summary>문서 파일 경로. 다른 이름으로 저장하면 바뀐다.</summary>
    public string FilePath { get; private set; }

    public int PageCount => PageSizes.Count;

    /// <summary>각 페이지의 크기 (포인트 단위).</summary>
    public IReadOnlyList<PageSize> PageSizes { get; }

    /// <summary>
    /// 파일을 메모리로 읽어서 연다. PDFium이 원본 파일을 열어 두지 않으므로 저장할 때 원본을 바로 교체할 수 있다.
    /// 대신 파일 크기만큼 메모리를 쓴다.
    /// </summary>
    public static async Task<PdfDocument> OpenAsync(string filePath, string? password = null)
    {
        var fullPath = Path.GetFullPath(filePath);
        FileBuffer buffer;
        try
        {
            buffer = await Task.Run(() => FileBuffer.Read(fullPath)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfException(PdfError.File, $"파일을 찾을 수 없거나 열 수 없습니다. ({ex.Message})");
        }

        try
        {
            return await PdfiumThread.InvokeAsync(() =>
            {
                var handle = PdfiumNative.FPDF_LoadMemDocument64(buffer.Pointer, (nuint)buffer.Length, password);
                if (handle == 0)
                {
                    throw PdfException.FromLastError();
                }

                var count = PdfiumNative.FPDF_GetPageCount(handle);
                var sizes = new PageSize[count];
                for (var i = 0; i < count; i++)
                {
                    // 크기를 읽지 못하는 페이지는 A4로 간주한다.
                    sizes[i] = PdfiumNative.FPDF_GetPageSizeByIndexF(handle, i, out var size)
                               && size.Width > 0 && size.Height > 0
                        ? new PageSize(size.Width, size.Height)
                        : new PageSize(595, 842);
                }

                return new PdfDocument(fullPath, handle, buffer, sizes);
            }).ConfigureAwait(false);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>요청한 영역을 렌더링한다. 대기 중에 취소되면 렌더링하지 않는다.</summary>
    public Task<PageBitmap> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(request.PageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(request.PageIndex, PageCount);

        return PdfiumThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Render(request);
        });
    }

    private unsafe PageBitmap Render(RenderRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var page = GetPageHandle(request.PageIndex);
        var region = request.Region;
        var result = new PageBitmap(region.Width, region.Height);
        try
        {
            fixed (byte* pixels = result.Buffer)
            {
                var bitmap = PdfiumNative.FPDFBitmap_CreateEx(
                    region.Width, region.Height, PdfiumNative.FPDFBitmap_BGRx, (nint)pixels, result.Stride);
                if (bitmap == 0)
                {
                    throw new OutOfMemoryException("PDFium 비트맵을 만들 수 없습니다.");
                }

                try
                {
                    PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, region.Width, region.Height, 0xFFFFFFFF);
                    // 페이지 전체 크기 기준으로 원점을 옮겨서 Region 영역만 비트맵에 그려지게 한다.
                    PdfiumNative.FPDF_RenderPageBitmap(
                        bitmap, page, -region.X, -region.Y, request.PageWidth, request.PageHeight,
                        rotate: 0, PdfiumNative.FPDF_ANNOT);
                }
                finally
                {
                    PdfiumNative.FPDFBitmap_Destroy(bitmap);
                }
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>페이지의 글자 목록을 읽는다. 좌표는 페이지 왼쪽 위 기준 포인트(화면에 보이는 방향).</summary>
    public Task<PageText> GetTextAsync(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        return PdfiumThread.InvokeAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var page = GetPageHandle(pageIndex);
            var textPage = PdfiumNative.FPDFText_LoadPage(page);
            if (textPage == 0)
            {
                return PageText.Empty;
            }

            try
            {
                var size = PageSizes[pageIndex];
                var count = PdfiumNative.FPDFText_CountChars(textPage);
                var chars = new TextChar[Math.Max(0, count)];
                for (var i = 0; i < chars.Length; i++)
                {
                    var value = (char)PdfiumNative.FPDFText_GetUnicode(textPage, i);
                    var generated = PdfiumNative.FPDFText_IsGenerated(textPage, i) == 1;
                    var box = PdfiumNative.FPDFText_GetLooseCharBox(textPage, i, out var r)
                        ? PdfRectToPage(page, size, r)
                        : default;
                    chars[i] = new TextChar(value, box, generated);
                }

                return new PageText(chars);
            }
            finally
            {
                PdfiumNative.FPDFText_ClosePage(textPage);
            }
        });
    }

    /// <summary>PDF 좌표 사각형을 페이지 왼쪽 위 기준 포인트 좌표로 바꾼다. 페이지 회전도 반영된다.</summary>
    private static LayoutRect PdfRectToPage(nint page, PageSize size, PdfiumNative.FS_RECTF r)
    {
        var width = (int)Math.Round(size.Width * DevicePrecision);
        var height = (int)Math.Round(size.Height * DevicePrecision);
        PdfiumNative.FPDF_PageToDevice(page, 0, 0, width, height, 0, r.Left, r.Top, out var x1, out var y1);
        PdfiumNative.FPDF_PageToDevice(page, 0, 0, width, height, 0, r.Right, r.Bottom, out var x2, out var y2);
        double left = Math.Min(x1, x2), top = Math.Min(y1, y2);
        return new LayoutRect(left / DevicePrecision, top / DevicePrecision,
            Math.Abs(x2 - x1) / (double)DevicePrecision, Math.Abs(y2 - y1) / (double)DevicePrecision);
    }

    private nint GetPageHandle(int pageIndex)
    {
        if (_pageHandles.TryGetValue(pageIndex, out var page))
        {
            return page;
        }

        page = PdfiumNative.FPDF_LoadPage(_handle, pageIndex);
        if (page == 0)
        {
            throw PdfException.FromLastError();
        }

        _pageHandles.Set(pageIndex, page);
        return page;
    }

    /// <summary>
    /// 문서를 닫는다. 이미 대기열에 들어간 작업이 끝난 뒤에 PDFium 스레드에서 해제된다.
    /// 닫기가 끝나기를 기다려야 하면 <see cref="DisposeAsync"/>를 쓴다.
    /// </summary>
    public void Dispose() => PdfiumThread.Post(Close);

    /// <summary>문서를 닫고, 메모리가 실제로 해제될 때까지 기다린다.</summary>
    public ValueTask DisposeAsync() => new(PdfiumThread.InvokeAsync(Close));

    private void Close()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pageHandles.Clear();
        PdfiumNative.FPDF_CloseDocument(_handle);
        _handle = 0;
        _buffer.Dispose();
    }
}
