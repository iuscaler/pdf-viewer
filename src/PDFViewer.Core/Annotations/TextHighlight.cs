using PDFViewer.Core.Layout;

namespace PDFViewer.Core.Annotations;

/// <summary>
/// 텍스트를 골라서 칠한 형광펜. 줄마다 사각형 하나씩이며, PDF로 저장할 때는 Highlight 주석의 QuadPoints가 된다.
/// </summary>
public sealed class TextHighlight : Annotation
{
    public TextHighlight(int pageIndex, IReadOnlyList<LayoutRect> rects, uint color, string text)
        : base(pageIndex, color)
    {
        if (rects.Count == 0)
        {
            throw new ArgumentException("형광펜 영역이 하나 이상 있어야 합니다.", nameof(rects));
        }

        Rects = rects;
        Text = text;
        Bounds = rects.Aggregate((a, b) =>
        {
            var x = Math.Min(a.X, b.X);
            var y = Math.Min(a.Y, b.Y);
            return new LayoutRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
        });
    }

    /// <summary>줄마다 칠한 영역.</summary>
    public IReadOnlyList<LayoutRect> Rects { get; }

    /// <summary>칠한 글자들. PDF 주석의 내용(Contents)으로 저장할 수 있다.</summary>
    public string Text { get; }

    public override LayoutRect Bounds { get; }

    public override bool HitTest(double x, double y, double radius) =>
        Rects.Any(r => x >= r.X - radius && x <= r.Right + radius && y >= r.Y - radius && y <= r.Bottom + radius);
}
