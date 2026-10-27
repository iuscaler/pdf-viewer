using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PDFViewer.Core.Pdf.Native;

namespace PDFViewer.Core.Pdf;

/// <summary>PDF 파일로 저장.</summary>
public sealed partial class PdfDocument
{
    /// <summary>
    /// 문서를 파일로 저장한다. 같은 폴더의 임시 파일에 먼저 쓰고, 다 쓰면 대상 파일과 바꾼다.
    /// 그래서 저장 중에 오류가 나도 원본이 깨지지 않는다.
    /// 원본 내용은 그대로 두고 바뀐 부분만 뒤에 덧붙이는 증분 저장을 쓴다.
    /// </summary>
    public Task SaveAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return PdfiumThread.InvokeAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var directory = Path.GetDirectoryName(fullPath)!;
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    WriteTo(stream);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(fullPath))
                {
                    File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, fullPath);
                }
            }
            catch
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // 임시 파일을 지우지 못해도 원래 오류를 알리는 것이 중요하다.
                }

                throw;
            }

            FilePath = fullPath;
        });
    }

    private unsafe void WriteTo(Stream stream)
    {
        var handle = GCHandle.Alloc(stream);
        try
        {
            var fileWrite = new PdfiumNative.FileWrite
            {
                Version = 1,
                WriteBlock = &WriteBlock,
                Stream = GCHandle.ToIntPtr(handle),
            };

            if (!PdfiumNative.FPDF_SaveAsCopy(_handle, &fileWrite, PdfiumNative.FPDF_INCREMENTAL))
            {
                throw new PdfException(PdfError.Unknown, "PDF를 저장하지 못했습니다.");
            }
        }
        finally
        {
            handle.Free();
        }
    }

    // PDFium이 저장할 내용을 조각조각 넘겨줄 때 호출된다. 성공하면 1, 실패하면 0.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int WriteBlock(PdfiumNative.FileWrite* self, void* data, uint size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(self->Stream).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, checked((int)size)));
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
