using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFViewer.Core.Caching;
using PDFViewer.Core.Pdf;
using PDFViewer.Core.Rendering;

namespace PDFViewer.App.Controls;

/// <summary>
/// 페이지 썸네일 하나. 화면에 나타나면 렌더링하고(가상화된 목록에서 쓴다), 사라지면 아직 시작하지 않은 렌더링을 취소한다.
/// <see cref="Revision"/>이 바뀌면(주석 변경) 다시 그린다. 렌더링 결과는 문서별 LRU 캐시에 둔다.
/// </summary>
public sealed class PageThumbnail : Image
{
    // 문서별 썸네일 캐시 (페이지, 리비전, 폭) → 비트맵. 문서가 사라지면 캐시도 함께 사라진다.
    private const long CacheBytesPerDocument = 64L * 1024 * 1024;
    private static readonly ConditionalWeakTable<PdfDocument, LruCache<(int Page, int Revision, int Width), BitmapSource>>
        Caches = new();

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(PdfDocument), typeof(PageThumbnail),
        new PropertyMetadata(null, (d, _) => ((PageThumbnail)d).Refresh()));

    public static readonly DependencyProperty PageIndexProperty = DependencyProperty.Register(
        nameof(PageIndex), typeof(int), typeof(PageThumbnail),
        new PropertyMetadata(-1, (d, _) => ((PageThumbnail)d).Refresh()));

    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(PageThumbnail),
        new PropertyMetadata(0, (d, _) => ((PageThumbnail)d).Refresh()));

    private CancellationTokenSource? _cancel;

    public PageThumbnail()
    {
        Stretch = Stretch.Fill;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => Cancel();
    }

    public PdfDocument? Document
    {
        get => (PdfDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public int PageIndex
    {
        get => (int)GetValue(PageIndexProperty);
        set => SetValue(PageIndexProperty, value);
    }

    /// <summary>페이지 내용 버전. 주석이 바뀔 때마다 올라간다.</summary>
    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    private async void Refresh()
    {
        Cancel();
        if (!IsLoaded || Document is not { } document || PageIndex < 0 || PageIndex >= document.PageCount
            || ActualWidth <= 0 && Width is double.NaN or <= 0)
        {
            return;
        }

        var size = document.PageSizes[PageIndex];
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var width = Math.Max(1, (int)Math.Round((double.IsNaN(Width) ? ActualWidth : Width) * dpi));
        var height = Math.Max(1, (int)Math.Round(width * size.Height / size.Width));
        var key = (PageIndex, Revision, width);
        var cache = Caches.GetValue(document,
            _ => new LruCache<(int, int, int), BitmapSource>(CacheBytesPerDocument,
                b => (long)b.PixelWidth * b.PixelHeight * 4));

        if (cache.TryGetValue(key, out var cached))
        {
            Source = cached;
            return;
        }

        var cancel = _cancel = new CancellationTokenSource();
        try
        {
            using var bitmap = await document.RenderAsync(RenderRequest.FullPage(PageIndex, width, height),
                cancel.Token);
            var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgr32, null,
                bitmap.Buffer, bitmap.Stride);
            source.Freeze();
            cache.Set(key, source);
            if (!cancel.IsCancellationRequested)
            {
                Source = source;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or PdfException)
        {
            // 화면에서 사라졌거나 문서가 닫혔거나 그릴 수 없는 페이지면 그리지 않는다.
        }
    }

    private void Cancel()
    {
        _cancel?.Cancel();
        _cancel = null;
    }
}
