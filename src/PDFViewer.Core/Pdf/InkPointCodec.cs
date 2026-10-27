using System.Globalization;
using System.Text;
using PDFViewer.Core.Annotations;

namespace PDFViewer.Core.Pdf;

/// <summary>
/// 획의 점(페이지 좌표 + 필압)을 PDF 주석에 함께 저장할 문자열로 바꾸고 되돌린다.
/// 형식: "x y p x y p ..." (공백으로 구분, 좌표는 소수 둘째 자리, 필압은 셋째 자리까지).
/// </summary>
internal static class InkPointCodec
{
    public static string Encode(IReadOnlyList<InkPoint> points)
    {
        var sb = new StringBuilder(points.Count * 18);
        foreach (var p in points)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(p.X.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ')
                .Append(p.Y.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ')
                .Append(p.Pressure.ToString("0.###", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>되돌린다. 형식이 맞지 않으면 null.</summary>
    public static IReadOnlyList<InkPoint>? Decode(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length % 3 != 0)
        {
            return null;
        }

        var points = new InkPoint[parts.Length / 3];
        for (var i = 0; i < points.Length; i++)
        {
            if (!float.TryParse(parts[i * 3], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !float.TryParse(parts[i * 3 + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !float.TryParse(parts[i * 3 + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            {
                return null;
            }

            points[i] = new InkPoint(x, y, p);
        }

        return points;
    }
}
