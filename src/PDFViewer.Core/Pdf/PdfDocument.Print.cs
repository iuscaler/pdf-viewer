using System.ComponentModel;
using System.Runtime.InteropServices;
using PDFViewer.Core.Pdf.Native;

namespace PDFViewer.Core.Pdf;

/// <summary>인쇄할 내용.</summary>
/// <param name="PrinterName">프린터 이름</param>
/// <param name="DevMode">프린터 설정 (DEVMODEW: 용지, 방향, 매수, 양면 등). null이면 프린터 기본값.</param>
/// <param name="Pages">인쇄할 페이지 번호 (0부터)</param>
/// <param name="JobName">인쇄 대기열에 보이는 이름</param>
/// <param name="OutputFile">프린터 대신 이 파일로 출력한다 (Microsoft Print to PDF 등에서 저장 창 없이 저장할 때)</param>
public sealed record PrintJob(
    string PrinterName, byte[]? DevMode, IReadOnlyList<int> Pages, string JobName, string? OutputFile = null);

/// <summary>
/// 인쇄. PDFium이 프린터의 GDI 장치 컨텍스트에 직접 그려서 글자와 도형이 벡터로 출력된다
/// (페이지를 이미지로 바꿔 보내는 것보다 선명하고 인쇄 데이터도 작다). 주석은 인쇄 플래그가 있는 것만 나온다.
/// </summary>
public sealed partial class PdfDocument
{
    /// <summary>
    /// 페이지들을 인쇄한다. 페이지마다 PDFium 작업을 나눠서 그 사이에 화면 렌더링이 끼어들 수 있다.
    /// 각 페이지는 용지의 인쇄 가능 영역에 비율을 유지한 채 가득 맞추고 가운데에 놓는다.
    /// 페이지와 용지의 가로세로 방향이 다르면 90도 돌려서 크게 인쇄한다.
    /// </summary>
    /// <param name="progress">페이지 하나를 보낼 때마다 지금까지 보낸 페이지 수</param>
    /// <exception cref="Win32Exception">프린터를 열거나 인쇄 작업을 시작하지 못했을 때</exception>
    public async Task PrintAsync(PrintJob job, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var page in job.Pages)
        {
            CheckPage(page);
        }

        var dc = await PdfiumThread.InvokeAsync(() => BeginPrint(job)).ConfigureAwait(false);
        var completed = false;
        try
        {
            for (var i = 0; i < job.Pages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageIndex = job.Pages[i];
                await PdfiumThread.InvokeAsync(() => PrintPage(dc, pageIndex)).ConfigureAwait(false);
                progress?.Report(i + 1);
            }

            completed = true;
        }
        finally
        {
            await PdfiumThread.InvokeAsync(() => EndPrint(dc, completed)).ConfigureAwait(false);
        }
    }

    private static nint BeginPrint(PrintJob job)
    {
        var devModePtr = job.DevMode is { Length: > 0 } ? Marshal.AllocHGlobal(job.DevMode.Length) : 0;
        try
        {
            if (devModePtr != 0)
            {
                Marshal.Copy(job.DevMode!, 0, devModePtr, job.DevMode!.Length);
            }

            // CreateDC는 DEVMODE를 복사해 두므로 바로 해제해도 된다.
            var dc = Gdi32.CreateDC(null, job.PrinterName, null, devModePtr);
            if (dc == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"프린터를 열 수 없습니다: {job.PrinterName}");
            }

            var docName = Marshal.StringToHGlobalUni(job.JobName);
            var output = job.OutputFile is null ? 0 : Marshal.StringToHGlobalUni(job.OutputFile);
            try
            {
                var info = new Gdi32.DOCINFOW
                {
                    cbSize = Marshal.SizeOf<Gdi32.DOCINFOW>(),
                    lpszDocName = docName,
                    lpszOutput = output,
                };
                if (Gdi32.StartDoc(dc, info) <= 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    Gdi32.DeleteDC(dc);
                    throw new Win32Exception(error, "인쇄 작업을 시작하지 못했습니다.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(docName);
                if (output != 0)
                {
                    Marshal.FreeHGlobal(output);
                }
            }

            return dc;
        }
        finally
        {
            if (devModePtr != 0)
            {
                Marshal.FreeHGlobal(devModePtr);
            }
        }
    }

    private void PrintPage(nint dc, int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var areaWidth = Gdi32.GetDeviceCaps(dc, Gdi32.HORZRES);
        var areaHeight = Gdi32.GetDeviceCaps(dc, Gdi32.VERTRES);
        var placement = FitToArea(PageSizes[pageIndex], areaWidth, areaHeight);

        if (Gdi32.StartPage(dc) <= 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "페이지를 인쇄하지 못했습니다.");
        }

        PdfiumNative.FPDF_RenderPage(dc, GetPageHandle(pageIndex), placement.X, placement.Y, placement.Width,
            placement.Height, placement.Rotate, PdfiumNative.FPDF_ANNOT | PdfiumNative.FPDF_PRINTING);

        if (Gdi32.EndPage(dc) <= 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "페이지를 인쇄하지 못했습니다.");
        }
    }

    private static void EndPrint(nint dc, bool completed)
    {
        if (completed)
        {
            Gdi32.EndDoc(dc);
        }
        else
        {
            Gdi32.AbortDoc(dc);
        }

        Gdi32.DeleteDC(dc);
    }

    /// <summary>
    /// 페이지를 인쇄 가능 영역(장치 픽셀)에 놓을 위치. 비율을 유지한 채 가득 맞추고 가운데에 둔다.
    /// 페이지와 영역의 방향(가로/세로)이 다르면 시계 방향으로 90도 돌린다(Rotate = 1).
    /// Width/Height는 돌린 뒤 용지 위에서의 크기다.
    /// </summary>
    internal static PrintPlacement FitToArea(PageSize page, int areaWidth, int areaHeight)
    {
        var rotate = (page.Width > page.Height) != (areaWidth > areaHeight) && page.Width != page.Height;
        var (width, height) = rotate ? (page.Height, page.Width) : (page.Width, page.Height);
        var scale = Math.Min(areaWidth / width, areaHeight / height);
        var w = (int)Math.Floor(width * scale);
        var h = (int)Math.Floor(height * scale);
        return new PrintPlacement((areaWidth - w) / 2, (areaHeight - h) / 2, w, h, rotate ? 1 : 0);
    }

    internal readonly record struct PrintPlacement(int X, int Y, int Width, int Height, int Rotate);
}
