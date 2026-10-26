namespace PDFViewer.Core.Layout;

public enum ZoomMode
{
    /// <summary>사용자가 지정한 배율.</summary>
    Custom,

    /// <summary>페이지 너비를 화면 너비에 맞춘다.</summary>
    FitWidth,

    /// <summary>페이지 전체가 화면에 들어오게 맞춘다.</summary>
    FitPage,
}
