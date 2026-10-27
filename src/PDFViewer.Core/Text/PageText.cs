using System.Text;
using PDFViewer.Core.Layout;

namespace PDFViewer.Core.Text;

/// <summary>
/// 글자 하나. 상자는 페이지 왼쪽 위를 원점으로 하는 포인트 좌표(화면에 보이는 방향)다.
/// PDFium이 줄바꿈 등을 위해 끼워 넣은 문자(Generated)는 상자가 비어 있을 수 있다.
/// </summary>
public readonly record struct TextChar(char Value, LayoutRect Box, bool IsGenerated)
{
    public bool HasBox => Box.Width > 0 && Box.Height > 0;
}

/// <summary>
/// 한 페이지의 글자 목록과 텍스트 선택 계산. 만든 뒤에는 바뀌지 않으므로 어느 스레드에서 써도 된다.
/// </summary>
public sealed class PageText
{
    // 글자 위를 눌렀는지 판정할 때 상자 바깥으로 허용하는 거리 (포인트)
    private const double CharTolerance = 1;

    public PageText(IReadOnlyList<TextChar> chars)
    {
        Chars = chars;
    }

    public static PageText Empty { get; } = new([]);

    public IReadOnlyList<TextChar> Chars { get; }

    /// <summary>점 (x, y)에 있는 글자. 없으면 null.</summary>
    public int? CharAt(double x, double y)
    {
        for (var i = 0; i < Chars.Count; i++)
        {
            var box = Chars[i].Box;
            if (Chars[i].HasBox && x >= box.X - CharTolerance && x <= box.Right + CharTolerance
                && y >= box.Y - CharTolerance && y <= box.Bottom + CharTolerance)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// 점 (x, y)에서 가장 가까운 글자. 드래그로 선택 범위를 넓힐 때 글자 사이나 줄 사이를 지나도 끊기지 않게 한다.
    /// 세로 거리를 더 크게 쳐서, 같은 줄의 글자가 다른 줄의 글자보다 우선한다. 글자가 없으면 null.
    /// </summary>
    public int? NearestChar(double x, double y)
    {
        int? best = null;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < Chars.Count; i++)
        {
            if (!Chars[i].HasBox)
            {
                continue;
            }

            var box = Chars[i].Box;
            var dx = x < box.X ? box.X - x : x > box.Right ? x - box.Right : 0;
            var dy = y < box.Y ? box.Y - y : y > box.Bottom ? y - box.Bottom : 0;
            var distance = dx + dy * 4;
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// 두 글자 사이(양 끝 포함)를 줄 단위 사각형으로 묶는다. 형광펜과 선택 영역 표시에 쓴다.
    /// </summary>
    public IReadOnlyList<LayoutRect> GetSelectionRects(int from, int to)
    {
        var (start, end) = Order(from, to);
        var rects = new List<LayoutRect>();
        LayoutRect? current = null;
        for (var i = start; i <= end; i++)
        {
            if (!Chars[i].HasBox)
            {
                continue;
            }

            var box = Chars[i].Box;
            if (current is { } r && IsSameLine(r, box))
            {
                current = Union(r, box);
            }
            else
            {
                if (current is { } done)
                {
                    rects.Add(done);
                }

                current = box;
            }
        }

        if (current is { } last)
        {
            rects.Add(last);
        }

        return rects;
    }

    /// <summary>두 글자 사이(양 끝 포함)의 문자열. PDFium이 넣은 줄바꿈도 포함한다.</summary>
    public string GetText(int from, int to)
    {
        var (start, end) = Order(from, to);
        var sb = new StringBuilder(end - start + 1);
        for (var i = start; i <= end; i++)
        {
            if (Chars[i].Value != '\0')
            {
                sb.Append(Chars[i].Value);
            }
        }

        return sb.ToString();
    }

    private (int Start, int End) Order(int a, int b)
    {
        var start = Math.Clamp(Math.Min(a, b), 0, Chars.Count - 1);
        var end = Math.Clamp(Math.Max(a, b), 0, Chars.Count - 1);
        return (start, end);
    }

    // 세로로 절반 이상 겹치고, 오른쪽으로 이어지면 같은 줄로 본다(다음 줄로 넘어가면 왼쪽으로 돌아간다).
    private static bool IsSameLine(LayoutRect line, LayoutRect box)
    {
        var overlap = Math.Min(line.Bottom, box.Bottom) - Math.Max(line.Y, box.Y);
        return overlap >= Math.Min(line.Height, box.Height) / 2 && box.X >= line.X - 1;
    }

    private static LayoutRect Union(LayoutRect a, LayoutRect b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new LayoutRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }
}
