using PDFViewer.Core.Pdf.Native;

namespace PDFViewer.Core.Pdf;

public enum PdfError
{
    Unknown,
    File,
    Format,
    Password,
    Security,
    Page,
}

public sealed class PdfException : Exception
{
    public PdfException(PdfError error, string message) : base(message)
    {
        Error = error;
    }

    public PdfError Error { get; }

    /// <summary>FPDF_GetLastError 값으로 예외를 만든다. PDFium 스레드에서 호출해야 한다.</summary>
    internal static PdfException FromLastError()
    {
        var code = PdfiumNative.FPDF_GetLastError();
        return code switch
        {
            PdfiumNative.FPDF_ERR_FILE => new(PdfError.File, "파일을 찾을 수 없거나 열 수 없습니다."),
            PdfiumNative.FPDF_ERR_FORMAT => new(PdfError.Format, "PDF 파일이 아니거나 손상된 파일입니다."),
            PdfiumNative.FPDF_ERR_PASSWORD => new(PdfError.Password, "암호가 필요하거나 암호가 올바르지 않습니다."),
            PdfiumNative.FPDF_ERR_SECURITY => new(PdfError.Security, "지원하지 않는 보안 방식입니다."),
            PdfiumNative.FPDF_ERR_PAGE => new(PdfError.Page, "페이지를 찾을 수 없거나 페이지 내용에 오류가 있습니다."),
            _ => new(PdfError.Unknown, $"알 수 없는 PDFium 오류입니다. (코드 {code})"),
        };
    }
}
