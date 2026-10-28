using System.ComponentModel;
using System.IO;
using System.Printing;
using System.Printing.Interop;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFViewer.Core.Pdf;

namespace PDFViewer.App.ViewModels;

/// <summary>인쇄 (Ctrl+P).</summary>
/// <remarks>
/// Windows 인쇄 대화 상자에서 프린터, 페이지 범위(전체 / 현재 페이지 / 지정 범위), 매수, 용지 등을 고른다.
/// 실제 인쇄는 PDFium이 프린터에 직접 벡터로 그린다(PdfDocument.Print.cs). 저장하지 않은 주석도 함께 인쇄된다.
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>인쇄 중이면 몇 쪽째인지 ("인쇄 중 3/10"). 아니면 null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrinting))]
    private string? _printStatus;

    public bool IsPrinting => PrintStatus is not null;

    // 인쇄 중에는 다시 누를 수 없다 (비동기 명령이 실행 중이면 CanExecute가 false가 된다).
    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private async Task PrintAsync()
    {
        if (Document is not { } document)
        {
            return;
        }

        if (!document.AllowsPrinting)
        {
            MessageBox.Show("이 문서는 작성자가 인쇄를 허용하지 않았습니다.", AppName, MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new PrintDialog
        {
            MinPage = 1,
            MaxPage = (uint)document.PageCount,
            PageRange = new PageRange(1, document.PageCount),
            UserPageRangeEnabled = true,
            CurrentPageEnabled = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var pages = dialog.PageRangeSelection switch
        {
            PageRangeSelection.CurrentPage => [CurrentPageIndex],
            PageRangeSelection.UserPages => Range(dialog.PageRange, document.PageCount),
            _ => Enumerable.Range(0, document.PageCount).ToArray(),
        };

        var queue = dialog.PrintQueue;
        var job = new PrintJob(queue.FullName, ToDevMode(queue, dialog.PrintTicket), pages,
            Path.GetFileName(document.FilePath));

        IsLoading = true;
        PrintStatus = $"인쇄 중 0/{pages.Length}";
        try
        {
            await document.PrintAsync(job, new Progress<int>(done => PrintStatus = $"인쇄 중 {done}/{pages.Length}"));
        }
        catch (Exception ex) when (ex is Win32Exception or PdfException or ObjectDisposedException)
        {
            MessageBox.Show($"인쇄하지 못했습니다.\n\n{ex.Message}", AppName, MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            PrintStatus = null;
            IsLoading = false;
        }
    }

    /// <summary>대화 상자에서 입력한 페이지 범위(1부터)를 0부터 시작하는 번호 목록으로. 문서 범위 밖은 잘라낸다.</summary>
    private static int[] Range(PageRange range, int pageCount)
    {
        var from = Math.Clamp(Math.Min(range.PageFrom, range.PageTo), 1, pageCount);
        var to = Math.Clamp(Math.Max(range.PageFrom, range.PageTo), 1, pageCount);
        return Enumerable.Range(from - 1, to - from + 1).ToArray();
    }

    /// <summary>
    /// 대화 상자에서 고른 설정(용지, 방향, 매수, 양면 등)을 GDI용 DEVMODE로 바꾼다.
    /// 바꾸지 못하면 null을 돌려주고 프린터 기본값으로 인쇄한다.
    /// </summary>
    private static byte[]? ToDevMode(PrintQueue queue, PrintTicket ticket)
    {
        try
        {
            using var converter = new PrintTicketConverter(queue.FullName, queue.ClientPrintSchemaVersion);
            return converter.ConvertPrintTicketToDevMode(ticket, BaseDevModeType.UserDefault);
        }
        catch (Exception ex) when (ex is PrintQueueException or ArgumentException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"[Print] 인쇄 설정을 변환하지 못했습니다: {ex.Message}");
            return null;
        }
    }
}
