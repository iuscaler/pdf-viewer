using PDFViewer.Core.Annotations;
using PDFViewer.Core.Pdf.Native;

namespace PDFViewer.Core.Pdf;

/// <summary>
/// 앱의 주석을 PDF 문서(메모리)에 실제 PDF 주석으로 넣고 뺀다.
/// PDF 안에 들어간 주석은 PDFium이 페이지와 함께 그리므로 Edge와 같은 모양(형광펜은 곱하기 합성)으로 보이고,
/// 저장하면 그대로 파일에 들어간다.
/// </summary>
public sealed partial class PdfDocument
{
    /// <summary>
    /// 펜/자유 형광펜 획을 Ink 주석으로 넣는다.
    /// </summary>
    /// <param name="outline">
    /// 화면에 그려지는 획의 윤곽선(페이지 좌표 다각형들). 필압과 곡선 보정이 반영된 모양을 외관으로 쓴다.
    /// </param>
    public Task AddInkAnnotationAsync(InkStroke stroke, IReadOnlyList<IReadOnlyList<PagePoint>> outline)
    {
        CheckPage(stroke.PageIndex);
        return PdfiumThread.InvokeAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var page = GetPageHandle(stroke.PageIndex);
            var size = PageSizes[stroke.PageIndex];
            var annot = CreateAnnot(page, PdfiumNative.FPDF_ANNOT_INK);
            try
            {
                var outlinePoints = outline.SelectMany(p => p).ToList();
                var bounds = outlinePoints.Count > 0
                    ? BoundsOf(outlinePoints)
                    : (stroke.Bounds.X, stroke.Bounds.Y, stroke.Bounds.Right, stroke.Bounds.Bottom);
                SetCommonProperties(annot, page, size, stroke, bounds);

                // 외관을 다시 만드는 다른 뷰어를 위해 원래 점 목록(InkList)과 굵기도 넣어 둔다.
                PdfiumNative.FPDFAnnot_SetBorder(annot, 0, 0, (float)stroke.Width);
                var inkList = stroke.Points.Select(p => ToPdfPoint(page, size, p.X, p.Y)).ToArray();
                PdfiumNative.FPDFAnnot_AddInkStroke(annot, inkList, (nuint)inkList.Length);

                // 외관: 윤곽선을 채운 경로. 형광펜은 곱하기 합성으로 아래 글자가 선명하게 남는다.
                PdfiumNative.FPDFAnnot_SetAP(annot, PdfiumNative.FPDF_ANNOT_APPEARANCEMODE_NORMAL, "");
                var path = CreateFilledPath(page, size, outline, stroke.Color);
                if (path != 0)
                {
                    if (stroke.IsHighlighter)
                    {
                        PdfiumNative.FPDFPageObj_SetBlendMode(path, "Multiply");
                    }

                    if (!PdfiumNative.FPDFAnnot_AppendObject(annot, path))
                    {
                        PdfiumNative.FPDFPageObj_Destroy(path);
                    }
                }
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        });
    }

    /// <summary>
    /// 텍스트 형광펜을 Highlight 주석으로 넣는다. 외관은 PDFium이 표준 방식(곱하기 합성)으로 만든다.
    /// </summary>
    public Task AddTextHighlightAsync(TextHighlight highlight)
    {
        CheckPage(highlight.PageIndex);
        return PdfiumThread.InvokeAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var page = GetPageHandle(highlight.PageIndex);
            var size = PageSizes[highlight.PageIndex];
            var annot = CreateAnnot(page, PdfiumNative.FPDF_ANNOT_HIGHLIGHT);
            try
            {
                var b = highlight.Bounds;
                SetCommonProperties(annot, page, size, highlight, (b.X, b.Y, b.Right, b.Bottom));
                foreach (var r in highlight.Rects)
                {
                    var tl = ToPdfPoint(page, size, r.X, r.Y);
                    var tr = ToPdfPoint(page, size, r.Right, r.Y);
                    var bl = ToPdfPoint(page, size, r.X, r.Bottom);
                    var br = ToPdfPoint(page, size, r.Right, r.Bottom);
                    var quad = new PdfiumNative.FS_QUADPOINTSF
                    {
                        X1 = tl.X, Y1 = tl.Y, X2 = tr.X, Y2 = tr.Y, X3 = bl.X, Y3 = bl.Y, X4 = br.X, Y4 = br.Y,
                    };
                    PdfiumNative.FPDFAnnot_AppendAttachmentPoints(annot, quad);
                }

                if (highlight.Text.Length > 0)
                {
                    PdfiumNative.FPDFAnnot_SetStringValue(annot, "Contents", highlight.Text);
                }
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        });
    }

    /// <summary>앱이 넣은 주석을 ID로 찾아 지운다. 지웠으면 true.</summary>
    public Task<bool> RemoveAnnotationAsync(int pageIndex, Guid id)
    {
        CheckPage(pageIndex);
        var name = NameOf(id);
        return PdfiumThread.InvokeAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var page = GetPageHandle(pageIndex);
            for (var i = PdfiumNative.FPDFPage_GetAnnotCount(page) - 1; i >= 0; i--)
            {
                if (GetAnnotName(page, i) == name)
                {
                    return PdfiumNative.FPDFPage_RemoveAnnot(page, i);
                }
            }

            return false;
        });
    }

    /// <summary>페이지에 있는 주석들의 이름(/NM). 이름이 없는 주석은 빈 문자열.</summary>
    public Task<IReadOnlyList<string>> GetAnnotationNamesAsync(int pageIndex)
    {
        CheckPage(pageIndex);
        return PdfiumThread.InvokeAsync<IReadOnlyList<string>>(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var page = GetPageHandle(pageIndex);
            var count = PdfiumNative.FPDFPage_GetAnnotCount(page);
            return Enumerable.Range(0, count).Select(i => GetAnnotName(page, i)).ToList();
        });
    }

    /// <summary>앱이 넣는 주석의 이름(/NM).</summary>
    public static string NameOf(Guid id) => $"pdfviewer-{id:D}";

    private void CheckPage(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
    }

    private static nint CreateAnnot(nint page, int subtype)
    {
        var annot = PdfiumNative.FPDFPage_CreateAnnot(page, subtype);
        return annot != 0 ? annot : throw new PdfException(PdfError.Unknown, "PDF 주석을 만들 수 없습니다.");
    }

    /// <summary>모든 주석에 공통인 영역, 색, 이름, 인쇄 플래그를 설정한다.</summary>
    private static void SetCommonProperties(nint annot, nint page, PageSize size, Annotation annotation,
        (double Left, double Top, double Right, double Bottom) bounds)
    {
        // 페이지 회전이 있으면 꼭짓점 위치가 바뀌므로 네 점을 모두 변환해서 감싸는 사각형을 만든다.
        var corners = new[]
        {
            ToPdfPoint(page, size, bounds.Left, bounds.Top), ToPdfPoint(page, size, bounds.Right, bounds.Top),
            ToPdfPoint(page, size, bounds.Left, bounds.Bottom), ToPdfPoint(page, size, bounds.Right, bounds.Bottom),
        };
        var rect = new PdfiumNative.FS_RECTF
        {
            Left = corners.Min(p => p.X) - 1,
            Right = corners.Max(p => p.X) + 1,
            Bottom = corners.Min(p => p.Y) - 1,
            Top = corners.Max(p => p.Y) + 1,
        };
        PdfiumNative.FPDFAnnot_SetRect(annot, rect);

        var c = annotation.Color;
        PdfiumNative.FPDFAnnot_SetColor(annot, PdfiumNative.FPDFANNOT_COLORTYPE_Color,
            (c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF, 0xFF);
        PdfiumNative.FPDFAnnot_SetStringValue(annot, "NM", NameOf(annotation.Id));
        PdfiumNative.FPDFAnnot_SetFlags(annot, PdfiumNative.FPDF_ANNOT_FLAG_PRINT);
    }

    /// <summary>다각형들을 하나의 채운 경로로 만든다 (겹친 부분은 한 번만 칠해지도록 nonzero 규칙).</summary>
    private static nint CreateFilledPath(nint page, PageSize size, IReadOnlyList<IReadOnlyList<PagePoint>> polygons,
        uint color)
    {
        nint path = 0;
        foreach (var polygon in polygons.Where(p => p.Count >= 3))
        {
            var start = ToPdfPoint(page, size, polygon[0].X, polygon[0].Y);
            if (path == 0)
            {
                path = PdfiumNative.FPDFPageObj_CreateNewPath(start.X, start.Y);
            }
            else
            {
                PdfiumNative.FPDFPath_MoveTo(path, start.X, start.Y);
            }

            for (var i = 1; i < polygon.Count; i++)
            {
                var p = ToPdfPoint(page, size, polygon[i].X, polygon[i].Y);
                PdfiumNative.FPDFPath_LineTo(path, p.X, p.Y);
            }

            PdfiumNative.FPDFPath_Close(path);
        }

        if (path != 0)
        {
            PdfiumNative.FPDFPageObj_SetFillColor(path, (color >> 16) & 0xFF, (color >> 8) & 0xFF, color & 0xFF, 0xFF);
            PdfiumNative.FPDFPath_SetDrawMode(path, PdfiumNative.FPDF_FILLMODE_WINDING, stroke: false);
        }

        return path;
    }

    /// <summary>페이지 좌표(왼쪽 위 원점, 화면에 보이는 방향, pt)를 PDF 좌표로 바꾼다. 페이지 회전이 반영된다.</summary>
    private static PdfiumNative.FS_POINTF ToPdfPoint(nint page, PageSize size, double x, double y)
    {
        var width = (int)Math.Round(size.Width * DevicePrecision);
        var height = (int)Math.Round(size.Height * DevicePrecision);
        PdfiumNative.FPDF_DeviceToPage(page, 0, 0, width, height, 0,
            (int)Math.Round(x * DevicePrecision), (int)Math.Round(y * DevicePrecision), out var px, out var py);
        return new PdfiumNative.FS_POINTF { X = (float)px, Y = (float)py };
    }

    private static (double Left, double Top, double Right, double Bottom) BoundsOf(IEnumerable<PagePoint> points)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var p in points)
        {
            left = Math.Min(left, p.X);
            top = Math.Min(top, p.Y);
            right = Math.Max(right, p.X);
            bottom = Math.Max(bottom, p.Y);
        }

        return (left, top, right, bottom);
    }

    private static unsafe string GetAnnotName(nint page, int index)
    {
        var annot = PdfiumNative.FPDFPage_GetAnnot(page, index);
        if (annot == 0)
        {
            return "";
        }

        try
        {
            var bytes = PdfiumNative.FPDFAnnot_GetStringValue(annot, "NM", null, 0);
            if (bytes <= 2)
            {
                return "";
            }

            var buffer = new char[bytes / 2];
            fixed (char* p = buffer)
            {
                PdfiumNative.FPDFAnnot_GetStringValue(annot, "NM", p, bytes);
            }

            return new string(buffer, 0, buffer.Length - 1); // 끝의 0 제외
        }
        finally
        {
            PdfiumNative.FPDFPage_CloseAnnot(annot);
        }
    }
}
