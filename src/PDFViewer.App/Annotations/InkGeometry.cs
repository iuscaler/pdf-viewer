using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using PDFViewer.Core.Annotations;

namespace PDFViewer.App.Annotations;

/// <summary>
/// 획의 모양. 화면에 그릴 때(그리는 중, 임시 표시)와 PDF 주석 외관(윤곽선)을 만들 때 같은 모양을 쓰도록 한곳에 모았다.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>펜: 점들을 centripetal Catmull-Rom 곡선으로 보간한 WPF 잉크 획. 둥근 펜촉에 필압이 반영된다.</item>
/// <item>
/// 형광펜: 얇은 사각형 펜촉(끌 모양)이 항상 진행 방향에 수직으로 서서 지나간 모양. 어느 방향으로 그어도 굵기가 같고,
/// 양 끝은 진행 방향에 수직으로 평평하게 잘린다. 점들을 부드러운 곡선으로 이은 뒤 끝이 평평한 펜으로 넓혀서 만든다.
/// </item>
/// </list>
/// </remarks>
internal static class InkGeometry
{
    // 윤곽선을 다각형으로 펼 때 허용 오차 (포인트)
    private const double FlattenTolerance = 0.02;

    // 형광펜 점 사이 최소 거리 (포인트). 너무 촘촘한 점은 곡선을 울퉁불퉁하게 만든다.
    private const double HighlighterMinPointDistance = 1.0;

    // 점 하나만 찍었을 때 형광펜 펜촉의 폭 (굵기에 대한 비율)
    private const double HighlighterTipRatio = 0.3;

    // 펜: 이보다 가까운 입력 점은 버린다 (포인트). 손떨림 수준의 점이 곡선을 울퉁불퉁하게 만든다.
    private const double PenMinPointDistance = 0.3;

    // 펜: 곡선을 보간할 때 점 사이 간격 (포인트)과 구간당 최대 보간 수
    private const double PenSampleSpacing = 0.75;
    private const int PenMaxSubdivisions = 24;

    /// <summary>
    /// InkCanvas용 그리기 속성. 펜은 둥근 끝에 필압을 반영한다.
    /// 형광펜은 셰이더를 쓸 수 없는 환경에서만 InkCanvas로 그리며, 이때는 WPF의 사각형 펜촉을 쓴다.
    /// </summary>
    public static DrawingAttributes CreateAttributes(Color color, double width, bool isHighlighter) => new()
    {
        Color = color,
        Width = width,
        Height = width,
        FitToCurve = true,
        StylusTip = isHighlighter ? StylusTip.Rectangle : StylusTip.Ellipse,
        IgnorePressure = isHighlighter,
        IsHighlighter = isHighlighter,
    };

    /// <summary>
    /// 획이 칠하는 영역 (포인트, 페이지 좌표). 이 영역을 획 색으로 채우면 된다.
    /// 그리는 중과 완성 후가 같은 점 목록이면 같은 모양이 나온다.
    /// </summary>
    public static Geometry GetGeometry(InkStroke stroke) =>
        stroke.IsHighlighter
            ? CreateHighlighterGeometry(stroke.Points, stroke.Width)
            : CreatePenGeometry(stroke.Points, stroke.Width);

    /// <summary>
    /// 펜 모양: 점들을 부드러운 곡선으로 촘촘하게 보간한 뒤(필압도 보간), 둥근 펜촉의 WPF 잉크 윤곽선으로 만든다.
    /// WPF 잉크의 곡선 보정(FitToCurve)은 쓰지 않는다. 그리는 중과 완성 후의 모양을 같게 하려고 보간을 직접 한다.
    /// </summary>
    private static Geometry CreatePenGeometry(IReadOnlyList<InkPoint> inkPoints, double width)
    {
        var smooth = SmoothPen(inkPoints);
        var points = new StylusPointCollection(smooth.Count);
        foreach (var p in smooth)
        {
            points.Add(new StylusPoint(p.X, p.Y, Math.Clamp(p.Pressure, 0f, 1f)));
        }

        var attributes = CreateAttributes(Colors.Black, width, isHighlighter: false);
        attributes.FitToCurve = false;
        var geometry = new Stroke(points, attributes).GetGeometry();
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// 펜 점들을 centripetal Catmull-Rom 곡선으로 보간한다. 점 간격이 고르지 않은 마우스 입력에서도
    /// 곡선이 바깥으로 튀거나 고리를 만들지 않는다. 곡선은 원래 점들을 모두 지난다.
    /// </summary>
    private static List<InkPoint> SmoothPen(IReadOnlyList<InkPoint> inkPoints)
    {
        var points = new List<InkPoint> { inkPoints[0] };
        for (var i = 1; i < inkPoints.Count; i++)
        {
            if (Distance(inkPoints[i], points[^1]) >= PenMinPointDistance)
            {
                points.Add(inkPoints[i]);
            }
        }

        if (points.Count < 3)
        {
            return points;
        }

        var result = new List<InkPoint>(points.Count * 4) { points[0] };
        for (var i = 0; i < points.Count - 1; i++)
        {
            var p1 = points[i];
            var p2 = points[i + 1];
            // 양 끝에서는 바깥쪽 점을 반대편으로 연장해서 만든다.
            var p0 = i > 0 ? points[i - 1] : Mirror(p2, p1);
            var p3 = i + 2 < points.Count ? points[i + 2] : Mirror(p1, p2);

            var steps = Math.Clamp((int)Math.Ceiling(Distance(p1, p2) / PenSampleSpacing), 1, PenMaxSubdivisions);
            for (var s = 1; s <= steps; s++)
            {
                result.Add(CentripetalCatmullRom(p0, p1, p2, p3, (double)s / steps));
            }
        }

        return result;
    }

    /// <summary>p1과 p2 사이의 곡선 위 점 (u: 0~1). 필압은 p1과 p2 사이에서 직선 보간한다.</summary>
    private static InkPoint CentripetalCatmullRom(InkPoint p0, InkPoint p1, InkPoint p2, InkPoint p3, double u)
    {
        // 매개변수 간격 = 거리의 제곱근 (centripetal). 겹친 점에서 0으로 나누지 않도록 최솟값을 둔다.
        const double epsilon = 1e-4;
        var t0 = 0.0;
        var t1 = t0 + Math.Max(epsilon, Math.Sqrt(Distance(p0, p1)));
        var t2 = t1 + Math.Max(epsilon, Math.Sqrt(Distance(p1, p2)));
        var t3 = t2 + Math.Max(epsilon, Math.Sqrt(Distance(p2, p3)));
        var t = t1 + (t2 - t1) * u;

        // Barry-Goldman 피라미드 공식
        var a1 = Lerp(p0, p1, (t - t0) / (t1 - t0));
        var a2 = Lerp(p1, p2, (t - t1) / (t2 - t1));
        var a3 = Lerp(p2, p3, (t - t2) / (t3 - t2));
        var b1 = Lerp(a1, a2, (t - t0) / (t2 - t0));
        var b2 = Lerp(a2, a3, (t - t1) / (t3 - t1));
        var c = Lerp(b1, b2, (t - t1) / (t2 - t1));

        var pressure = p1.Pressure + (p2.Pressure - p1.Pressure) * (float)u;
        return new InkPoint((float)c.X, (float)c.Y, pressure);
    }

    private static Point Lerp(InkPoint a, InkPoint b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static Point Lerp(Point a, Point b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    // b를 a 기준으로 반대편에 옮긴 점 (a + (a - b))
    private static InkPoint Mirror(InkPoint b, InkPoint a) => new(2 * a.X - b.X, 2 * a.Y - b.Y, a.Pressure);

    private static double Distance(InkPoint a, InkPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// 획의 윤곽선을 다각형들로 돌려준다 (포인트, 페이지 좌표). 화면에 그려지는 모양 그대로 PDF 주석의 외관이 된다.
    /// 겹치는 부분은 nonzero 규칙으로 채운다.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<PagePoint>> GetOutline(InkStroke stroke)
    {
        var geometry = GetGeometry(stroke).GetFlattenedPathGeometry(FlattenTolerance, ToleranceType.Absolute);
        var polygons = new List<IReadOnlyList<PagePoint>>();
        foreach (var figure in geometry.Figures)
        {
            var points = new List<PagePoint> { new(figure.StartPoint.X, figure.StartPoint.Y) };
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case PolyLineSegment polyLine:
                        points.AddRange(polyLine.Points.Select(p => new PagePoint(p.X, p.Y)));
                        break;
                    case LineSegment line:
                        points.Add(new PagePoint(line.Point.X, line.Point.Y));
                        break;
                }
            }

            polygons.Add(points);
        }

        return polygons;
    }

    /// <summary>
    /// 형광펜 모양: 점들을 부드러운 곡선으로 잇고, 굵기 width에 끝이 평평한 펜으로 넓힌다.
    /// 펜촉이 진행 방향에 수직으로 돌아가며 지나간 모양이 된다.
    /// </summary>
    private static Geometry CreateHighlighterGeometry(IReadOnlyList<InkPoint> inkPoints, double width)
    {
        var points = Simplify(inkPoints);
        if (points.Count < 2)
        {
            // 점 하나만 찍었으면 세워 둔 펜촉 모양 하나를 남긴다.
            var p = points[0];
            var tip = width * HighlighterTipRatio;
            var dot = new RectangleGeometry(new Rect(p.X - tip / 2, p.Y - width / 2, tip, width));
            dot.Freeze();
            return dot;
        }

        var path = new StreamGeometry();
        using (var context = path.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            if (points.Count == 2)
            {
                context.LineTo(points[1], isStroked: true, isSmoothJoin: true);
            }
            else
            {
                context.PolyBezierTo(CatmullRomToBezier(points), isStroked: true, isSmoothJoin: true);
            }
        }

        path.Freeze();
        var pen = new Pen(Brushes.Black, width)
        {
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat,
            // 방향이 바뀌는 곳에서 펜촉이 돌아가며 지나간 자리를 둥글게 채운다.
            LineJoin = PenLineJoin.Round,
        };
        var outline = path.GetWidenedPathGeometry(pen, FlattenTolerance, ToleranceType.Absolute);
        outline.FillRule = FillRule.Nonzero;
        outline.Freeze();
        return outline;
    }

    /// <summary>너무 가까운 점을 버린다 (마지막 점은 항상 남긴다).</summary>
    private static List<Point> Simplify(IReadOnlyList<InkPoint> inkPoints)
    {
        var points = new List<Point> { new(inkPoints[0].X, inkPoints[0].Y) };
        for (var i = 1; i < inkPoints.Count; i++)
        {
            var p = new Point(inkPoints[i].X, inkPoints[i].Y);
            var isLast = i == inkPoints.Count - 1;
            if ((p - points[^1]).Length >= HighlighterMinPointDistance)
            {
                points.Add(p);
            }
            else if (isLast && points.Count > 1)
            {
                points[^1] = p;
            }
        }

        return points;
    }

    /// <summary>점들을 지나는 Catmull-Rom 곡선을 3차 베지어 제어점 목록으로 바꾼다 (PolyBezierTo용).</summary>
    private static List<Point> CatmullRomToBezier(List<Point> p)
    {
        var result = new List<Point>((p.Count - 1) * 3);
        for (var i = 0; i < p.Count - 1; i++)
        {
            var p0 = p[Math.Max(0, i - 1)];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = p[Math.Min(p.Count - 1, i + 2)];
            result.Add(p1 + (p2 - p0) / 6);
            result.Add(p2 - (p3 - p1) / 6);
            result.Add(p2);
        }

        return result;
    }

    public static uint ToArgb(Color c) => (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);

    public static Color FromArgb(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
