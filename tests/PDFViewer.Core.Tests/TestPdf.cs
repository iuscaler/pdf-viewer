using System.Text;

namespace PDFViewer.Core.Tests;

/// <summary>테스트용 최소 PDF 파일 생성기.</summary>
internal static class TestPdf
{
    public sealed record Page(double Width, double Height, string Content = "");

    /// <summary>임시 폴더에 PDF 파일을 만들고 경로를 돌려준다.</summary>
    public static string Create(params Page[] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "", // Pages 객체는 페이지 번호를 알고 난 뒤 채운다
        };

        var kids = new List<string>();
        foreach (var page in pages)
        {
            var pageId = objects.Count + 1;
            var contentId = pageId + 1;
            kids.Add($"{pageId} 0 R");
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.Width} {page.Height}] /Contents {contentId} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(page.Content)} >>\nstream\n{page.Content}\nendstream");
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {pages.Length} >>";

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        var path = Path.Combine(Path.GetTempPath(), $"pdfviewer-test-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        return path;
    }
}
