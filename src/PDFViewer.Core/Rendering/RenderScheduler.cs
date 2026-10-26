using PDFViewer.Core.Pdf;

namespace PDFViewer.Core.Rendering;

/// <summary>
/// 렌더링 요청을 우선순위 순서로 하나씩 PDFium 스레드에 넘긴다.
/// <see cref="Update"/>를 호출할 때마다 대기 목록 전체가 교체되므로,
/// 화면 밖으로 나간 페이지의 요청은 실행되지 않고 버려진다.
/// </summary>
public sealed class RenderScheduler : IDisposable
{
    private readonly PdfDocument _document;
    private readonly Lock _lock = new();
    private List<RenderRequest> _pending = [];
    private RenderRequest? _running;
    private bool _pumping;
    private bool _disposed;

    public RenderScheduler(PdfDocument document)
    {
        _document = document;
    }

    /// <summary>
    /// 렌더링이 끝났을 때 스레드 풀 스레드에서 발생한다.
    /// 처리기가 <see cref="PageBitmap"/>의 소유권을 가지며 사용 후 Dispose해야 한다.
    /// </summary>
    public event Action<RenderRequest, PageBitmap>? Completed;

    /// <summary>렌더링이 실패했을 때 스레드 풀 스레드에서 발생한다.</summary>
    public event Action<RenderRequest, Exception>? Failed;

    /// <summary>대기 중인 요청을 교체한다. 앞에 있는 요청일수록 먼저 실행된다.</summary>
    public void Update(IEnumerable<RenderRequest> requestsInPriorityOrder)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _pending = requestsInPriorityOrder
                .Distinct()
                .Where(r => r != _running)
                .ToList();

            if (!_pumping && _pending.Count > 0)
            {
                _pumping = true;
                _ = PumpAsync();
            }
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            RenderRequest request;
            lock (_lock)
            {
                if (_disposed || _pending.Count == 0)
                {
                    _running = null;
                    _pumping = false;
                    return;
                }

                request = _pending[0];
                _pending.RemoveAt(0);
                _running = request;
            }

            try
            {
                var bitmap = await _document.RenderAsync(request).ConfigureAwait(false);
                bool deliver;
                lock (_lock)
                {
                    deliver = !_disposed;
                }

                if (deliver && Completed is { } completed)
                {
                    completed(request, bitmap);
                }
                else
                {
                    bitmap.Dispose();
                }
            }
            catch (Exception ex)
            {
                bool report;
                lock (_lock)
                {
                    report = !_disposed;
                }

                if (report)
                {
                    Failed?.Invoke(request, ex);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pending.Clear();
        }
    }
}
