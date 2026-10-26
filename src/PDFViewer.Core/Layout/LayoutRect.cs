namespace PDFViewer.Core.Layout;

/// <summary>문서 좌표계(DIP)의 사각형. WPF에 의존하지 않기 위해 따로 정의한다.</summary>
public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool IntersectsWith(LayoutRect other) =>
        other.X < Right && other.Right > X && other.Y < Bottom && other.Bottom > Y;

    public LayoutRect Intersect(LayoutRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right > x && bottom > y ? new LayoutRect(x, y, right - x, bottom - y) : default;
    }
}
