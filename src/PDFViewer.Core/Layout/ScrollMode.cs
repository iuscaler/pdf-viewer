namespace PDFViewer.Core.Layout;

public enum ScrollMode
{
    /// <summary>모든 페이지를 이어 붙여서 스크롤한다.</summary>
    Continuous,

    /// <summary>한 번에 한 줄(1페이지 보기면 한 페이지)만 보여주고, 끝에 닿으면 다음 페이지로 넘어간다.</summary>
    PageByPage,
}
