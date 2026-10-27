using System.Windows;
using System.Windows.Media;
using PDFViewer.App.Annotations;
using PDFViewer.App.Diagnostics;
using PDFViewer.Core.Annotations;

namespace PDFViewer.App.Controls;

/// <summary>
/// 펜과 자유 형광펜을 이 뷰가 직접 입력받아 그리기.
/// </summary>
/// <remarks>
/// 그리는 동안에도 완성된 획과 똑같은 모양 계산(<see cref="InkGeometry.GetGeometry"/>)으로 그려서, 손을 떼는 순간 모양이
/// 바뀌지 않는다. 펜은 일반 그리기로, 형광펜은 곱하기 레이어(PdfDocumentView.Highlight.cs)로 그린다.
/// 마우스는 PdfDocumentView.Text.cs의 마우스 처리에서, 펜은 PdfDocumentView.Ink.cs의 Stylus 처리에서 이곳을 부른다.
/// </remarks>
public sealed partial class PdfDocumentView
{
    // 떨림 필터 (StrokeSmoother) 설정. 마우스 좌표의 픽셀 계단은 화면 픽셀 단위로 생기므로 화면 물리 픽셀 기준으로 정하고,
    // 획을 시작할 때의 배율로 페이지 포인트 단위로 바꾼다.
    // 평활 길이: 그리는 점이 입력을 약 이만큼 뒤따라온다. 결과 점 사이 최소 거리: 이보다 촘촘한 점은 남기지 않는다.
    private const double SmoothingPixels = 3.0;
    private const double MinPointPixels = 1.5;

    // 마우스 입력의 필압. WPF 잉크와 같이 0.5를 기준 굵기로 본다.
    private const float MousePressure = 0.5f;

    private LiveStroke? _liveStroke;
    private bool _stylusDrawing;

    /// <summary>
    /// 펜 도구, 또는 형광펜 도구에서 텍스트가 아닌 곳을 누르면 그리기를 시작한다.
    /// 형광펜은 곱하기 합성을 쓸 수 있을 때만 여기서 그린다 (아니면 InkCanvas가 받는다).
    /// </summary>
    private bool TryBeginFreehand(Point viewPosition, float pressure, string device = "mouse")
    {
        var isPen = ActiveTool == AnnotationTool.Pen;
        if (!(isPen || (IsHighlighterTool && UsesMultiply)) || Annotations is null || Document is null)
        {
            return false;
        }

        var (x, y) = ViewToDocument(viewPosition);
        if (FindPageAt(x, y, 0) is not { } page)
        {
            return false; // 페이지 밖에서 시작한 획은 그리지 않는다.
        }

        var (px, py) = DocumentToPage(page, x, y);
        var pixelsPerPoint = _layout.PageRects[page].Width / Document.PageSizes[page].Width * _dpiScale;
        var smoother = new StrokeSmoother(SmoothingPixels / pixelsPerPoint, MinPointPixels / pixelsPerPoint);
        _liveStroke = isPen
            ? new LiveStroke(page, isHighlighter: false, PenColor, PenWidth, smoother)
            : new LiveStroke(page, isHighlighter: true, HighlighterColor, HighlighterWidth, smoother);
        smoother.Add(px, py, pressure);
        if (InputLog.Enabled)
        {
            InputLog.BeginStroke(isPen ? "pen" : "highlighter", device, new Point(px, py), _layout.Zoom, _dpiScale);
        }

        InvalidateVisual();
        return true;
    }

    /// <summary>뷰 좌표를 그리고 있는 획의 페이지 좌표(포인트)로 바꾼다 (입력 측정용).</summary>
    private Point ViewToLiveStrokePage(Point viewPosition)
    {
        var (x, y) = ViewToDocument(viewPosition);
        var (px, py) = DocumentToPage(_liveStroke!.Page, x, y);
        return new Point(px, py);
    }

    private void AddFreehandPoint(Point viewPosition, float pressure)
    {
        if (_liveStroke is not { } live)
        {
            return;
        }

        // 페이지 밖으로 나가도 점은 계속 모은다 (그릴 때 페이지 영역으로 잘린다).
        var (x, y) = ViewToDocument(viewPosition);
        var (px, py) = DocumentToPage(live.Page, x, y);
        var count = live.Smoother.Points.Count;
        live.Smoother.Add(px, py, pressure);
        if (live.Smoother.Points.Count != count)
        {
            InvalidateVisual();
        }
    }

    private void EndFreehand()
    {
        if (_liveStroke is not { } live)
        {
            return;
        }

        _liveStroke = null;
        live.Smoother.Finish();
        if (InputLog.Enabled)
        {
            InputLog.EndStroke(live.Smoother.Points.Count);
        }

        Annotations?.Add(live.ToStroke());
        InvalidateVisual();
    }

    /// <summary>그리고 있는 펜 획을 그린다 (OnRender에서 페이지들을 그린 뒤). 형광펜은 곱하기 레이어가 그린다.</summary>
    private void DrawLivePen(DrawingContext dc)
    {
        if (_liveStroke is not { IsHighlighter: false } live || Document is not { } document)
        {
            return;
        }

        foreach (var (page, rect) in _visiblePageRects)
        {
            if (page != live.Page)
            {
                continue;
            }

            var size = document.PageSizes[page];
            dc.PushClip(new RectangleGeometry(rect));
            dc.PushTransform(new MatrixTransform(rect.Width / size.Width, 0, 0, rect.Height / size.Height,
                rect.X, rect.Y));
            dc.DrawGeometry(new SolidColorBrush(live.Color), null, InkGeometry.GetGeometry(live.ToStroke()));
            dc.Pop();
            dc.Pop();
        }
    }

    /// <summary>그리고 있는 획 (페이지 좌표, 포인트). 점은 떨림 필터를 거친 것이며, 그대로 저장된다.</summary>
    private sealed class LiveStroke(int page, bool isHighlighter, Color color, double width, StrokeSmoother smoother)
    {
        public int Page { get; } = page;

        public bool IsHighlighter { get; } = isHighlighter;

        public Color Color { get; } = color;

        public StrokeSmoother Smoother { get; } = smoother;

        public InkStroke ToStroke() =>
            new(Page, Smoother.Points.ToArray(), InkGeometry.ToArgb(Color), width, IsHighlighter);
    }
}
