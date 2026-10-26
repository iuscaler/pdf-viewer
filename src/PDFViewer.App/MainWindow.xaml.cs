using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PDFViewer.App.ViewModels;

namespace PDFViewer.App;

public partial class MainWindow : Window
{
    // 전체화면에서 마우스를 화면 맨 위 몇 픽셀 안으로 가져가면 툴바가 나타난다.
    private const double ToolbarRevealHeight = 3;
    private const double ToolbarHideMargin = 24;
    private static readonly TimeSpan CursorHideDelay = TimeSpan.FromSeconds(2);

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _cursorHideTimer;
    private bool _isFullScreenApplied;
    private WindowState _windowStateBeforeFullScreen;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _cursorHideTimer = new DispatcherTimer { Interval = CursorHideDelay };
        _cursorHideTimer.Tick += (_, _) =>
        {
            _cursorHideTimer.Stop();
            Mouse.OverrideCursor = Cursors.None;
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        Mouse.OverrideCursor = null;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Document) when _viewModel.IsDocumentOpen:
                // 문서를 열면 바로 키보드로 스크롤할 수 있게 한다.
                DocumentScroller.Focus();
                break;
            case nameof(MainViewModel.IsFullScreen):
            case nameof(MainViewModel.IsPresentation):
                ApplyWindowMode();
                break;
        }
    }

    #region 전체화면 / 프레젠테이션

    private void ApplyWindowMode()
    {
        if (_viewModel.IsFullScreen != _isFullScreenApplied)
        {
            _isFullScreenApplied = _viewModel.IsFullScreen;
            if (_isFullScreenApplied)
            {
                _windowStateBeforeFullScreen = WindowState;
                // 최대화 상태에서 테두리만 없애면 작업 표시줄을 덮지 못하므로 한 번 일반 상태로 되돌린다.
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;

                // 문서가 툴바 자리까지 차지하고, 툴바는 필요할 때만 문서 위에 겹쳐서 보인다.
                Grid.SetRow(DocumentArea, 0);
                Grid.SetRowSpan(DocumentArea, 2);
                Toolbar.Visibility = Visibility.Collapsed;
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                WindowState = _windowStateBeforeFullScreen;

                Grid.SetRow(DocumentArea, 1);
                Grid.SetRowSpan(DocumentArea, 1);
                Toolbar.Visibility = Visibility.Visible;
            }
        }

        var presentation = _viewModel.IsPresentation;
        DocumentArea.Background = presentation ? Brushes.Black : (Brush)FindResource("DocumentBackground");
        DocumentScroller.VerticalScrollBarVisibility =
            presentation ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Visible;
        DocumentScroller.HorizontalScrollBarVisibility =
            presentation ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;

        if (presentation)
        {
            Toolbar.Visibility = Visibility.Collapsed;
            _cursorHideTimer.Start();
        }
        else
        {
            _cursorHideTimer.Stop();
            Mouse.OverrideCursor = null;
        }

        DocumentScroller.Focus();
    }

    private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_viewModel.IsPresentation)
        {
            // 마우스를 움직이면 커서를 보여주고, 잠시 가만히 있으면 다시 숨긴다.
            Mouse.OverrideCursor = null;
            _cursorHideTimer.Stop();
            _cursorHideTimer.Start();
            return;
        }

        if (!_viewModel.IsFullScreen)
        {
            return;
        }

        var y = e.GetPosition(this).Y;
        if (y <= ToolbarRevealHeight)
        {
            Toolbar.Visibility = Visibility.Visible;
        }
        else if (Toolbar.IsVisible && y > Toolbar.ActualHeight + ToolbarHideMargin
                                   && ViewMenuButton.ContextMenu?.IsOpen != true)
        {
            Toolbar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsPresentation)
        {
            return;
        }

        // 프레젠테이션 리모컨과 발표용 키. 글자 키는 주석 도구 단축키(P/H/E/V)용으로 남겨 둔다.
        var command = e.Key switch
        {
            Key.Space or Key.Enter => _viewModel.NextPageCommand,
            Key.Back => _viewModel.PreviousPageCommand,
            Key.Home => _viewModel.FirstPageCommand,
            Key.End => _viewModel.LastPageCommand,
            _ => null,
        };

        if (command is not null)
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            e.Handled = true;
        }
    }

    // 프레젠테이션에서는 왼쪽 클릭으로 다음 페이지, 오른쪽 클릭으로 이전 페이지
    private void OnDocumentPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsPresentation && _viewModel.NextPageCommand.CanExecute(null))
        {
            _viewModel.NextPageCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDocumentPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsPresentation && _viewModel.PreviousPageCommand.CanExecute(null))
        {
            _viewModel.PreviousPageCommand.Execute(null);
            e.Handled = true;
        }
    }

    #endregion

    private void OnViewMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (ViewMenuButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = ViewMenuButton;
            menu.Placement = PlacementMode.Bottom;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    private void OnPageNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            PageNumberBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            DocumentScroller.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PageNumberBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            DocumentScroller.Focus();
            e.Handled = true;
        }
    }

    private void OnPageNumberGotFocus(object sender, KeyboardFocusChangedEventArgs e) => PageNumberBox.SelectAll();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedPdf(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (GetDroppedPdf(e) is { } path)
        {
            await _viewModel.OpenFileAsync(path);
        }
    }

    private static string? GetDroppedPdf(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase))
            : null;
}
