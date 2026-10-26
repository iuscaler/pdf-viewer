using PDFViewer.Core.Caching;
using PDFViewer.Core.Pdf.Native;
using PDFViewer.Core.Rendering;

namespace PDFViewer.Core.Pdf;

/// <summary>
/// 열린 PDF 문서. 네이티브 핸들은 PDFium 스레드에서만 접근하며,
/// 공개 메서드는 작업을 PDFium 스레드로 넘기는 비동기 메서드다.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    // 최근에 렌더링한 페이지 핸들을 유지해서 페이지를 다시 파싱하지 않도록 한다.
    private const int PageHandleCacheSize = 8;

    private readonly LruCache<int, nint> _pageHandles;
    private nint _handle;
    private bool _disposed;

    private PdfDocument(string filePath, nint handle, PageSize[] pageSizes)
    {
        FilePath = filePath;
        _handle = handle;
        PageSizes = pageSizes;
        _pageHandles = new LruCache<int, nint>(
            PageHandleCacheSize, _ => 1, (_, page) => PdfiumNative.FPDF_ClosePage(page));
    }

    public string FilePath { get; }

    public int PageCount => PageSizes.Count;

    /// <summary>각 페이지의 크기 (포인트 단위).</summary>
    public IReadOnlyList<PageSize> PageSizes { get; }

    public static Task<PdfDocument> OpenAsync(string filePath, string? password = null)
    {
        var fullPath = Path.GetFullPath(filePath);
        return PdfiumThread.InvokeAsync(() =>
        {
            var handle = PdfiumNative.FPDF_LoadDocument(fullPath, password);
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

            return new PdfDocument(fullPath, handle, sizes);
        });
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
    /// 문서를 닫는다. 이미 대기열에 들어간 렌더링 작업이 끝난 뒤에 PDFium 스레드에서 해제된다.
    /// </summary>
    public void Dispose()
    {
        PdfiumThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pageHandles.Clear();
            PdfiumNative.FPDF_CloseDocument(_handle);
            _handle = 0;
        });
    }
}
