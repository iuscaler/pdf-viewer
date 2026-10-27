using System.Security.Cryptography;
using System.Text;

namespace PDFViewer.Core.Tests;

/// <summary>테스트용 최소 PDF 파일 생성기.</summary>
internal static class TestPdf
{
    /// <param name="Content">페이지 내용 스트림. 글꼴은 /F1 (Helvetica)을 쓸 수 있다.</param>
    /// <param name="Rotate">페이지 회전 (/Rotate, 90의 배수)</param>
    /// <param name="Annots">페이지에 넣을 주석 사전들 (다른 프로그램이 만든 주석 흉내)</param>
    /// <param name="Appearances">
    /// 주석마다 외관 스트림 (Annots와 같은 순서, 없으면 null). 형식: "BBox 숫자 4개|내용 스트림".
    /// </param>
    public sealed record Page(double Width, double Height, string Content = "", int Rotate = 0,
        string[]? Annots = null, string?[]? Appearances = null);

    /// <summary>임시 폴더에 PDF 파일을 만들고 경로를 돌려준다.</summary>
    public static string Create(params Page[] pages) => Build(pages, userPassword: null);

    /// <summary>
    /// 암호로 보호된 PDF를 만든다 (표준 보안 처리기, RC4 40비트, 개정 2). 내용 스트림이 암호화된다.
    /// </summary>
    public static string CreateEncrypted(string userPassword, params Page[] pages) => Build(pages, userPassword);

    private static string Build(Page[] pages, string? userPassword)
    {
        var security = userPassword is null ? null : new Rc4Security(userPassword, "owner-" + userPassword);
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Array.Empty<byte>(), // Pages 객체는 페이지 번호를 알고 난 뒤 채운다
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        };

        var kids = new List<string>();
        foreach (var page in pages)
        {
            var pageId = objects.Count + 1;
            var contentId = pageId + 1;
            var annots = page.Annots ?? [];
            var annotRefs = string.Join(' ', annots.Select((_, i) => $"{contentId + 1 + i} 0 R"));
            kids.Add($"{pageId} 0 R");
            objects.Add(Ascii(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.Width} {page.Height}] /Rotate {page.Rotate} " +
                $"/Resources << /Font << /F1 3 0 R >> >> /Contents {contentId} 0 R" +
                (annots.Length > 0 ? $" /Annots [{annotRefs}]" : "") + " >>"));
            var content = Encoding.Latin1.GetBytes(page.Content);
            if (security is not null)
            {
                content = security.Encrypt(contentId, content);
            }

            objects.Add([.. Ascii($"<< /Length {content.Length} >>\nstream\n"), .. content, .. Ascii("\nendstream")]);

            // 외관 스트림은 주석들 뒤에 둔다: 주석 i의 외관은 (첫 주석 번호 + 주석 수 + i)번 객체
            var firstAnnotId = contentId + 1;
            var appearances = page.Appearances ?? [];
            var appearanceIds = new Dictionary<int, int>();
            for (var i = 0; i < annots.Length; i++)
            {
                var dict = annots[i];
                if (i < appearances.Length && appearances[i] is not null)
                {
                    appearanceIds[i] = firstAnnotId + annots.Length + appearanceIds.Count;
                    dict = dict[..dict.LastIndexOf(">>", StringComparison.Ordinal)] +
                           $"/AP << /N {appearanceIds[i]} 0 R >> >>";
                }

                objects.Add(Ascii(dict));
            }

            foreach (var (i, _) in appearanceIds.OrderBy(p => p.Value))
            {
                var parts = appearances[i]!.Split('|', 2);
                objects.Add(Ascii($"<< /Type /XObject /Subtype /Form /BBox [{parts[0]}] /Length {parts[1].Length} >>" +
                                  $"\nstream\n{parts[1]}\nendstream"));
            }
        }

        objects[1] = Ascii($"<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {pages.Length} >>");
        var trailerExtra = "";
        if (security is not null)
        {
            objects.Add(Ascii(security.EncryptDictionary));
            trailerExtra = $" /Encrypt {objects.Count} 0 R /ID [<{security.FileIdHex}> <{security.FileIdHex}>]";
        }

        using var output = new MemoryStream();
        output.Write(Ascii("%PDF-1.4\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            output.Write(Ascii($"{i + 1} 0 obj\n"));
            output.Write(objects[i]);
            output.Write(Ascii("\nendobj\n"));
        }

        var xref = output.Position;
        var sb = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        output.Write(Ascii(sb.ToString()));

        var path = Path.Combine(Path.GetTempPath(), $"pdfviewer-test-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, output.ToArray());
        return path;
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>PDF 표준 보안 처리기 개정 2 (RC4 40비트).</summary>
    private sealed class Rc4Security
    {
        private static readonly byte[] Padding = Convert.FromHexString(
            "28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");

        private const int Permissions = -4;
        private readonly byte[] _key;
        private readonly byte[] _owner;
        private readonly byte[] _user;
        private readonly byte[] _fileId = MD5.HashData("pdfviewer-test"u8);

        public Rc4Security(string userPassword, string ownerPassword)
        {
            _owner = Rc4(MD5.HashData(Pad(ownerPassword))[..5], Pad(userPassword));
            _key = MD5.HashData([.. Pad(userPassword), .. _owner, .. BitConverter.GetBytes(Permissions), .. _fileId])[..5];
            _user = Rc4(_key, Padding);
        }

        public string FileIdHex => Convert.ToHexString(_fileId);

        public string EncryptDictionary =>
            $"<< /Filter /Standard /V 1 /R 2 /O <{Convert.ToHexString(_owner)}> /U <{Convert.ToHexString(_user)}> " +
            $"/P {Permissions} >>";

        public byte[] Encrypt(int objectNumber, byte[] data)
        {
            var objectKey = MD5.HashData(
                [.. _key, .. BitConverter.GetBytes(objectNumber)[..3], 0, 0])[..Math.Min(_key.Length + 5, 16)];
            return Rc4(objectKey, data);
        }

        private static byte[] Pad(string password) =>
            [.. Encoding.Latin1.GetBytes(password).Concat(Padding).Take(32)];

        private static byte[] Rc4(byte[] key, byte[] data)
        {
            var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            for (int i = 0, j = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 0xFF;
                (s[i], s[j]) = (s[j], s[i]);
            }

            var result = new byte[data.Length];
            for (int n = 0, i = 0, j = 0; n < data.Length; n++)
            {
                i = (i + 1) & 0xFF;
                j = (j + s[i]) & 0xFF;
                (s[i], s[j]) = (s[j], s[i]);
                result[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
            }

            return result;
        }
    }
}
