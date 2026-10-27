using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PDFViewer.App.Annotations;
using PDFViewer.Core.Annotations;

namespace PDFViewer.App.Controls;

/// <summary>
/// 형광펜을 그리는 동안에도 Edge처럼 곱하기 합성으로 보여주기.
/// </summary>
/// <remarks>
/// 다음 형광펜들을 화면 좌표의 "형광펜 레이어"(불투명한 색)에 모아 그리고, 뷰 전체에 <see cref="MultiplyEffect"/>를 걸어
/// 페이지 색에 레이어 색을 곱한다.
/// <list type="bullet">
/// <item>그리고 있는 자유 형광펜 (입력은 PdfDocumentView.Freehand.cs)</item>
/// <item>형광펜 도구로 텍스트를 끄는 중인 미리보기</item>
/// <item>PDF에 넣었지만 아직 다시 렌더링되지 않은 페이지의 형광펜</item>
/// </list>
/// 레이어가 비어 있으면 효과를 꺼서 평소 렌더링에는 비용이 들지 않는다.
/// 셰이더를 쓸 수 없는 환경이면 예전처럼 InkCanvas와 반투명 그리기를 쓴다.
/// </remarks>
public sealed partial class PdfDocumentView
{
    private readonly MultiplyEffect? _multiplyEffect = MultiplyEffect.IsSupported ? new MultiplyEffect() : null;

    // 셰이더 입력으로는 VisualBrush 등만 쓸 수 있어서(DrawingBrush 불가), 레이어를 DrawingVisual로 만든다.
    private readonly DrawingVisual _highlightOverlay = new();
    private readonly VisualBrush _highlightOverlayBrush = new()
    {
        ViewboxUnits = BrushMappingMode.Absolute,
        Stretch = Stretch.Fill,
        TileMode = TileMode.None,
    };

    // 페이지별 임시 형광펜 Drawing (포인트 단위, 불투명). 화면에 보이는 비트맵의 리비전이 바뀌면 다시 만든다.
    private readonly Dictionary<int, PendingDrawing> _highlightDrawings = new();

    // 이번 OnRender에서 그린 페이지와 화면 위치
    private readonly List<(int Page, Rect Rect)> _visiblePageRects = new();

    /// <summary>형광펜을 곱하기 합성으로 그릴 수 있는지 여부 (셰이더 사용 가능).</summary>
    private bool UsesMultiply => _multiplyEffect is not null;

    private void InitializeHighlight()
    {
        if (_multiplyEffect is not null)
        {
            _highlightOverlayBrush.Visual = _highlightOverlay;
            _multiplyEffect.Overlay = _highlightOverlayBrush;
        }
    }

    #region 형광펜 레이어

    /// <summary>OnRender 끝에서 호출한다. 이번에 그린 페이지 위치에 맞춰 형광펜 레이어를 다시 만든다.</summary>
    private void UpdateHighlightOverlay()
    {
        if (_multiplyEffect is null || Document is not { } document)
        {
            return;
        }

        var hasContent = false;
        using (var context = _highlightOverlay.RenderOpen())
        {
            foreach (var (page, rect) in _visiblePageRects)
            {
                var highlights = IsShowingStaleContent(page) ? GetPendingHighlightDrawing(page) : null;
                var selection = IsHighlighterTool && _selection is { } s && s.Page == page
                                && _pageTexts.GetValueOrDefault(page) is { } text
                    ? text.GetSelectionRects(s.Anchor, s.Focus)
                    : null;
                var freehand = _liveStroke is { IsHighlighter: true } f && f.Page == page ? f : null;
                if (highlights is null && selection is null && freehand is null)
                {
                    continue;
                }

                var size = document.PageSizes[page];
                context.PushClip(new RectangleGeometry(rect));
                context.PushTransform(new MatrixTransform(rect.Width / size.Width, 0, 0, rect.Height / size.Height,
                    rect.X, rect.Y));

                if (highlights is not null)
                {
                    context.DrawDrawing(highlights);
                }

                var brush = new SolidColorBrush(HighlighterColor);
                if (selection is not null)
                {
                    foreach (var r in selection)
                    {
                        context.DrawRectangle(brush, null, new Rect(r.X, r.Y, r.Width, r.Height));
                    }
                }

                if (freehand is not null)
                {
                    context.DrawGeometry(brush, null, InkGeometry.GetGeometry(freehand.ToStroke()));
                }

                context.Pop();
                context.Pop();
                hasContent = true;
            }
        }

        _highlightOverlayBrush.Viewbox = new Rect(RenderSize);
        // 곱할 것이 없으면 효과를 꺼서 평소에는 셰이더 비용이 들지 않게 한다.
        // 형광펜 도구를 쓰는 동안에는 켜 둔다. 칠할 때마다 켜고 끄면 화면 전체가 다시 합성되면서 깜빡일 수 있다.
        Effect = hasContent || IsHighlighterTool ? _multiplyEffect : null;
    }

    /// <summary>
    /// 화면의 비트맵에 아직 그려지지 않은 형광펜(자유·텍스트)을 불투명한 색으로 그린 Drawing. 없으면 null.
    /// 이미 비트맵에 있는 형광펜까지 넣으면 두 번 곱해져서 잠깐 진해졌다가 돌아온다(깜빡임).
    /// </summary>
    private Drawing? GetPendingHighlightDrawing(int pageIndex)
    {
        var shown = ShownRevision(pageIndex);
        if (_highlightDrawings.TryGetValue(pageIndex, out var cached) && cached.ShownRevision == shown)
        {
            return cached.Drawing;
        }

        var highlights = PendingAnnotations(pageIndex, shown).Where(IsHighlight).ToList();
        if (highlights.Count == 0)
        {
            _highlightDrawings[pageIndex] = new PendingDrawing(shown, null);
            return null;
        }

        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            foreach (var annotation in highlights)
            {
                var brush = new SolidColorBrush(InkGeometry.FromArgb(annotation.Color));
                brush.Freeze();
                switch (annotation)
                {
                    case TextHighlight highlight:
                        foreach (var r in highlight.Rects)
                        {
                            context.DrawRectangle(brush, null, new Rect(r.X, r.Y, r.Width, r.Height));
                        }

                        break;
                    case InkStroke stroke:
                        context.DrawGeometry(brush, null, InkGeometry.GetGeometry(stroke));
                        break;
                }
            }
        }

        group.Freeze();
        _highlightDrawings[pageIndex] = new PendingDrawing(shown, group);
        return group;
    }

    /// <summary>곱하기 합성으로 그려야 하는 주석인지 여부.</summary>
    private static bool IsHighlight(Annotation annotation) =>
        annotation is TextHighlight or InkStroke { IsHighlighter: true };

    #endregion

    /// <summary>화면의 비트맵(리비전 ShownRevision)에 아직 없는 주석을 임시로 그린 Drawing. 그릴 것이 없으면 null.</summary>
    private sealed record PendingDrawing(int ShownRevision, Drawing? Drawing);
}
