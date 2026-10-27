using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace PDFViewer.App.Diagnostics;

/// <summary>
/// 펜/마우스 입력 측정. 환경 변수 PDFVIEWER_INPUT_LOG=1 일 때만 동작하며,
/// %TEMP%\pdfviewer-input.log 에 획마다 받은 입력 점과, Windows가 실제로 기록한 마우스 이동 점을 남긴다.
/// 앱이 받는 점이 실제 입력보다 얼마나 적은지, 점이 얼마나 떨리는지 확인하는 용도다.
/// </summary>
internal static class InputLog
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("PDFVIEWER_INPUT_LOG") == "1";

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "pdfviewer-input.log");
    private static readonly StringBuilder Buffer = new();

    private static int _strokeNumber;
    private static int _events;
    private static int _received;
    private static int _systemPoints;
    private static int _lastMessageTime;
    private static long _startTicks;
    private static double _pathLength;
    private static Point? _lastPagePoint;
    private static float _minPressure = float.MaxValue;
    private static float _maxPressure = float.MinValue;

    public static void BeginStroke(string tool, string device, Point pagePoint, double zoom, double dpiScale)
    {
        _strokeNumber++;
        _events = 0;
        _received = 1;
        _systemPoints = 0;
        _lastMessageTime = 0;
        _startTicks = Environment.TickCount64;
        _pathLength = 0;
        _lastPagePoint = pagePoint;
        _minPressure = float.MaxValue;
        _maxPressure = float.MinValue;
        Buffer.Clear();
        Append($"# stroke {_strokeNumber}: {tool}, {device}, zoom={zoom:0.###}, dpiScale={dpiScale:0.##}");
        Append($"  start page=({pagePoint.X:0.00},{pagePoint.Y:0.00})");
    }

    /// <summary>마우스 이동 이벤트 하나. 그 사이에 Windows가 기록한 이동 점의 개수도 센다.</summary>
    public static void MouseMove(UIElement element, MouseEventArgs e, Point pagePoint)
    {
        _events++;
        _received++;
        var screen = element.PointToScreen(e.GetPosition(element)); // 물리 픽셀
        var systemCount = CountSystemPointsSince(screen, e.Timestamp, _lastMessageTime);
        _lastMessageTime = e.Timestamp;
        if (systemCount > 0)
        {
            _systemPoints += systemCount;
        }

        Track(pagePoint);
        Append($"  t={Environment.TickCount64 - _startTicks,5}ms screen=({screen.X:0},{screen.Y:0}) " +
               $"page=({pagePoint.X:0.00},{pagePoint.Y:0.00}) system-points-since-last={systemCount}");
    }

    /// <summary>펜 이동 이벤트 하나 (여러 점이 함께 온다).</summary>
    public static void StylusMove(StylusPointCollection points, IEnumerable<Point> pagePoints)
    {
        _events++;
        _received += points.Count;
        foreach (var p in points)
        {
            _minPressure = Math.Min(_minPressure, p.PressureFactor);
            _maxPressure = Math.Max(_maxPressure, p.PressureFactor);
        }

        foreach (var p in pagePoints)
        {
            Track(p);
        }

        Append($"  t={Environment.TickCount64 - _startTicks,5}ms points={points.Count} pressure=" +
               string.Join(",", points.Select(p => p.PressureFactor.ToString("0.000", CultureInfo.InvariantCulture))));
    }

    public static void EndStroke(int keptPoints)
    {
        var duration = Environment.TickCount64 - _startTicks;
        var spacing = _received > 1 ? _pathLength / (_received - 1) : 0;
        var summary = $"  summary: duration={duration}ms, events={_events}, received-points={_received}, " +
                      $"kept-points={keptPoints}, path={_pathLength:0.0}pt, avg-spacing={spacing:0.00}pt";
        if (_systemPoints > 0)
        {
            summary += $", system-mouse-points={_systemPoints} (앱이 받은 이동 이벤트 대비 {(double)_systemPoints / Math.Max(1, _events):0.0}배)";
        }

        if (_maxPressure >= _minPressure)
        {
            summary += $", pressure={_minPressure:0.000}~{_maxPressure:0.000}";
        }

        Append(summary);
        try
        {
            File.AppendAllText(LogPath, Buffer.ToString());
        }
        catch (IOException)
        {
            // 측정 로그를 쓰지 못해도 앱 동작에는 영향이 없어야 한다.
        }

        Buffer.Clear();
    }

    private static void Track(Point pagePoint)
    {
        if (_lastPagePoint is { } last)
        {
            _pathLength += (pagePoint - last).Length;
        }

        _lastPagePoint = pagePoint;
    }

    private static void Append(string line) => Buffer.AppendLine(line);

    #region GetMouseMovePointsEx

    private const int GmmpUseDisplayPoints = 1;
    private const int HistorySize = 64;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseMovePoint
    {
        public int X;
        public int Y;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMouseMovePointsEx(uint cbSize, ref MouseMovePoint lppt,
        [Out] MouseMovePoint[] lpptBuf, int nBufPoints, uint resolution);

    /// <summary>
    /// 이번 이동 이벤트(screen, time) 직전까지, 이전 이벤트 시각(previousTime) 이후에 Windows가 기록한 마우스 이동 점의 수.
    /// 이전 이벤트가 없거나 기록을 찾지 못하면 -1.
    /// </summary>
    private static int CountSystemPointsSince(Point screen, int time, int previousTime)
    {
        if (previousTime == 0)
        {
            return -1;
        }

        var current = new MouseMovePoint
        {
            X = (int)Math.Round(screen.X) & 0xFFFF,
            Y = (int)Math.Round(screen.Y) & 0xFFFF,
            Time = (uint)time,
        };
        var history = new MouseMovePoint[HistorySize];
        var count = GetMouseMovePointsEx((uint)Marshal.SizeOf<MouseMovePoint>(), ref current, history, HistorySize,
            GmmpUseDisplayPoints);
        if (count <= 0)
        {
            return -1;
        }

        // 기록은 최근 것부터 온다. 이번 점을 포함해 이전 이벤트 시각보다 나중인 점을 센다.
        var n = 0;
        for (var i = 0; i < count; i++)
        {
            if ((int)(history[i].Time - (uint)previousTime) <= 0)
            {
                break;
            }

            n++;
        }

        return n;
    }

    #endregion
}
