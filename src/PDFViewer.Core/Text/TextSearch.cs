using System.Text;
using PDFViewer.Core.Layout;

namespace PDFViewer.Core.Text;

/// <summary>검색 결과 하나. 줄마다 사각형 하나 (페이지 좌표, 포인트).</summary>
public sealed record SearchMatch(int PageIndex, int StartChar, int EndChar, IReadOnlyList<LayoutRect> Rects);

/// <summary>
/// 페이지 텍스트에서 문자열 찾기. 대소문자를 구분하지 않고, 줄바꿈과 연속된 공백은 공백 하나로 본다
/// (그래서 두 줄에 걸친 문구도 찾는다).
/// </summary>
public static class TextSearch
{
    public static IReadOnlyList<SearchMatch> Find(PageText text, int pageIndex, string query)
    {
        var needle = Normalize(query, out _).Trim();
        if (needle.Length == 0 || text.Chars.Count == 0)
        {
            return [];
        }

        var haystack = Normalize(text.Chars.Select(c => c.Value), out var map);
        var matches = new List<SearchMatch>();
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var start = map[index];
            var end = map[index + needle.Length - 1];
            var rects = text.GetSelectionRects(start, end);
            if (rects.Count > 0)
            {
                matches.Add(new SearchMatch(pageIndex, start, end, rects));
            }

            index += needle.Length;
        }

        return matches;
    }

    private static string Normalize(IEnumerable<char> chars, out List<int> map)
    {
        var sb = new StringBuilder();
        map = [];
        var i = -1;
        foreach (var c in chars)
        {
            i++;
            if (c == '\0')
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                // 공백이 여러 개 이어지면 하나만 남긴다.
                if (sb.Length > 0 && sb[^1] != ' ')
                {
                    sb.Append(' ');
                    map.Add(i);
                }

                continue;
            }

            sb.Append(c);
            map.Add(i);
        }

        return sb.ToString();
    }
}
