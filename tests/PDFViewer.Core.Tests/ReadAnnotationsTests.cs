using PDFViewer.Core.Annotations;
using PDFViewer.Core.Layout;
using PDFViewer.Core.Pdf;
using PDFViewer.Core.Rendering;

namespace PDFViewer.Core.Tests;

/// <summary>파일에 이미 들어 있는 주석 읽기 (이 앱이 저장한 주석, 다른 프로그램의 주석).</summary>
public sealed class ReadAnnotationsTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }

    private string Track(string path)
    {
        _files.Add(path);
        return path;
    }

    [Fact]
    public async Task OwnAnnotations_RoundTripExactly()
    {
        var path = Track(TestPdf.Create(new TestPdf.Page(595, 842)));
        var pen = new InkStroke(0, [new InkPoint(100, 100, 0.2f), new InkPoint(150, 120, 0.8f), new InkPoint(200, 110, 0.5f)],
            0xFFE81123, 3);
        var marker = new InkStroke(0, [new InkPoint(100, 300, 0.5f), new InkPoint(300, 300, 0.5f)], 0xFFFFEB3B, 12,
            isHighlighter: true);
        var highlight = new TextHighlight(0, [new LayoutRect(72, 500, 120, 14)], 0xFF8BE04E, "hello");

        await using (var doc = await PdfDocument.OpenAsync(path))
        {
            await doc.AddInkAnnotationAsync(pen, Band(pen.Bounds));
            await doc.AddInkAnnotationAsync(marker, Band(marker.Bounds));
            await doc.AddTextHighlightAsync(highlight);
            await doc.SaveAsync(path);
        }

        await using var reopened = await PdfDocument.OpenAsync(path);
        var read = await reopened.ReadAnnotationsAsync(0);

        Assert.Equal(3, read.Count);
        var readPen = Assert.Single(read.OfType<InkStroke>(), s => s.Name == pen.Name);
        Assert.False(readPen.IsHighlighter);
        Assert.Equal(pen.Color, readPen.Color);
        Assert.Equal(pen.Width, readPen.Width, 3);
        Assert.Equal(pen.Points, readPen.Points); // 필압까지 그대로

        var readMarker = Assert.Single(read.OfType<InkStroke>(), s => s.Name == marker.Name);
        Assert.True(readMarker.IsHighlighter);

        var readHighlight = Assert.Single(read.OfType<TextHighlight>());
        Assert.Equal(highlight.Name, readHighlight.Name);
        Assert.Equal("hello", readHighlight.Text);
        Assert.Equal(highlight.Color, readHighlight.Color);
        var r = Assert.Single(readHighlight.Rects);
        Assert.Equal(72, r.X, 1);
        Assert.Equal(500, r.Y, 1);
        Assert.Equal(120, r.Width, 1);
        Assert.Equal(14, r.Height, 1);
    }

    [Fact]
    public async Task ForeignInk_ReadsPathsColorWidthAndOpacity()
    {
        // 다른 프로그램이 만든 Ink 주석: path 두 개, 빨강, 굵기 4, 이름 없음 / 반투명 Ink(형광펜)
        var path = Track(TestPdf.Create(new TestPdf.Page(595, 842, Annots:
        [
            "<< /Type /Annot /Subtype /Ink /Rect [90 590 310 710] /C [1 0 0] /Border [0 0 4] " +
            "/InkList [[100 700 200 700] [100 600 300 600]] >>",
            "<< /Type /Annot /Subtype /Ink /Rect [90 390 310 410] /C [1 1 0] /CA 0.4 /NM (edge-1) " +
            "/InkList [[100 400 300 400]] >>",
        ])));

        await using var doc = await PdfDocument.OpenAsync(path);
        var read = (await doc.ReadAnnotationsAsync(0)).OfType<InkStroke>().ToList();

        Assert.Equal(3, read.Count);
        var pen = read.Where(s => !s.IsHighlighter).ToList();
        Assert.Equal(2, pen.Count);
        Assert.Single(pen.Select(s => s.Name).Distinct()); // 한 주석에서 나온 획은 이름이 같다
        Assert.StartsWith(Annotation.NamePrefix, pen[0].Name); // 이름이 없어서 새로 붙임
        Assert.Equal(0xFFFF0000u, pen[0].Color);
        Assert.Equal(4, pen[0].Width, 3);
        // PDF 좌표 (100, 700) → 페이지 좌표 (100, 142)
        Assert.Equal(100, pen[0].Points[0].X, 1);
        Assert.Equal(142, pen[0].Points[0].Y, 1);

        var marker = Assert.Single(read, s => s.IsHighlighter);
        Assert.Equal("edge-1", marker.Name); // 원래 이름 유지
    }

    [Fact]
    public async Task ForeignInkWithAppearance_ReadsColorFromAppearance()
    {
        // 외관(AP)이 있는 Ink 주석 (Edge 등이 저장한 주석처럼). /C가 있어도 FPDFAnnot_GetColor는 실패한다.
        var path = Track(TestPdf.Create(new TestPdf.Page(595, 842,
            Annots: ["<< /Type /Annot /Subtype /Ink /Rect [90 690 310 710] /C [0 0 1] /InkList [[100 700 300 700]] >>"],
            Appearances: ["90 690 310 710|0 0.5 0 RG 6 w 100 700 m 300 700 l S"])));

        await using var doc = await PdfDocument.OpenAsync(path);
        var stroke = Assert.IsType<InkStroke>(Assert.Single(await doc.ReadAnnotationsAsync(0)));

        // 외관의 선 색 (0, 0.5, 0) = 초록. 0.5는 127이나 128로 반올림될 수 있다.
        Assert.Equal(0u, (stroke.Color >> 16) & 0xFF);
        Assert.InRange((stroke.Color >> 8) & 0xFF, 127u, 128u);
        Assert.Equal(0u, stroke.Color & 0xFF);
        // 외관이 그대로 렌더링된다 (초록 선).
        using var bitmap = await doc.RenderAsync(RenderRequest.FullPage(0, 595, 842));
        var i = 142 * bitmap.Stride + 200 * 4;
        Assert.True(bitmap.Buffer[i + 1] > 100 && bitmap.Buffer[i + 2] < 50, "외관의 초록 선이 보여야 한다");
    }

    [Fact]
    public async Task ForeignHighlight_ReadsQuadPoints()
    {
        var path = Track(TestPdf.Create(new TestPdf.Page(595, 842, Annots:
        [
            "<< /Type /Annot /Subtype /Highlight /Rect [72 690 200 710] /C [0 1 0] /Contents (abc) " +
            "/QuadPoints [72 710 200 710 72 690 200 690] >>",
        ])));

        await using var doc = await PdfDocument.OpenAsync(path);
        var highlight = Assert.IsType<TextHighlight>(Assert.Single(await doc.ReadAnnotationsAsync(0)));

        Assert.Equal("abc", highlight.Text);
        var r = Assert.Single(highlight.Rects);
        Assert.Equal(new LayoutRect(72, 132, 128, 20), r with
        {
            X = Math.Round(r.X), Y = Math.Round(r.Y), Width = Math.Round(r.Width), Height = Math.Round(r.Height),
        });
    }

    [Fact]
    public async Task ForeignInk_CanBeRemovedByName()
    {
        var path = Track(TestPdf.Create(new TestPdf.Page(595, 842, Annots:
        [
            "<< /Type /Annot /Subtype /Ink /Rect [90 590 310 710] /C [1 0 0] /Border [0 0 10] " +
            "/InkList [[100 700 300 700]] >>",
        ])));

        await using var doc = await PdfDocument.OpenAsync(path);
        var stroke = Assert.Single(await doc.ReadAnnotationsAsync(0));

        Assert.True(await doc.RemoveAnnotationAsync(0, stroke.Name));
        Assert.Empty(await doc.ReadAnnotationsAsync(0));
    }

    [Fact]
    public async Task DuplicateNames_GetUniqueNames()
    {
        var path = Track(TestPdf.Create(new TestPdf.Page(595, 842, Annots:
        [
            "<< /Type /Annot /Subtype /Ink /Rect [0 0 10 10] /NM (same) /InkList [[1 1 5 5]] >>",
            "<< /Type /Annot /Subtype /Ink /Rect [0 0 10 10] /NM (same) /InkList [[2 2 6 6]] >>",
        ])));

        await using var doc = await PdfDocument.OpenAsync(path);
        var read = await doc.ReadAnnotationsAsync(0);

        Assert.Equal(2, read.Select(a => a.Name).Distinct().Count());
    }

    [Fact]
    public void AnnotationSet_Load_IsNotHistoryAndRemoveTakesWholeGroup()
    {
        var set = new AnnotationSet();
        var a = new InkStroke(0, [new InkPoint(0, 0, 0.5f), new InkPoint(10, 0, 0.5f)], 0xFF000000, 1) { Name = "x" };
        var b = new InkStroke(0, [new InkPoint(0, 20, 0.5f), new InkPoint(10, 20, 0.5f)], 0xFF000000, 1) { Name = "x" };

        set.Load([a, b]);
        set.Load([a]); // 이미 있으면 건너뜀

        Assert.Equal(2, set.GetAnnotations(0).Count);
        Assert.False(set.CanUndo);
        Assert.False(set.IsModified);

        set.Remove([a]); // 같은 이름의 b도 함께
        Assert.Empty(set.GetAnnotations(0));

        set.Undo();
        Assert.Equal(2, set.GetAnnotations(0).Count);
    }

    private static IReadOnlyList<IReadOnlyList<PagePoint>> Band(LayoutRect r) =>
    [
        [new PagePoint(r.X, r.Y), new PagePoint(r.Right, r.Y), new PagePoint(r.Right, r.Bottom), new PagePoint(r.X, r.Bottom)],
    ];
}
