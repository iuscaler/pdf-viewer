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
/// <param name="Revision">
/// 요청한 쪽이 붙이는 페이지 내용 버전(주석을 고칠 때마다 올라감). 결과 비트맵이 어느 시점의 페이지인지 알 수 있다.
/// </param>
public sealed record RenderRequest(int PageIndex, int PageWidth, int PageHeight, PixelRect Region, int Revision = 0)
{
    /// <summary>페이지 전체를 렌더링하는 요청을 만든다.</summary>
    public static RenderRequest FullPage(int pageIndex, int width, int height, int revision = 0) =>
        new(pageIndex, width, height, new PixelRect(0, 0, width, height), revision);

    public bool IsFullPage =>
        Region.X == 0 && Region.Y == 0 && Region.Width == PageWidth && Region.Height == PageHeight;
}
