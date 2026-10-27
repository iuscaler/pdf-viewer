using PDFViewer.Core.Layout;

namespace PDFViewer.Core.Annotations;

/// <summary>페이지 좌표의 점 (포인트, 페이지 왼쪽 위 원점, 화면에 보이는 방향).</summary>
public readonly record struct PagePoint(double X, double Y);

/// <summary>
/// 페이지에 붙는 주석. 좌표는 페이지 왼쪽 위를 원점으로 하는 포인트 단위(화면에 보이는 방향 기준)다.
/// 만든 뒤에는 바뀌지 않으며, 실행 취소와 지우개가 같은 주석을 가리킬 수 있도록 값이 아닌 참조로 비교한다.
/// </summary>
public abstract class Annotation
{
    protected Annotation(int pageIndex, uint color)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        PageIndex = pageIndex;
        Color = color;
    }

    /// <summary>
    /// 주석 고유 ID. PDF 주석의 이름(/NM)으로 저장해서, 실행 취소나 지우개로 지울 때 PDF 안의 주석을 찾는 데 쓴다.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    public int PageIndex { get; }

    /// <summary>ARGB 색상 (0xAARRGGBB).</summary>
    public uint Color { get; }

    /// <summary>주석이 차지하는 영역 (포인트, 페이지 좌표).</summary>
    public abstract LayoutRect Bounds { get; }

    /// <summary>점 (x, y)에서 반지름 radius 안에 주석이 닿는지 여부. 모두 포인트 단위.</summary>
    public abstract bool HitTest(double x, double y, double radius);
}
