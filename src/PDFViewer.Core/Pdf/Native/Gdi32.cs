using System.Runtime.InteropServices;

namespace PDFViewer.Core.Pdf.Native;

/// <summary>프린터 인쇄에 쓰는 GDI 함수. PDFium이 프린터 DC에 직접 그린다(<see cref="PdfiumNative.FPDF_RenderPage"/>).</summary>
internal static partial class Gdi32
{
    private const string Lib = "gdi32.dll";

    // GetDeviceCaps 인덱스
    public const int HORZRES = 8;   // 인쇄 가능 영역 폭 (장치 픽셀)
    public const int VERTRES = 10;  // 인쇄 가능 영역 높이 (장치 픽셀)
    public const int LOGPIXELSX = 88;

    [StructLayout(LayoutKind.Sequential)]
    public struct DOCINFOW
    {
        public int cbSize;
        public nint lpszDocName;
        public nint lpszOutput;   // 파일 이름이면 프린터 대신 그 파일로 출력한다 (예: Microsoft Print to PDF)
        public nint lpszDatatype;
        public uint fwType;
    }

    /// <param name="devMode">DEVMODEW 구조체 (용지, 방향, 매수 등). 0이면 프린터 기본값.</param>
    [LibraryImport(Lib, EntryPoint = "CreateDCW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateDC(string? driver, string device, string? port, nint devMode);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport(Lib)]
    public static partial int GetDeviceCaps(nint hdc, int index);

    /// <returns>인쇄 작업 번호. 실패하면 0 이하.</returns>
    [LibraryImport(Lib, EntryPoint = "StartDocW", SetLastError = true)]
    public static partial int StartDoc(nint hdc, in DOCINFOW docInfo);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int StartPage(nint hdc);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int EndPage(nint hdc);

    [LibraryImport(Lib)]
    public static partial int EndDoc(nint hdc);

    [LibraryImport(Lib)]
    public static partial int AbortDoc(nint hdc);
}
