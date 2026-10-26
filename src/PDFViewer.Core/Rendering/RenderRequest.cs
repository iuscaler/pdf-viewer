namespace PDFViewer.Core.Rendering;

/// <summary>정수 픽셀 사각형.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public long Area => (long)Width * Height;

    public bool Contains(PixelRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
}

/// <summary>
/// 페이지 렌더링 요청. 페이지 전체를 <see cref="PageWidth"/> x <see cref="PageHeight"/> 픽셀로
/// 렌더링했다고 가정했을 때 그중 <see cref="Region"/> 영역만 비트맵으로 만든다.
/// </summary>
public sealed record RenderRequest(int PageIndex, int PageWidth, int PageHeight, PixelRect Region)
{
    /// <summary>페이지 전체를 렌더링하는 요청을 만든다.</summary>
    public static RenderRequest FullPage(int pageIndex, int width, int height) =>
        new(pageIndex, width, height, new PixelRect(0, 0, width, height));

    public bool IsFullPage =>
        Region.X == 0 && Region.Y == 0 && Region.Width == PageWidth && Region.Height == PageHeight;
}
