using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PDFViewer.Core.Layout;

namespace PDFViewer.App.Settings;

/// <summary>
/// 앱 설정. %APPDATA%\PDFViewer\settings.json 에 저장한다.
/// 읽지 못하면(처음 실행, 파일 손상) 기본값을 쓴다.
/// </summary>
public sealed class AppSettings
{
    /// <summary>최근 파일을 몇 개까지 기억할지.</summary>
    public const int MaxRecentFiles = 10;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDFViewer", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // 보기
    public ZoomMode ZoomMode { get; set; } = ZoomMode.FitWidth;
    public double Zoom { get; set; } = 1;
    public ScrollMode ScrollMode { get; set; } = ScrollMode.Continuous;
    public PageLayout PageLayout { get; set; } = PageLayout.SinglePage;
    public bool ShowCoverSeparately { get; set; }
    public bool RightToLeft { get; set; }
    public bool ShowThumbnails { get; set; }

    // 펜 / 형광펜 (색은 #AARRGGBB)
    public string? PenColor { get; set; }
    public double? PenWidth { get; set; }
    public string? HighlighterColor { get; set; }
    public double? HighlighterWidth { get; set; }

    // 창 위치와 크기 (DIP). 최대화 상태였으면 Maximized.
    public WindowPlacement? Window { get; set; }

    /// <summary>최근에 연 파일. 앞쪽이 가장 최근.</summary>
    public List<RecentFile> RecentFiles { get; set; } = [];

    /// <summary>파일을 열었거나 닫을 때 최근 파일 목록 맨 앞으로 올리고 마지막 페이지를 기억한다.</summary>
    public void RememberFile(string path, int lastPage)
    {
        RecentFiles.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, new RecentFile { Path = path, LastPage = lastPage });
        if (RecentFiles.Count > MaxRecentFiles)
        {
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        }
    }

    /// <summary>파일의 마지막으로 보던 페이지 (0부터). 기록이 없으면 0.</summary>
    public int LastPageOf(string path) =>
        RecentFiles.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))?.LastPage
        ?? 0;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[AppSettings] 설정을 읽지 못했습니다: {ex.Message}");
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 설정을 저장하지 못해도 앱 동작에는 영향이 없다.
            Debug.WriteLine($"[AppSettings] 설정을 저장하지 못했습니다: {ex.Message}");
        }
    }
}

public sealed class RecentFile
{
    public string Path { get; set; } = "";

    /// <summary>마지막으로 보던 페이지 (0부터).</summary>
    public int LastPage { get; set; }

    [JsonIgnore]
    public string FileName => System.IO.Path.GetFileName(Path);

    [JsonIgnore]
    public string? Folder => System.IO.Path.GetDirectoryName(Path);
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
