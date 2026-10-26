using System.IO;
using System.Windows;
using PDFViewer.App.ViewModels;

namespace PDFViewer.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var viewModel = new MainViewModel();
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
