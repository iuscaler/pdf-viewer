using System.Runtime.InteropServices;

namespace PDFViewer.Core.Pdf.Native;

/// <summary>
/// PDFium C API 바인딩. 필요한 함수만 직접 선언한다.
/// 모든 호출은 반드시 <see cref="PdfiumThread"/> 위에서 이루어져야 한다.
/// </summary>
internal static partial class PdfiumNative
{
    private const string Lib = "pdfium";

    // FPDF_GetLastError 오류 코드
    public const uint FPDF_ERR_SUCCESS = 0;
    public const uint FPDF_ERR_UNKNOWN = 1;
    public const uint FPDF_ERR_FILE = 2;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;
    public const uint FPDF_ERR_SECURITY = 5;
    public const uint FPDF_ERR_PAGE = 6;

    // FPDF_RenderPageBitmap 플래그
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;

    // FPDFBitmap 포맷
    public const int FPDFBitmap_BGRx = 3;
    public const int FPDFBitmap_BGRA = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_SIZEF
    {
        public float Width;
        public float Height;
    }

    [LibraryImport(Lib)]
    public static partial void FPDF_InitLibrary();

    [LibraryImport(Lib)]
    public static partial void FPDF_DestroyLibrary();

    [LibraryImport(Lib)]
    public static partial uint FPDF_GetLastError();

    /// <param name="filePath">UTF-8 경로</param>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDF_LoadDocument(string filePath, string? password);

    [LibraryImport(Lib)]
    public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Lib)]
    public static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDF_GetPageSizeByIndexF(nint document, int pageIndex, out FS_SIZEF size);

    [LibraryImport(Lib)]
    public static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Lib)]
    public static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Lib)]
    public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, nint firstScan, int stride);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Lib)]
    public static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmap(
        nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
}
