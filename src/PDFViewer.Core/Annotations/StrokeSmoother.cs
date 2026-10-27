namespace PDFViewer.Core.Annotations;

/// <summary>
/// 손으로 그리는 입력 점의 떨림을 걸러낸다.
/// </summary>
/// <remarks>
/// 마우스 좌표는 픽셀 단위 정수라서, 사선으로 부드럽게 그어도 입력은 "가로 1픽셀, 세로 1픽셀"의 계단 모양으로 들어온다.
/// 이 점들을 그대로 곡선으로 이으면 선이 미세하게 울퉁불퉁해진다.
/// <para>
/// 그리는 점이 입력을 이동 거리에 비례해 따라가게 한다 (거리 기반 지수 평활):
/// <c>s += (raw - s) * (1 - exp(-d / L))</c>, d는 이번에 입력이 움직인 거리, L은 평활 길이.
/// 시간이 아니라 거리 기준이라 이벤트가 한꺼번에 몰려와도 결과가 같고, 멈춰 있으면 그리는 점도 거의 움직이지 않는다.
/// 그리는 점은 입력보다 대략 L만큼 뒤따라오며, 획을 마치면 마지막 입력 위치까지 잇는다.
/// </para>
/// </remarks>
public sealed class StrokeSmoother
{
    // 필압은 제자리에서도 바뀔 수 있으므로 최소한 이 비율로는 따라가게 한다.
    private const double MinPressureFollow = 0.15;

    private readonly double _smoothingLength;
    private readonly double _minDistance;
    private readonly List<InkPoint> _points = [];
    private InkPoint _smoothed;
    private InkPoint _lastRaw;

    /// <param name="smoothingLength">평활 길이 L (입력과 같은 단위). 클수록 부드럽지만 입력을 늦게 따라간다.</param>
    /// <param name="minDistance">결과 점 사이 최소 거리. 이보다 가까운 점은 남기지 않는다.</param>
    public StrokeSmoother(double smoothingLength, double minDistance)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(smoothingLength);
        ArgumentOutOfRangeException.ThrowIfNegative(minDistance);
        _smoothingLength = smoothingLength;
        _minDistance = minDistance;
    }

    /// <summary>걸러낸 점들. 첫 점은 첫 입력 그대로다.</summary>
    public IReadOnlyList<InkPoint> Points => _points;

    public void Add(double x, double y, float pressure)
    {
        var raw = new InkPoint((float)x, (float)y, pressure);
        if (_points.Count == 0)
        {
            _smoothed = _lastRaw = raw;
            _points.Add(raw);
            return;
        }

        var follow = 1 - Math.Exp(-Distance(raw, _lastRaw) / _smoothingLength);
        _lastRaw = raw;
        var pressureFollow = Math.Max(follow, MinPressureFollow);
        _smoothed = new InkPoint(
            (float)(_smoothed.X + (raw.X - _smoothed.X) * follow),
            (float)(_smoothed.Y + (raw.Y - _smoothed.Y) * follow),
            (float)(_smoothed.Pressure + (raw.Pressure - _smoothed.Pressure) * pressureFollow));

        if (Distance(_smoothed, _points[^1]) >= _minDistance)
        {
            _points.Add(_smoothed);
        }
    }

    /// <summary>획을 마친다. 그리는 점이 뒤따라오던 만큼 마지막 입력 위치까지 잇는다.</summary>
    public void Finish()
    {
        if (_points.Count > 0 && Distance(_lastRaw, _points[^1]) > _minDistance / 2)
        {
            _points.Add(_lastRaw with { Pressure = _smoothed.Pressure });
        }
    }

    private static double Distance(InkPoint a, InkPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
