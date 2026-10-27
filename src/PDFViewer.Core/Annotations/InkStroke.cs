using PDFViewer.Core.Layout;

namespace PDFViewer.Core.Annotations;

/// <summary>
/// 획의 한 점. 좌표는 페이지 왼쪽 위를 원점으로 하는 포인트 단위(화면에 보이는 방향 기준)이고,
/// 필압은 0~1 (마우스 입력은 0.5).
/// </summary>
public readonly record struct InkPoint(float X, float Y, float Pressure);

/// <summary>펜(또는 자유 형광펜)으로 그린 획 하나.</summary>
public sealed class InkStroke : Annotation
{
    public InkStroke(int pageIndex, IReadOnlyList<InkPoint> points, uint color, double width, bool isHighlighter = false)
        : base(pageIndex, color)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        if (points.Count == 0)
        {
            throw new ArgumentException("획에는 점이 하나 이상 있어야 합니다.", nameof(points));
        }

        Points = points;
        Width = width;
        IsHighlighter = isHighlighter;
        Bounds = ComputeBounds(points, width);
    }

    public IReadOnlyList<InkPoint> Points { get; }

    /// <summary>펜 굵기 (포인트).</summary>
    public double Width { get; }

    /// <summary>자유 형광펜으로 그린 반투명 획인지 여부.</summary>
    public bool IsHighlighter { get; }

    /// <summary>펜 굵기까지 포함한 영역 (포인트, 페이지 좌표).</summary>
    public override LayoutRect Bounds { get; }

    public override bool HitTest(double x, double y, double radius)
    {
        var reach = radius + Width / 2;
        if (x < Bounds.X - radius || x > Bounds.Right + radius || y < Bounds.Y - radius || y > Bounds.Bottom + radius)
        {
            return false;
        }

        if (Points.Count == 1)
        {
            return DistanceSquared(x, y, Points[0].X, Points[0].Y) <= reach * reach;
        }

        for (var i = 1; i < Points.Count; i++)
        {
            if (SegmentDistanceSquared(x, y, Points[i - 1], Points[i]) <= reach * reach)
            {
                return true;
            }
        }

        return false;
    }

    private static LayoutRect ComputeBounds(IReadOnlyList<InkPoint> points, double width)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var p in points)
        {
            left = Math.Min(left, p.X);
            top = Math.Min(top, p.Y);
            right = Math.Max(right, p.X);
            bottom = Math.Max(bottom, p.Y);
        }

        var half = width / 2;
        return new LayoutRect(left - half, top - half, right - left + width, bottom - top + width);
    }

    private static double DistanceSquared(double x1, double y1, double x2, double y2) =>
        (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2);

    // 점과 선분 사이 거리의 제곱
    private static double SegmentDistanceSquared(double x, double y, InkPoint a, InkPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared, 0, 1);
        return DistanceSquared(x, y, a.X + t * dx, a.Y + t * dy);
    }
}
