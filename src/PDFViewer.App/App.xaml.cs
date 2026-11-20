using System.IO;
using System.Windows;
using PDFViewer.App.Settings;
using PDFViewer.App.ViewModels;

namespace PDFViewer.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Windows 11(Fluent) 테마: 스크롤바, 메뉴, 입력란 등 기본 컨트롤을 Windows 11 모양으로 그린다.
        // .NET의 실험 기능이다(WPF0001). 다크 모드는 직접 정한 색과 어긋나므로 밝은 테마로 고정한다.
        // 되돌리려면 아래 세 줄을 지운다.
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Light;
#pragma warning restore WPF0001

        base.OnStartup(e);

        var viewModel = new MainViewModel(AppSettings.Load());
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();

        // "연결 프로그램"이나 명령줄로 전달된 파일 열기
        if (e.Args.FirstOrDefault(File.Exists) is { } path)
        {
            await viewModel.OpenFileAsync(path);
        }
    }
}
