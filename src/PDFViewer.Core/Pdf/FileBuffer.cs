using System.Runtime.InteropServices;

namespace PDFViewer.Core.Pdf;

/// <summary>
/// 파일 전체를 담은 네이티브 메모리. PDFium이 문서를 메모리에서 열 때 쓰며, 문서를 닫을 때까지 유지해야 한다.
/// 파일을 메모리로 읽어 두면 PDFium이 원본 파일을 열어 두지 않으므로, 저장할 때 원본을 바로 교체할 수 있다.
/// </summary>
internal sealed unsafe class FileBuffer : IDisposable
{
    private FileBuffer(nint pointer, long length)
    {
        Pointer = pointer;
        Length = length;
    }

    public nint Pointer { get; private set; }

    public long Length { get; }

    public static FileBuffer Read(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = RandomAccess.GetLength(handle);
        var pointer = (nint)NativeMemory.Alloc((nuint)Math.Max(1, length));
        try
        {
            // Span 길이가 int라서 1GB씩 나눠 읽는다.
            const int chunk = 1 << 30;
            long offset = 0;
            while (offset < length)
            {
                var count = (int)Math.Min(chunk, length - offset);
                var read = RandomAccess.Read(handle, new Span<byte>((byte*)pointer + offset, count), offset);
                if (read == 0)
                {
                    throw new EndOfStreamException("파일을 끝까지 읽지 못했습니다.");
                }

                offset += read;
            }

            return new FileBuffer(pointer, length);
        }
        catch
        {
            NativeMemory.Free((void*)pointer);
            throw;
        }
    }

    public void Dispose()
    {
        if (Pointer != 0)
        {
            NativeMemory.Free((void*)Pointer);
            Pointer = 0;
        }
    }
}
