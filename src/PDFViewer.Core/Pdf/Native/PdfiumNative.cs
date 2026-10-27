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

    /// <summary>PDF 좌표(왼쪽 아래 원점)를 장치 좌표(왼쪽 위 원점, 페이지 회전 반영)로 바꾼다.</summary>
    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY,
        int rotate, double pageX, double pageY, out int deviceX, out int deviceY);

    // ---- 텍스트 (fpdf_text.h) ----

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_RECTF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    [LibraryImport(Lib)]
    public static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(Lib)]
    public static partial void FPDFText_ClosePage(nint textPage);

    [LibraryImport(Lib)]
    public static partial int FPDFText_CountChars(nint textPage);

    [LibraryImport(Lib)]
    public static partial uint FPDFText_GetUnicode(nint textPage, int index);

    /// <summary>PDFium이 줄바꿈 등을 위해 끼워 넣은 문자면 1.</summary>
    [LibraryImport(Lib)]
    public static partial int FPDFText_IsGenerated(nint textPage, int index);

    /// <summary>글꼴의 위아래 여백까지 포함한 글자 상자 (PDF 좌표).</summary>
    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFText_GetLooseCharBox(nint textPage, int index, out FS_RECTF rect);

    // ---- 메모리에서 열기, 좌표 변환 ----

    /// <summary>메모리 버퍼에서 문서를 연다. 버퍼는 문서를 닫을 때까지 유지해야 한다.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDF_LoadMemDocument64(nint dataBuffer, nuint size, string? password);

    /// <summary>장치 좌표(왼쪽 위 원점, 페이지 회전 반영)를 PDF 좌표로 바꾼다.</summary>
    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDF_DeviceToPage(nint page, int startX, int startY, int sizeX, int sizeY,
        int rotate, int deviceX, int deviceY, out double pageX, out double pageY);

    // ---- 주석 (fpdf_annot.h) ----

    public const int FPDF_ANNOT_HIGHLIGHT = 9;
    public const int FPDF_ANNOT_INK = 15;
    public const int FPDF_ANNOT_FLAG_PRINT = 1 << 2;
    public const int FPDF_ANNOT_APPEARANCEMODE_NORMAL = 0;
    public const int FPDFANNOT_COLORTYPE_Color = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_POINTF
    {
        public float X;
        public float Y;
    }

    /// <summary>사각형 네 꼭짓점. 관례상 왼쪽 위, 오른쪽 위, 왼쪽 아래, 오른쪽 아래 순서.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FS_QUADPOINTSF
    {
        public float X1, Y1, X2, Y2, X3, Y3, X4, Y4;
    }

    [LibraryImport(Lib)]
    public static partial nint FPDFPage_CreateAnnot(nint page, int subtype);

    [LibraryImport(Lib)]
    public static partial int FPDFPage_GetAnnotCount(nint page);

    [LibraryImport(Lib)]
    public static partial nint FPDFPage_GetAnnot(nint page, int index);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPage_RemoveAnnot(nint page, int index);

    [LibraryImport(Lib)]
    public static partial void FPDFPage_CloseAnnot(nint annot);

    [LibraryImport(Lib)]
    public static partial int FPDFAnnot_GetSubtype(nint annot);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_SetRect(nint annot, in FS_RECTF rect);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_SetColor(nint annot, int type, uint r, uint g, uint b, uint a);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_SetFlags(nint annot, int flags);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_SetBorder(nint annot, float horizontalRadius, float verticalRadius,
        float borderWidth);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_SetStringValue(nint annot,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);

    /// <summary>값을 UTF-16으로 buffer에 쓰고, 끝의 0을 포함한 바이트 수를 돌려준다.</summary>
    [LibraryImport(Lib)]
    public static unsafe partial uint FPDFAnnot_GetStringValue(nint annot,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, char* buffer, uint bufferLength);

    /// <summary>외관 스트림을 설정한다. 빈 문자열이면 비어 있는 스트림을 만든다(BBox = 주석 사각형).</summary>
    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_SetAP(nint annot, int appearanceMode,
        [MarshalAs(UnmanagedType.LPWStr)] string? value);

    /// <summary>Ink 주석의 InkList에 획 하나를 추가한다. 추가된 획의 번호를 돌려주고, 실패하면 -1.</summary>
    [LibraryImport(Lib)]
    public static partial int FPDFAnnot_AddInkStroke(nint annot, FS_POINTF[] points, nuint pointCount);

    /// <summary>경로 등의 페이지 객체를 주석 외관에 추가한다. 성공하면 객체의 소유권이 주석으로 넘어간다.</summary>
    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_AppendObject(nint annot, nint pageObject);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_AppendAttachmentPoints(nint annot, in FS_QUADPOINTSF quadPoints);

    // ---- 주석 읽기 ----

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_GetColor(nint annot, int type, out uint r, out uint g, out uint b,
        out uint a);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_GetBorder(nint annot, out float horizontalRadius, out float verticalRadius,
        out float borderWidth);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_GetNumberValue(nint annot,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key, out float value);

    /// <summary>외관 스트림 안의 객체 수 (Ink, Stamp 주석만).</summary>
    [LibraryImport(Lib)]
    public static partial int FPDFAnnot_GetObjectCount(nint annot);

    [LibraryImport(Lib)]
    public static partial nint FPDFAnnot_GetObject(nint annot, int index);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_GetStrokeColor(nint pageObject, out uint r, out uint g, out uint b,
        out uint a);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_GetFillColor(nint pageObject, out uint r, out uint g, out uint b,
        out uint a);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_GetDrawMode(nint path, out int fillMode,
        [MarshalAs(UnmanagedType.Bool)] out bool stroke);

    [LibraryImport(Lib)]
    public static partial uint FPDFAnnot_GetInkListCount(nint annot);

    /// <summary>InkList의 path 하나의 점들. buffer가 null이거나 작으면 점 개수만 돌려준다.</summary>
    [LibraryImport(Lib)]
    public static partial uint FPDFAnnot_GetInkListPath(nint annot, uint pathIndex,
        [Out] FS_POINTF[]? buffer, uint length);

    [LibraryImport(Lib)]
    public static partial nuint FPDFAnnot_CountAttachmentPoints(nint annot);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFAnnot_GetAttachmentPoints(nint annot, nuint quadIndex,
        out FS_QUADPOINTSF quadPoints);

    // ---- 페이지 객체 (fpdf_edit.h) ----

    public const int FPDF_FILLMODE_WINDING = 2;

    [LibraryImport(Lib)]
    public static partial nint FPDFPageObj_CreateNewPath(float x, float y);

    [LibraryImport(Lib)]
    public static partial void FPDFPageObj_Destroy(nint pageObject);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_MoveTo(nint path, float x, float y);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_LineTo(nint path, float x, float y);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_Close(nint path);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPath_SetDrawMode(nint path, int fillMode,
        [MarshalAs(UnmanagedType.Bool)] bool stroke);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFPageObj_SetFillColor(nint pageObject, uint r, uint g, uint b, uint a);

    [LibraryImport(Lib)]
    public static partial void FPDFPageObj_SetBlendMode(nint pageObject,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string blendMode);

    // ---- 저장 (fpdf_save.h) ----

    public const uint FPDF_INCREMENTAL = 1 << 0;

    /// <summary>
    /// FPDF_FILEWRITE 구조체. PDFium은 앞의 두 필드만 알고, 뒤의 Stream은 콜백에서 쓸 출력 대상(GCHandle)이다.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct FileWrite
    {
        public int Version;
        public delegate* unmanaged[Cdecl]<FileWrite*, void*, uint, int> WriteBlock;
        public nint Stream;
    }

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool FPDF_SaveAsCopy(nint document, FileWrite* fileWrite, uint flags);
}
