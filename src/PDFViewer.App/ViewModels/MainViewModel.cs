using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PDFViewer.App.Controls;
using PDFViewer.Core.Layout;
using PDFViewer.Core.Pdf;

namespace PDFViewer.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const string AppName = "PDF Viewer";

    // 확대/축소 버튼과 단축키가 거치는 배율 단계
    private static readonly double[] ZoomSteps =
        [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6, 8, 10];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCount), nameof(IsDocumentOpen), nameof(Title), nameof(CurrentPageNumber))]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand), nameof(ZoomOutCommand), nameof(ActualSizeCommand),
        nameof(FitWidthCommand), nameof(FitPageCommand), nameof(NextPageCommand), nameof(PreviousPageCommand),
        nameof(FirstPageCommand), nameof(LastPageCommand), nameof(TogglePresentationCommand))]
    private PdfDocument? _document;

    [ObservableProperty]
    private double _zoom = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFitWidth), nameof(IsFitPage))]
    private ZoomMode _zoomMode = ZoomMode.FitWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContinuous), nameof(IsPageByPage))]
    private ScrollMode _scrollMode = ScrollMode.Continuous;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSinglePage), nameof(IsTwoPage))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    private PageLayout _pageLayout = PageLayout.SinglePage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    private bool _showCoverSeparately;

    [ObservableProperty]
    private bool _rightToLeft;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageNumber))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand), nameof(FirstPageCommand),
        nameof(LastPageCommand))]
    private int _currentPageIndex;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExitFullScreenCommand))]
    private bool _isFullScreen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExitFullScreenCommand))]
    private bool _isPresentation;

    // 프레젠테이션 모드에 들어가기 전의 보기 상태. 나올 때 되돌린다.
    private (ZoomMode ZoomMode, double Zoom, ScrollMode ScrollMode, bool FullScreen)? _beforePresentation;

    public int PageCount => Document?.PageCount ?? 0;

    public bool IsDocumentOpen => Document is not null;

    public string Title => Document is null ? AppName : $"{Path.GetFileName(Document.FilePath)} - {AppName}";

    /// <summary>화면에 표시하는 페이지 번호 (1부터). 범위를 벗어난 값은 가장 가까운 페이지로 맞춘다.</summary>
    public int CurrentPageNumber
    {
        get => PageCount == 0 ? 0 : CurrentPageIndex + 1;
        set
        {
            if (PageCount > 0)
            {
                CurrentPageIndex = Math.Clamp(value - 1, 0, PageCount - 1);
            }

            // 값이 맞춰졌으면 입력란에도 반영되도록 항상 알린다.
            OnPropertyChanged();
        }
    }

    #region 토글 버튼 / 메뉴용 속성

    // 이미 선택된 항목을 다시 눌러도 선택이 풀리지 않는다.

    public bool IsFitWidth
    {
        get => ZoomMode == ZoomMode.FitWidth;
        set => SetFromToggle(value, () => ZoomMode = ZoomMode.FitWidth, nameof(IsFitWidth), nameof(IsFitPage));
    }

    public bool IsFitPage
    {
        get => ZoomMode == ZoomMode.FitPage;
        set => SetFromToggle(value, () => ZoomMode = ZoomMode.FitPage, nameof(IsFitWidth), nameof(IsFitPage));
    }

    public bool IsContinuous
    {
        get => ScrollMode == ScrollMode.Continuous;
        set => SetFromToggle(value, () => ScrollMode = ScrollMode.Continuous, nameof(IsContinuous),
            nameof(IsPageByPage));
    }

    public bool IsPageByPage
    {
        get => ScrollMode == ScrollMode.PageByPage;
        set => SetFromToggle(value, () => ScrollMode = ScrollMode.PageByPage, nameof(IsContinuous),
            nameof(IsPageByPage));
    }

    public bool IsSinglePage
    {
        get => PageLayout == PageLayout.SinglePage;
        set => SetFromToggle(value, () => PageLayout = PageLayout.SinglePage, nameof(IsSinglePage),
            nameof(IsTwoPage));
    }

    public bool IsTwoPage
    {
        get => PageLayout == PageLayout.TwoPage;
        set => SetFromToggle(value, () => PageLayout = PageLayout.TwoPage, nameof(IsSinglePage), nameof(IsTwoPage));
    }

    private void SetFromToggle(bool isChecked, Action select, params string[] properties)
    {
        if (isChecked)
        {
            select();
        }

        foreach (var property in properties)
        {
            OnPropertyChanged(property);
        }
    }

    #endregion

    #region 파일

    [RelayCommand]
    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDF 파일 (*.pdf)|*.pdf|모든 파일 (*.*)|*.*",
            Title = "PDF 열기",
        };

        if (dialog.ShowDialog() == true)
        {
            await OpenFileAsync(dialog.FileName);
        }
    }

    public async Task OpenFileAsync(string path)
    {
        IsLoading = true;
        try
        {
            var document = await PdfDocument.OpenAsync(path);
            var old = Document;
            CurrentPageIndex = 0;
            Document = document;
            old?.Dispose();
        }
        catch (PdfException ex)
        {
            MessageBox.Show($"{Path.GetFileName(path)}\n\n{ex.Message}", AppName, MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            IsLoading = false;
        }
    }

    #endregion

    #region 배율 / 보기 방식

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ZoomIn() => SetCustomZoom(ZoomSteps.FirstOrDefault(s => s > Zoom + 0.001, PdfDocumentView.MaxZoom));

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ZoomOut() => SetCustomZoom(ZoomSteps.LastOrDefault(s => s < Zoom - 0.001, PdfDocumentView.MinZoom));

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ActualSize() => SetCustomZoom(1);

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void FitWidth() => ZoomMode = ZoomMode.FitWidth;

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void FitPage() => ZoomMode = ZoomMode.FitPage;

    private void SetCustomZoom(double zoom)
    {
        ZoomMode = ZoomMode.Custom;
        Zoom = zoom;
    }

    [RelayCommand]
    private void ShowSinglePage() => PageLayout = PageLayout.SinglePage;

    [RelayCommand]
    private void ShowTwoPages() => PageLayout = PageLayout.TwoPage;

    #endregion

    #region 페이지 이동

    // 2페이지 보기에서는 한 줄(두 페이지)씩 이동한다.
    private (int First, int Count) CurrentRow => DocumentLayout.GetRowRange(CurrentPageIndex, PageCount,
        new LayoutOptions { PageLayout = PageLayout, ShowCoverSeparately = ShowCoverSeparately });

    private bool CanGoNext => PageCount > 0 && CurrentRow.First + CurrentRow.Count < PageCount;

    private bool CanGoPrevious => PageCount > 0 && CurrentRow.First > 0;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPage()
    {
        var (first, count) = CurrentRow;
        CurrentPageIndex = first + count;
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousPage() => CurrentPageIndex = DocumentLayout.GetRowRange(CurrentRow.First - 1, PageCount,
        new LayoutOptions { PageLayout = PageLayout, ShowCoverSeparately = ShowCoverSeparately }).First;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void FirstPage() => CurrentPageIndex = 0;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void LastPage() => CurrentPageIndex = PageCount - 1;

    #endregion

    #region 전체화면 / 프레젠테이션

    [RelayCommand]
    private void ToggleFullScreen()
    {
        if (IsPresentation)
        {
            ExitPresentation();
        }
        else
        {
            IsFullScreen = !IsFullScreen;
        }
    }

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void TogglePresentation()
    {
        if (IsPresentation)
        {
            ExitPresentation();
            return;
        }

        // 프레젠테이션은 전체화면 + 페이지 맞춤 + 페이지 단위. 1페이지/2페이지 보기는 그대로 둔다.
        _beforePresentation = (ZoomMode, Zoom, ScrollMode, IsFullScreen);
        IsPresentation = true;
        IsFullScreen = true;
        ScrollMode = ScrollMode.PageByPage;
        ZoomMode = ZoomMode.FitPage;
    }

    private bool CanExitFullScreen => IsFullScreen || IsPresentation;

    /// <summary>Esc. 프레젠테이션 중이면 프레젠테이션 전 상태(전체화면이었으면 전체화면)로 돌아간다.</summary>
    [RelayCommand(CanExecute = nameof(CanExitFullScreen))]
    private void ExitFullScreen()
    {
        if (IsPresentation)
        {
            ExitPresentation();
        }
        else
        {
            IsFullScreen = false;
        }
    }

    private void ExitPresentation()
    {
        IsPresentation = false;
        if (_beforePresentation is not { } before)
        {
            return;
        }

        _beforePresentation = null;
        IsFullScreen = before.FullScreen;
        ScrollMode = before.ScrollMode;
        if (before.ZoomMode == ZoomMode.Custom)
        {
            SetCustomZoom(before.Zoom);
        }
        else
        {
            ZoomMode = before.ZoomMode;
        }
    }

    #endregion

    public void Dispose()
    {
        Document?.Dispose();
        Document = null;
    }
}
