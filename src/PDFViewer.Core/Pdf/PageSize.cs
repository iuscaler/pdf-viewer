namespace PDFViewer.Core.Pdf;

/// <summary>페이지 크기 (포인트 단위, 1pt = 1/72인치). 페이지 회전이 반영된 값이다.</summary>
public readonly record struct PageSize(double Width, double Height);
