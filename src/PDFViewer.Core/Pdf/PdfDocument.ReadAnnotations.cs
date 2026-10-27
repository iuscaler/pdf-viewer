using PDFViewer.Core.Annotations;
using PDFViewer.Core.Layout;
using PDFViewer.Core.Pdf.Native;

namespace PDFViewer.Core.Pdf;

/// <summary>
/// 파일에 이미 들어 있는 펜(Ink)과 형광펜(Highlight) 주석을 앱의 주석으로 읽는다.
/// 이 앱이 저장한 주석은 함께 저장해 둔 정보로 그대로 되살리고, 다른 프로그램의 주석은 PDF 표준 정보로 읽는다.
/// </summary>
public sealed partial class PdfDocument
{
    // 다른 프로그램의 Ink 주석에 굵기 정보가 없을 때 쓰는 굵기 (포인트)
    private const float DefaultInkWidth = 1;

    // 이 투명도보다 투명한 Ink 주석은 형광펜으로 본다.
    private const float HighlighterOpacity = 0.95f;

    // 이 문서에서 이미 쓰인 주석 이름. 이름이 없거나 겹치는 주석에는 새 이름을 붙인다.
    private readonly HashSet<string> _readAnnotationNames = [];

    /// <summary>
    /// 한 페이지의 펜·형광펜 주석을 읽는다. 이름(/NM)이 없거나 다른 주석과 겹치면 메모리 안의 문서에서 새 이름을 붙인다
    /// (지우개와 실행 취소가 이름으로 PDF 주석을 찾기 때문). 이 이름은 저장할 때 파일에도 들어간다.
    /// </summary>
    public Task<IReadOnlyList<Annotation>> ReadAnnotationsAsync(int pageIndex)
    {
        CheckPage(pageIndex);
        return PdfiumThread.InvokeAsync<IReadOnlyList<Annotation>>(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var page = GetPageHandle(pageIndex);
            var size = PageSizes[pageIndex];
            var result = new List<Annotation>();
            var count = PdfiumNative.FPDFPage_GetAnnotCount(page);
            for (var i = 0; i < count; i++)
            {
                var annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
                if (annot == 0)
                {
                    continue;
                }

                try
                {
                    var subtype = PdfiumNative.FPDFAnnot_GetSubtype(annot);
                    if (subtype is not (PdfiumNative.FPDF_ANNOT_INK or PdfiumNative.FPDF_ANNOT_HIGHLIGHT))
                    {
                        continue;
                    }

                    var name = EnsureUniqueName(annot);
                    var color = ReadColor(annot, subtype == PdfiumNative.FPDF_ANNOT_HIGHLIGHT ? 0xFFFFEB3B : 0xFF000000);
                    if (subtype == PdfiumNative.FPDF_ANNOT_INK)
                    {
                        result.AddRange(ReadInk(annot, page, size, pageIndex, color, name));
                    }
                    else if (ReadHighlight(annot, page, size, pageIndex, color, name) is { } highlight)
                    {
                        result.Add(highlight);
                    }
                }
                finally
                {
                    PdfiumNative.FPDFPage_CloseAnnot(annot);
                }
            }

            return result;
        });
    }

    private string EnsureUniqueName(nint annot)
    {
        var name = GetAnnotString(annot, "NM");
        if (name.Length == 0 || !_readAnnotationNames.Add(name))
        {
            name = NameOf(Guid.NewGuid());
            _readAnnotationNames.Add(name);
            PdfiumNative.FPDFAnnot_SetStringValue(annot, "NM", name);
        }

        return name;
    }

    /// <summary>
    /// 주석의 색. FPDFAnnot_GetColor는 외관이 이미 있는 주석에서는 실패하므로 다음 순서로 찾는다:
    /// 이 앱이 저장해 둔 색 → /C (외관이 없을 때) → 외관 안 첫 경로 객체의 선/채우기 색 → 기본값.
    /// </summary>
    private static uint ReadColor(nint annot, uint fallback)
    {
        var saved = GetAnnotString(annot, ColorKey);
        if (saved.Length == 9 && saved[0] == '#'
            && uint.TryParse(saved.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var argb))
        {
            return argb;
        }

        if (PdfiumNative.FPDFAnnot_GetColor(annot, PdfiumNative.FPDFANNOT_COLORTYPE_Color, out var r, out var g,
                out var b, out _))
        {
            return Rgb(r, g, b);
        }

        var objects = PdfiumNative.FPDFAnnot_GetObjectCount(annot);
        for (var i = 0; i < objects; i++)
        {
            var obj = PdfiumNative.FPDFAnnot_GetObject(annot, i);
            if (obj == 0)
            {
                continue;
            }

            var strokes = PdfiumNative.FPDFPath_GetDrawMode(obj, out var fillMode, out var stroke) && stroke;
            if (strokes && PdfiumNative.FPDFPageObj_GetStrokeColor(obj, out r, out g, out b, out _))
            {
                return Rgb(r, g, b);
            }

            if (fillMode != 0 && PdfiumNative.FPDFPageObj_GetFillColor(obj, out r, out g, out b, out _))
            {
                return Rgb(r, g, b);
            }
        }

        return fallback;
    }

    private static uint Rgb(uint r, uint g, uint b) => 0xFF000000 | (r & 0xFF) << 16 | (g & 0xFF) << 8 | (b & 0xFF);

    /// <summary>
    /// Ink 주석 하나를 획들로 읽는다. 이 앱이 저장한 주석은 저장해 둔 점(필압 포함)을 쓰고,
    /// 다른 프로그램의 주석은 InkList의 path마다 획 하나를 만든다 (모두 같은 이름이라 함께 지워진다).
    /// </summary>
    private static IEnumerable<InkStroke> ReadInk(nint annot, nint page, PageSize size, int pageIndex, uint color,
        string name)
    {
        var width = PdfiumNative.FPDFAnnot_GetBorder(annot, out _, out _, out var w) && w > 0 ? w : DefaultInkWidth;
        var kind = GetAnnotString(annot, KindKey);
        if (kind.Length > 0 && InkPointCodec.Decode(GetAnnotString(annot, PointsKey)) is { Count: > 0 } saved)
        {
            yield return new InkStroke(pageIndex, saved, color, width, kind == KindHighlighter) { Name = name };
            yield break;
        }

        var isHighlighter = PdfiumNative.FPDFAnnot_GetNumberValue(annot, "CA", out var opacity)
                            && opacity < HighlighterOpacity;
        var pathCount = PdfiumNative.FPDFAnnot_GetInkListCount(annot);
        for (uint path = 0; path < pathCount; path++)
        {
            var length = PdfiumNative.FPDFAnnot_GetInkListPath(annot, path, null, 0);
            if (length == 0)
            {
                continue;
            }

            var buffer = new PdfiumNative.FS_POINTF[length];
            PdfiumNative.FPDFAnnot_GetInkListPath(annot, path, buffer, length);
            var points = buffer.Select(p =>
            {
                var (x, y) = ToPagePoint(page, size, p.X, p.Y);
                return new InkPoint((float)x, (float)y, 0.5f);
            }).ToArray();
            yield return new InkStroke(pageIndex, points, color, width, isHighlighter) { Name = name };
        }
    }

    private static TextHighlight? ReadHighlight(nint annot, nint page, PageSize size, int pageIndex, uint color,
        string name)
    {
        var rects = new List<LayoutRect>();
        var count = PdfiumNative.FPDFAnnot_CountAttachmentPoints(annot);
        for (nuint i = 0; i < count; i++)
        {
            if (!PdfiumNative.FPDFAnnot_GetAttachmentPoints(annot, i, out var q))
            {
                continue;
            }

            // 네 꼭짓점을 페이지 좌표로 바꿔서 감싸는 사각형으로 만든다.
            var corners = new[]
            {
                ToPagePoint(page, size, q.X1, q.Y1), ToPagePoint(page, size, q.X2, q.Y2),
                ToPagePoint(page, size, q.X3, q.Y3), ToPagePoint(page, size, q.X4, q.Y4),
            };
            double left = corners.Min(c => c.X), top = corners.Min(c => c.Y);
            rects.Add(new LayoutRect(left, top, corners.Max(c => c.X) - left, corners.Max(c => c.Y) - top));
        }

        return rects.Count > 0
            ? new TextHighlight(pageIndex, rects, color, GetAnnotString(annot, "Contents")) { Name = name }
            : null;
    }

    /// <summary>PDF 좌표를 페이지 좌표(왼쪽 위 원점, 화면에 보이는 방향, pt)로 바꾼다. 페이지 회전이 반영된다.</summary>
    private static (double X, double Y) ToPagePoint(nint page, PageSize size, double x, double y)
    {
        var width = (int)Math.Round(size.Width * DevicePrecision);
        var height = (int)Math.Round(size.Height * DevicePrecision);
        PdfiumNative.FPDF_PageToDevice(page, 0, 0, width, height, 0, x, y, out var dx, out var dy);
        return (dx / (double)DevicePrecision, dy / (double)DevicePrecision);
    }
}
