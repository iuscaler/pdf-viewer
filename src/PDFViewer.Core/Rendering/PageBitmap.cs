using System.Buffers;

namespace PDFViewer.Core.Rendering;

/// <summary>
/// 렌더링 결과 (32비트 BGRx 픽셀). 버퍼는 <see cref="ArrayPool{T}"/>에서 빌려오므로
/// 사용이 끝나면 반드시 Dispose해서 반환해야 한다.
/// </summary>
public sealed class PageBitmap : IDisposable
{
    private byte[]? _buffer;

    public PageBitmap(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Stride = width * 4;
        _buffer = ArrayPool<byte>.Shared.Rent(Stride * height);
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public int BufferSize => Stride * Height;

    /// <summary>실제 픽셀 데이터. 빌려온 배열은 요청 크기보다 클 수 있으므로 <see cref="BufferSize"/>까지만 유효하다.</summary>
    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(PageBitmap));

    public void Dispose()
    {
        if (_buffer is { } buffer)
        {
            _buffer = null;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
