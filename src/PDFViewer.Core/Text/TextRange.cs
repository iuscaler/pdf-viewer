using System.Text;

namespace PDFViewer.Core.Text;

/// <summary>문서 안의 글자 위치: 페이지 번호와 그 페이지 안의 글자 번호.</summary>
public readonly record struct TextPosition(int Page, int Char) : IComparable<TextPosition>
{
    public int CompareTo(TextPosition other) =>
        Page != other.Page ? Page.CompareTo(other.Page) : Char.CompareTo(other.Char);
}

/// <summary>
/// 여러 페이지에 걸칠 수 있는 텍스트 선택 범위 (양 끝 글자 포함).
/// Anchor는 선택을 시작한 쪽, Focus는 드래그로 넓힌 쪽 끝이다.
/// </summary>
public readonly record struct TextRange(TextPosition Anchor, TextPosition Focus)
{
    public TextPosition Start => Anchor.CompareTo(Focus) <= 0 ? Anchor : Focus;

    public TextPosition End => Anchor.CompareTo(Focus) <= 0 ? Focus : Anchor;

    /// <summary>범위가 걸친 페이지들.</summary>
    public IEnumerable<int> Pages => Enumerable.Range(Start.Page, End.Page - Start.Page + 1);

    /// <summary>문서 전체 (Ctrl+A). 마지막 페이지의 글자 번호는 그 페이지의 마지막 글자로 맞춰진다.</summary>
    public static TextRange All(int pageCount) => new(new TextPosition(0, 0), new TextPosition(pageCount - 1, int.MaxValue));

    public bool ContainsPage(int page) => page >= Start.Page && page <= End.Page;

    /// <summary>
    /// 이 범위 중 한 페이지에 들어가는 글자 범위 (양 끝 포함). 첫 페이지와 마지막 페이지 사이의 페이지는 전체가 들어간다.
    /// 페이지가 범위 밖이거나 글자가 없으면 null.
    /// </summary>
    public (int From, int To)? OnPage(int page, int charCount)
    {
        if (!ContainsPage(page) || charCount == 0)
        {
            return null;
        }

        var (start, end) = (Start, End);
        var from = page == start.Page ? Math.Min(start.Char, charCount - 1) : 0;
        var to = page == end.Page ? Math.Min(end.Char, charCount - 1) : charCount - 1;
        return (from, to);
    }

    /// <summary>범위의 글자를 이어 붙인 문자열. 페이지가 바뀌는 곳에는 줄바꿈을 넣는다.</summary>
    /// <param name="pageText">페이지 번호로 그 페이지의 글자 정보를 돌려주는 함수. 범위 안의 모든 페이지에 대해 불린다.</param>
    public string GetText(Func<int, PageText> pageText)
    {
        var sb = new StringBuilder();
        foreach (var page in Pages)
        {
            var text = pageText(page);
            if (OnPage(page, text.Chars.Count) is not { } range)
            {
                continue;
            }

            if (sb.Length > 0 && sb[^1] != '\n')
            {
                sb.Append("\r\n");
            }

            sb.Append(text.GetText(range.From, range.To));
        }

        return sb.ToString();
    }
}
