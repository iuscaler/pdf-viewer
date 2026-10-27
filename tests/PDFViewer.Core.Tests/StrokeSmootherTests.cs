using PDFViewer.Core.Annotations;

namespace PDFViewer.Core.Tests;

public sealed class StrokeSmootherTests
{
    // 기울기 1/2 사선을 픽셀 계단으로 입력: 가로 1칸, 가로 1칸 + 세로 1칸 ... (실제 마우스 입력 모양)
    private static List<(double X, double Y)> Staircase(int steps)
    {
        var points = new List<(double, double)> { (0, 0) };
        int x = 0, y = 0;
        for (var i = 1; i <= steps; i++)
        {
            x++;
            points.Add((x, y));
            if (i % 2 == 0)
            {
                y++;
                points.Add((x, y));
            }
        }

        return points;
    }

    // 점들에 가장 잘 맞는 직선(최소제곱)에서 벗어난 최대 거리 = 얼마나 울퉁불퉁한지
    private static double MaxDeviation(IEnumerable<InkPoint> points)
    {
        var list = points.ToList();
        double mx = list.Average(p => p.X), my = list.Average(p => p.Y);
        double sxx = list.Sum(p => (p.X - mx) * (p.X - mx)), sxy = list.Sum(p => (p.X - mx) * (p.Y - my));
        var slope = sxy / sxx;
        return list.Max(p => Math.Abs(p.Y - my - slope * (p.X - mx)) / Math.Sqrt(1 + slope * slope));
    }

    [Fact]
    public void Staircase_BecomesNearlyStraight()
    {
        var input = Staircase(200);
        var smoother = new StrokeSmoother(smoothingLength: 3, minDistance: 1.5);
        foreach (var (x, y) in input)
        {
            smoother.Add(x, y, 0.5f);
        }

        smoother.Finish();

        var rawDeviation = MaxDeviation(input.Select(p => new InkPoint((float)p.X, (float)p.Y, 0.5f)));
        // 처음 몇 점(필터가 따라붙기 전)을 빼고 비교한다.
        var smoothDeviation = MaxDeviation(smoother.Points.Skip(5).SkipLast(1));
        Assert.True(smoothDeviation < rawDeviation / 3, $"raw {rawDeviation:0.000}, smooth {smoothDeviation:0.000}");
    }

    [Fact]
    public void KeepsFirstAndLastInputPoints()
    {
        var smoother = new StrokeSmoother(3, 1.5);
        smoother.Add(10, 10, 0.5f);
        for (var i = 1; i <= 50; i++)
        {
            smoother.Add(10 + i, 10, 0.5f);
        }

        smoother.Finish();

        Assert.Equal(new InkPoint(10, 10, 0.5f), smoother.Points[0]);
        Assert.Equal(60, smoother.Points[^1].X, 3);
        Assert.Equal(10, smoother.Points[^1].Y, 3);
    }

    [Fact]
    public void RespectsMinDistance()
    {
        var smoother = new StrokeSmoother(3, 1.5);
        for (var i = 0; i <= 100; i++)
        {
            smoother.Add(i * 0.25, 0, 0.5f);
        }

        var points = smoother.Points;
        for (var i = 1; i < points.Count; i++)
        {
            Assert.True(points[i].X - points[i - 1].X >= 1.5 - 1e-4);
        }
    }

    [Fact]
    public void StandingStill_DoesNotDrift()
    {
        var smoother = new StrokeSmoother(3, 0.1);
        smoother.Add(0, 0, 0.5f);
        smoother.Add(10, 0, 0.5f);
        var count = smoother.Points.Count;

        // 같은 자리에서 이벤트가 계속 와도(이동 거리 0) 점이 늘지 않는다.
        for (var i = 0; i < 20; i++)
        {
            smoother.Add(10, 0, 0.5f);
        }

        Assert.Equal(count, smoother.Points.Count);
    }

    [Fact]
    public void PressureIsSmoothed()
    {
        var smoother = new StrokeSmoother(3, 0.5);
        smoother.Add(0, 0, 0.5f);
        // 필압이 0.2와 0.8 사이를 번갈아 튀는 입력
        for (var i = 1; i <= 60; i++)
        {
            smoother.Add(i, 0, i % 2 == 0 ? 0.8f : 0.2f);
        }

        var pressures = smoother.Points.Skip(10).Select(p => p.Pressure).ToList();
        Assert.True(pressures.Max() - pressures.Min() < 0.3, $"{pressures.Min():0.00}~{pressures.Max():0.00}");
    }
}
