using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using PDFViewer.App.Annotations;
using PDFViewer.App.Settings;

namespace PDFViewer.App.ViewModels;

/// <summary>설정 불러오기/저장, 최근 파일, 마지막으로 보던 페이지.</summary>
public sealed partial class MainViewModel
{
    private readonly AppSettings _settings;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        ZoomMode = settings.ZoomMode;
        Zoom = settings.Zoom;
        ScrollMode = settings.ScrollMode;
        PageLayout = settings.PageLayout;
        ShowCoverSeparately = settings.ShowCoverSeparately;
        RightToLeft = settings.RightToLeft;
        IsThumbnailsOpen = settings.ShowThumbnails;

        // 색이나 굵기를 바꾸면 그 도구가 선택되므로, 다 불러온 뒤 선택 도구로 되돌린다.
        if (ParseColor(settings.PenColor) is { } penColor)
        {
            PenColor = penColor;
        }

        if (settings.PenWidth is { } penWidth)
        {
            PenWidth = Math.Clamp(penWidth, PenMinWidth, PenMaxWidth);
        }

        if (ParseColor(settings.HighlighterColor) is { } highlighterColor)
        {
            HighlighterColor = highlighterColor;
        }

        if (settings.HighlighterWidth is { } highlighterWidth)
        {
            HighlighterWidth = Math.Clamp(highlighterWidth, HighlighterMinWidth, HighlighterMaxWidth);
        }

        ActiveTool = Controls.AnnotationTool.Select;
        RefreshRecentFiles();
    }

    public MainViewModel() : this(new AppSettings())
    {
    }

    public AppSettings Settings => _settings;

    /// <summary>최근 파일 (열기 메뉴와 시작 화면). 존재하지 않는 파일은 뺀다.</summary>
    public ObservableCollection<RecentFile> RecentFiles { get; } = [];

    public bool HasRecentFiles => RecentFiles.Count > 0;

    [RelayCommand]
    private async Task OpenRecentAsync(RecentFile? file)
    {
        if (file is null)
        {
            return;
        }

        if (!File.Exists(file.Path))
        {
            System.Windows.MessageBox.Show($"파일을 찾을 수 없습니다.\n\n{file.Path}", AppName);
            _settings.RecentFiles.RemoveAll(f => f.Path == file.Path);
            RefreshRecentFiles();
            return;
        }

        await OpenFileAsync(file.Path);
    }

    [RelayCommand]
    private void ClearRecentFiles()
    {
        _settings.RecentFiles.Clear();
        RefreshRecentFiles();
    }

    /// <summary>지금 보고 있는 문서를 최근 파일 맨 앞에 두고, 보고 있는 페이지를 기억한다.</summary>
    private void RememberCurrentDocument()
    {
        if (Document is { } document)
        {
            _settings.RememberFile(document.FilePath, CurrentPageIndex);
            RefreshRecentFiles();
        }
    }

    private void RefreshRecentFiles()
    {
        RecentFiles.Clear();
        foreach (var file in _settings.RecentFiles.Where(f => File.Exists(f.Path)))
        {
            RecentFiles.Add(file);
        }

        OnPropertyChanged(nameof(HasRecentFiles));
    }

    /// <summary>현재 상태를 설정에 담는다 (창 위치는 MainWindow가 담는다).</summary>
    public void CaptureSettings()
    {
        // 프레젠테이션 중에 닫으면 프레젠테이션 전 상태를 저장한다.
        var (zoomMode, zoom, scrollMode) = _beforePresentation is { } before
            ? (before.ZoomMode, before.Zoom, before.ScrollMode)
            : (ZoomMode, Zoom, ScrollMode);
        _settings.ZoomMode = zoomMode;
        _settings.Zoom = zoom;
        _settings.ScrollMode = scrollMode;
        _settings.PageLayout = PageLayout;
        _settings.ShowCoverSeparately = ShowCoverSeparately;
        _settings.RightToLeft = RightToLeft;
        _settings.ShowThumbnails = IsThumbnailsOpen;
        _settings.PenColor = ColorToString(PenColor);
        _settings.PenWidth = PenWidth;
        _settings.HighlighterColor = ColorToString(HighlighterColor);
        _settings.HighlighterWidth = HighlighterWidth;
        RememberCurrentDocument();
    }

    private static string ColorToString(Color c) => $"#{InkGeometry.ToArgb(c):X8}";

    private static Color? ParseColor(string? text) =>
        text is { Length: 9 } && text[0] == '#'
        && uint.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var argb)
            ? InkGeometry.FromArgb(argb)
            : null;
}
