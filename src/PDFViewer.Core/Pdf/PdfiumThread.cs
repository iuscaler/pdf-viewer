using System.Collections.Concurrent;
using PDFViewer.Core.Pdf.Native;

namespace PDFViewer.Core.Pdf;

/// <summary>
/// PDFium은 스레드 안전하지 않으므로 모든 PDFium 호출을 이 전용 스레드 하나에서 실행한다.
/// 라이브러리 초기화도 이 스레드에서 처음 사용될 때 한 번만 수행된다.
/// </summary>
public static class PdfiumThread
{
    private static readonly BlockingCollection<Action> Queue = new();
    private static readonly Thread Worker;

    static PdfiumThread()
    {
        Worker = new Thread(Run)
        {
            Name = "PDFium",
            IsBackground = true,
        };
        Worker.Start();
    }

    /// <summary>현재 스레드가 PDFium 스레드인지 여부.</summary>
    public static bool IsCurrent => Thread.CurrentThread == Worker;

    public static Task<T> InvokeAsync<T>(Func<T> func)
    {
        // 연속 작업이 PDFium 스레드를 점유하지 않도록 비동기로 실행한다.
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public static Task InvokeAsync(Action action) =>
        InvokeAsync(() =>
        {
            action();
            return true;
        });

    /// <summary>결과를 기다리지 않는 작업 (리소스 해제 등).</summary>
    public static void Post(Action action) => Queue.Add(action);

    private static void Run()
    {
        PdfiumNative.FPDF_InitLibrary();
        foreach (var action in Queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // Post로 넘어온 작업의 예외가 스레드를 죽이지 않도록 한다.
                System.Diagnostics.Debug.WriteLine($"[PDFium] {ex}");
            }
        }
    }
}
