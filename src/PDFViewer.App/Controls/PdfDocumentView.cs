using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PDFViewer.Core.Caching;
using PDFViewer.Core.Layout;
using PDFViewer.Core.Pdf;
using PDFViewer.Core.Rendering;

namespace PDFViewer.App.Controls;

/// <summary>
/// PDF 페이지를 보여주는 가상화 컨트롤.
/// <see cref="ScrollViewer"/> 안에 CanContentScroll=True로 넣어서 사용하며, 스크롤은 직접 관리한다(IScrollInfo).
/// 화면에 보이는 페이지(와 그 주변)만 렌더링을 요청하고 OnRender에서 그린다.
/// </summary>
/// <remarks>
/// 스크롤 범위는 "창(Window)"이라는 문서 좌표 사각형으로 정한다.
/// 연속 스크롤에서는 문서 전체가, 페이지 단위 모드에서는 현재 페이지가 있는 줄이 창이 된다.
/// 스크롤 위치(_offsetX/Y)는 창의 왼쪽 위를 기준으로 한다.
/// </remarks>
public sealed class PdfDocumentView : FrameworkElement, IScrollInfo
{
    public const double MinZoom = 0.1;
    public const double MaxZoom = 10;

    // 페이지 전체 비트맵의 최대 픽셀 수. 이보다 크게 확대하면 전체 비트맵은 이 크기로 제한하고,
    // 화면에 보이는 부분만 원래 해상도로 따로 렌더링한다(상세 영역).
    private const long MaxFullPagePixels = 6_000_000;

    private const long ImageCacheBytes = 512L * 1024 * 1024;
    private const double LineScrollAmount = 48;
    private const double WheelScrollPerNotch = 96;
    private static readonly TimeSpan ZoomSettleDelay = TimeSpan.FromMilliseconds(180);

    // 페이지 단위 모드에서 휠로 페이지를 넘긴 뒤, 휠 입력이 이 시간 동안 멈춰야 다시 넘길 수 있다.
    // 터치패드의 관성 스크롤 때문에 여러 페이지가 한꺼번에 넘어가는 것을 막는다.
    private static readonly TimeSpan PageTurnQuietTime = TimeSpan.FromMilliseconds(250);

    private static readonly Brush PageBrush = Brushes.White;
    private static readonly Pen PageBorderPen = CreateFrozenPen(Color.FromArgb(0x40, 0, 0, 0), 1);
    private static readonly Brush ShadowBrush = CreateFrozenBrush(Color.FromArgb(0x18, 0, 0, 0));
    private static readonly Typeface MessageTypeface = new("Segoe UI");

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(PdfDocument), typeof(PdfDocumentView),
        new PropertyMetadata(null, (d, _) => ((PdfDocumentView)d).OnDocumentChanged()));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(PdfDocumentView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((PdfDocumentView)d).OnZoomPropertyChanged(),
            (_, value) => Math.Clamp((double)value, MinZoom, MaxZoom)));

    public static readonly DependencyProperty ZoomModeProperty = DependencyProperty.Register(
        nameof(ZoomMode), typeof(ZoomMode), typeof(PdfDocumentView),
        new FrameworkPropertyMetadata(ZoomMode.FitWidth, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((PdfDocumentView)d).OnZoomModeChanged()));

    public static readonly DependencyProperty ScrollModeProperty = DependencyProperty.Register(
        nameof(ScrollMode), typeof(ScrollMode), typeof(PdfDocumentView),
        new FrameworkPropertyMetadata(ScrollMode.Continuous, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((PdfDocumentView)d).OnScrollModeChanged((ScrollMode)e.OldValue)));

    public static readonly DependencyProperty PageLayoutProperty = DependencyProperty.Register(
        nameof(PageLayout), typeof(PageLayout), typeof(PdfDocumentView),
        new PropertyMetadata(PageLayout.SinglePage, (d, _) => ((PdfDocumentView)d).OnLayoutOptionsChanged()));

    public static readonly DependencyProperty ShowCoverSeparatelyProperty = DependencyProperty.Register(
        nameof(ShowCoverSeparately), typeof(bool), typeof(PdfDocumentView),
        new PropertyMetadata(false, (d, _) => ((PdfDocumentView)d).OnLayoutOptionsChanged()));

    public static readonly DependencyProperty RightToLeftProperty = DependencyProperty.Register(
        nameof(RightToLeft), typeof(bool), typeof(PdfDocumentView),
        new PropertyMetadata(false, (d, _) => ((PdfDocumentView)d).OnLayoutOptionsChanged()));

    public static readonly DependencyProperty CurrentPageIndexProperty = DependencyProperty.Register(
        nameof(CurrentPageIndex), typeof(int), typeof(PdfDocumentView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((PdfDocumentView)d).OnCurrentPageIndexChanged()));

    private readonly LruCache<int, PageImage> _pageImages =
        new(ImageCacheBytes, image => (long)image.Bitmap.PixelWidth * image.Bitmap.PixelHeight * 4);

    private readonly Dictionary<int, DetailImage> _details = new();
    private readonly HashSet<int> _failedPages = new();
    private readonly DispatcherTimer _zoomSettleTimer;

    private LayoutOptions _options = new();
    private DocumentLayout _layout = DocumentLayout.Empty;
    private RenderScheduler? _scheduler;
    private int _documentVersion;
    private Size _viewport;
    private double _offsetX;
    private double _offsetY;
    private double _dpiScale = 1;
    private bool _zoomSettling;
    private bool _updatingProperty;

    // 페이지 단위 모드에서 페이지 끝을 넘어 굴린 휠 양과, 다음 페이지 넘김이 허용되는 시각
    private double _wheelOverscroll;
    private DateTime _pageTurnAllowedAt;

    public PdfDocumentView()
    {
        Focusable = false;
        ClipToBounds = true;
        _zoomSettleTimer = new DispatcherTimer(ZoomSettleDelay, DispatcherPriority.Background, OnZoomSettled,
            Dispatcher);
        _zoomSettleTimer.Stop();
        Loaded += (_, _) =>
        {
            _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            UpdateRenderRequests();
        };
    }

    public PdfDocument? Document
    {
        get => (PdfDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>배율 (1 = 100%).</summary>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public ZoomMode ZoomMode
    {
        get => (ZoomMode)GetValue(ZoomModeProperty);
        set => SetValue(ZoomModeProperty, value);
    }

    public ScrollMode ScrollMode
    {
        get => (ScrollMode)GetValue(ScrollModeProperty);
        set => SetValue(ScrollModeProperty, value);
    }

    public PageLayout PageLayout
    {
        get => (PageLayout)GetValue(PageLayoutProperty);
        set => SetValue(PageLayoutProperty, value);
    }

    /// <summary>2페이지 보기에서 첫 페이지(표지)를 혼자 놓는다.</summary>
    public bool ShowCoverSeparately
    {
        get => (bool)GetValue(ShowCoverSeparatelyProperty);
        set => SetValue(ShowCoverSeparatelyProperty, value);
    }

    /// <summary>오른쪽에서 왼쪽으로 읽는 책. 2페이지 보기의 좌우와 페이지 단위 모드의 ←/→ 방향이 바뀐다.</summary>
    public bool RightToLeft
    {
        get => (bool)GetValue(RightToLeftProperty);
        set => SetValue(RightToLeftProperty, value);
    }

    /// <summary>현재 페이지 (0부터). 스크롤하면 갱신되고, 값을 바꾸면 해당 페이지로 이동한다.</summary>
    public int CurrentPageIndex
    {
        get => (int)GetValue(CurrentPageIndexProperty);
        set => SetValue(CurrentPageIndexProperty, value);
    }

    private bool IsPageMode => ScrollMode == ScrollMode.PageByPage;

    #region 문서 / 배율 / 스크롤 방식 변경

    private void OnDocumentChanged()
    {
        _scheduler?.Dispose();
        _scheduler = null;
        _documentVersion++;
        _pageImages.Clear();
        _details.Clear();
        _failedPages.Clear();
        _offsetX = _offsetY = 0;

        if (Document is { } document)
        {
            _scheduler = new RenderScheduler(document);
            var version = _documentVersion;
            _scheduler.Completed += (request, bitmap) => OnRenderCompleted(version, request, bitmap);
            _scheduler.Failed += (request, _) =>
                Dispatcher.BeginInvoke(() => OnRenderFailed(version, request));
        }

        SetPropertyInternally(CurrentPageIndexProperty, 0);
        SetPropertyInternally(ZoomProperty, ComputeZoomForMode() ?? Zoom);
        RebuildLayout(Zoom);
        if (_layout.PageCount > 0)
        {
            ScrollToPage(0);
        }
    }

    private void OnZoomPropertyChanged()
    {
        if (!_updatingProperty)
        {
            ApplyZoom(Zoom, null);
        }
    }

    private void OnZoomModeChanged()
    {
        if (ComputeZoomForMode() is { } zoom)
        {
            ApplyZoom(zoom, null);
            if (ZoomMode == ZoomMode.FitPage)
            {
                // 페이지 맞춤은 현재 페이지 전체가 보이도록 페이지 맨 위로 이동한다.
                ScrollToPage(CurrentPageIndex);
            }
        }
    }

    private void OnScrollModeChanged(ScrollMode oldMode)
    {
        if (_layout.PageCount == 0)
        {
            return;
        }

        // 바꾸기 전 화면 왼쪽 위에 있던 문서 위의 지점이 바꾼 뒤에도 왼쪽 위에 오도록 한다.
        var oldWindow = GetWindow(oldMode);
        var visibleX = _offsetX - CenterOffset(oldWindow.Width, _viewport.Width) + oldWindow.X;
        var visibleY = _offsetY - CenterOffset(oldWindow.Height, _viewport.Height) + oldWindow.Y;

        var window = Window;
        _offsetX = visibleX - window.X + CenterOffsetX;
        _offsetY = visibleY - window.Y + CenterOffsetY;
        ClampOffsets();
        OnViewChanged();
    }

    private void OnLayoutOptionsChanged()
    {
        _options = new LayoutOptions
        {
            PageLayout = PageLayout,
            ShowCoverSeparately = ShowCoverSeparately,
            RightToLeft = RightToLeft,
        };

        if (_layout.PageCount == 0)
        {
            return;
        }

        // 보기 방식을 바꿔도 보고 있던 페이지를 유지한다.
        var page = ClampedCurrentPage;
        SetPropertyInternally(ZoomProperty, ComputeZoomForMode() ?? Zoom);
        RebuildLayout(Zoom);
        ScrollToPage(page);
    }

    private void OnCurrentPageIndexChanged()
    {
        if (!_updatingProperty)
        {
            ScrollToPage(CurrentPageIndex);
        }
    }

    /// <summary>현재 배율 모드에 맞는 배율. 사용자 지정 모드거나 계산할 수 없으면 null.</summary>
    private double? ComputeZoomForMode()
    {
        if (Document is not { PageCount: > 0 } document || _viewport.Width <= 0 || _viewport.Height <= 0)
        {
            return null;
        }

        return DocumentLayout.GetFitZoom(document.PageSizes, CurrentPageIndex, ZoomMode, _viewport.Width,
            _viewport.Height, _options);
    }

    /// <summary>
    /// 배율을 바꾼다. anchor(뷰 좌표)에 있던 문서 위의 지점이 배율을 바꾼 뒤에도 같은 자리에 오도록 스크롤한다.
    /// anchor가 null이면 화면 위쪽 가운데를 기준으로 한다.
    /// </summary>
    private void ApplyZoom(double zoom, Point? anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        SetPropertyInternally(ZoomProperty, zoom);
        if (Document is null || Math.Abs(zoom - _layout.Zoom) < 1e-9)
        {
            return;
        }

        var viewPoint = anchor ?? new Point(_viewport.Width / 2, 0);
        var (docX, docY) = ViewToDocument(viewPoint);
        // 페이지 단위 모드에서는 화면에 다른 페이지가 없으므로 항상 현재 페이지를 기준으로 삼는다.
        var pageAnchor = IsPageMode
            ? _layout.GetAnchor(ClampedCurrentPage, docX, docY)
            : _layout.GetAnchor(docX, docY);

        RebuildLayout(zoom);

        var (newX, newY) = _layout.GetPoint(pageAnchor);
        var window = Window;
        _offsetX = newX - window.X + CenterOffsetX - viewPoint.X;
        _offsetY = newY - window.Y + CenterOffsetY - viewPoint.Y;
        if (anchor is null)
        {
            // 크기가 다른 페이지가 섞여 있으면 문서가 화면보다 넓어질 수 있다. 현재 페이지를 가로 가운데에 둔다.
            CenterHorizontally();
        }

        ClampOffsets();

        // 조작이 멈출 때까지는 기존 비트맵을 늘려서 보여주고, 멈추면 새 배율로 렌더링한다.
        _zoomSettling = true;
        _zoomSettleTimer.Stop();
        _zoomSettleTimer.Start();

        OnViewChanged();
    }

    private void OnZoomSettled(object? sender, EventArgs e)
    {
        _zoomSettleTimer.Stop();
        _zoomSettling = false;
        UpdateRenderRequests();
    }

    private void RebuildLayout(double zoom)
    {
        _layout = Document is { } document
            ? DocumentLayout.Create(document.PageSizes, zoom, _options)
            : DocumentLayout.Empty;
        ClampOffsets();
        OnViewChanged();
    }

    private void SetPropertyInternally(DependencyProperty property, object value)
    {
        _updatingProperty = true;
        try
        {
            // SetCurrentValue는 바인딩을 유지한 채 값을 바꾸고 TwoWay 바인딩이면 원본에도 반영한다.
            SetCurrentValue(property, value);
        }
        finally
        {
            _updatingProperty = false;
        }
    }

    #endregion

    #region 좌표 / 스크롤

    private int ClampedCurrentPage => Math.Clamp(CurrentPageIndex, 0, Math.Max(0, _layout.PageCount - 1));

    /// <summary>스크롤할 수 있는 범위 (문서 좌표).</summary>
    private LayoutRect Window => GetWindow(ScrollMode);

    private LayoutRect GetWindow(ScrollMode mode)
    {
        if (mode == ScrollMode.Continuous || _layout.PageCount == 0)
        {
            return new LayoutRect(0, 0, _layout.ExtentWidth, _layout.ExtentHeight);
        }

        var bounds = _layout.GetRowBounds(_layout.GetRowOfPage(ClampedCurrentPage));
        var margin = _options.Margin;
        return new LayoutRect(bounds.X - margin, bounds.Y - margin, bounds.Width + margin * 2,
            bounds.Height + margin * 2);
    }

    /// <summary>창에 들어 있는 페이지 범위.</summary>
    private (int First, int Last) WindowPages
    {
        get
        {
            if (_layout.PageCount == 0)
            {
                return (0, -1);
            }

            if (!IsPageMode)
            {
                return (0, _layout.PageCount - 1);
            }

            var row = _layout.GetRowOfPage(ClampedCurrentPage);
            return (row.FirstPage, row.LastPage);
        }
    }

    // 창이 화면보다 작으면 가운데에 놓는다.
    private double CenterOffsetX => CenterOffset(Window.Width, _viewport.Width);

    private double CenterOffsetY => CenterOffset(Window.Height, _viewport.Height);

    private static double CenterOffset(double windowSize, double viewportSize) =>
        Math.Max(0, (viewportSize - windowSize) / 2);

    private double MaxOffsetX => Math.Max(0, Window.Width - _viewport.Width);

    private double MaxOffsetY => Math.Max(0, Window.Height - _viewport.Height);

    private (double X, double Y) ViewToDocument(Point p)
    {
        var window = Window;
        return (p.X + _offsetX - CenterOffsetX + window.X, p.Y + _offsetY - CenterOffsetY + window.Y);
    }

    private LayoutRect VisibleDocumentRect
    {
        get
        {
            var (x, y) = ViewToDocument(new Point(0, 0));
            return new LayoutRect(x, y, _viewport.Width, _viewport.Height);
        }
    }

    /// <summary>화면에 보이는 페이지 범위. 페이지 단위 모드에서는 창 밖의 페이지를 빼고 센다.</summary>
    private (int First, int Last) VisiblePages
    {
        get
        {
            var visible = VisibleDocumentRect;
            var (first, last) = _layout.GetPagesInRange(visible.Y, visible.Bottom);
            var (windowFirst, windowLast) = WindowPages;
            return (Math.Max(first, windowFirst), Math.Min(last, windowLast));
        }
    }

    /// <summary>문서 좌표 사각형을 화면 픽셀 경계에 맞춘 뷰 좌표 사각형으로 바꾼다.</summary>
    private Rect DocumentToViewSnapped(LayoutRect r, int pixelWidth, int pixelHeight)
    {
        var window = Window;
        var x = Math.Round((r.X - window.X - _offsetX + CenterOffsetX) * _dpiScale) / _dpiScale;
        var y = Math.Round((r.Y - window.Y - _offsetY + CenterOffsetY) * _dpiScale) / _dpiScale;
        return new Rect(x, y, pixelWidth / _dpiScale, pixelHeight / _dpiScale);
    }

    private void ClampOffsets()
    {
        _offsetX = Math.Clamp(_offsetX, 0, MaxOffsetX);
        _offsetY = Math.Clamp(_offsetY, 0, MaxOffsetY);
    }

    /// <summary>페이지의 맨 위로 이동한다. 페이지 단위 모드에서는 그 페이지가 있는 줄로 창이 바뀐다.</summary>
    private void ScrollToPage(int pageIndex)
    {
        if (_layout.PageCount == 0)
        {
            return;
        }

        pageIndex = Math.Clamp(pageIndex, 0, _layout.PageCount - 1);
        if (CurrentPageIndex != pageIndex)
        {
            SetPropertyInternally(CurrentPageIndexProperty, pageIndex);
        }

        // 페이지 단위 + 맞춤 배율이면 페이지마다 크기가 달라도 항상 그 페이지(줄)에 맞춘다.
        if (IsPageMode && ComputeZoomForMode() is { } fitZoom && Math.Abs(fitZoom - _layout.Zoom) > 1e-6)
        {
            SetPropertyInternally(ZoomProperty, Math.Clamp(fitZoom, MinZoom, MaxZoom));
            _zoomSettling = false;
            RebuildLayout(Zoom);
        }

        var row = _layout.GetRowOfPage(pageIndex);
        _offsetY = row.Top - _options.Margin - Window.Y;
        CenterHorizontally();
        ClampOffsets();
        OnViewChanged();
    }

    /// <summary>
    /// 창의 가로 가운데를 화면 가운데에 둔다. 모든 페이지는 문서 가운데 선을 기준으로 놓이므로,
    /// 크기가 다른 페이지가 섞여 문서가 화면보다 넓어도 현재 페이지가 한쪽으로 치우치지 않는다.
    /// </summary>
    private void CenterHorizontally() => _offsetX = (Window.Width - _viewport.Width) / 2;

    /// <summary>
    /// 페이지 단위 모드에서 앞뒤 줄로 넘어간다. 뒤로 갈 때 toEnd가 true면 앞 페이지의 맨 아래를 보여준다.
    /// </summary>
    private bool TurnPage(int direction, bool toEnd)
    {
        if (_layout.PageCount == 0)
        {
            return false;
        }

        var row = _layout.GetRowOfPage(ClampedCurrentPage);
        var target = direction > 0 ? row.LastPage + 1 : row.FirstPage - 1;
        if (target < 0 || target >= _layout.PageCount)
        {
            return false;
        }

        ScrollToPage(_layout.GetRowOfPage(target).FirstPage);
        if (toEnd && direction < 0)
        {
            SetVerticalOffset(MaxOffsetY);
        }

        return true;
    }

    /// <summary>
    /// 세로로 스크롤한다. 페이지 단위 모드에서 이미 페이지 끝에 닿아 있으면 다음(이전) 페이지로 넘어간다.
    /// </summary>
    private void ScrollVertically(double delta)
    {
        if (!IsPageMode)
        {
            SetVerticalOffset(_offsetY + delta);
            return;
        }

        var atEdge = delta > 0 ? _offsetY >= MaxOffsetY - 0.5 : _offsetY <= 0.5;
        if (atEdge)
        {
            TurnPage(Math.Sign(delta), toEnd: true);
        }
        else
        {
            SetVerticalOffset(_offsetY + delta);
        }
    }

    /// <summary>
    /// 터치패드 휠 스크롤. 페이지 끝에서 일정량 이상 더 굴려야 페이지가 넘어가고,
    /// 넘긴 뒤 이어지는 관성 스크롤로는 더 넘어가지 않는다.
    /// </summary>
    private void ScrollByWheel(double delta)
    {
        if (!IsPageMode)
        {
            SetVerticalOffset(_offsetY + delta);
            return;
        }

        var now = DateTime.UtcNow;
        var atEdge = delta > 0 ? _offsetY >= MaxOffsetY - 0.5 : _offsetY <= 0.5;
        if (!atEdge)
        {
            _wheelOverscroll = 0;
            SetVerticalOffset(_offsetY + delta);
            return;
        }

        if (now < _pageTurnAllowedAt)
        {
            // 페이지를 넘긴 직후 이어지는 휠(관성 스크롤)은 무시하고 대기 시간을 늘린다.
            _pageTurnAllowedAt = now + PageTurnQuietTime;
            return;
        }

        if (Math.Sign(delta) != Math.Sign(_wheelOverscroll))
        {
            _wheelOverscroll = 0;
        }

        _wheelOverscroll += delta;
        if (Math.Abs(_wheelOverscroll) >= WheelScrollPerNotch)
        {
            _wheelOverscroll = 0;
            if (TurnPage(Math.Sign(delta), toEnd: true))
            {
                _pageTurnAllowedAt = now + PageTurnQuietTime;
            }
        }
    }

    /// <summary>스크롤 위치, 배율, 크기가 바뀐 뒤에 호출한다.</summary>
    private void OnViewChanged()
    {
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
        UpdateCurrentPage();
        UpdateRenderRequests();
    }

    /// <summary>연속 스크롤에서는 화면에서 가장 넓게 보이는 페이지를 현재 페이지로 삼는다.</summary>
    private void UpdateCurrentPage()
    {
        if (IsPageMode)
        {
            return;
        }

        var visible = VisibleDocumentRect;
        var (first, last) = VisiblePages;
        if (last < first)
        {
            return;
        }

        // 가장 넓게 보이는 줄을 고른다. 2페이지 보기에서 같은 줄의 두 페이지 중 어느 쪽인지는 따지지 않는다.
        var bestRow = _layout.GetRowOfPage(first);
        var bestArea = -1.0;
        for (var i = first; i <= last;)
        {
            var row = _layout.GetRowOfPage(i);
            var area = 0.0;
            for (var p = row.FirstPage; p <= row.LastPage; p++)
            {
                var r = _layout.PageRects[p].Intersect(visible);
                area += r.Width * r.Height;
            }

            if (area > bestArea)
            {
                bestRow = row;
                bestArea = area;
            }

            i = row.LastPage + 1;
        }

        // 현재 페이지가 그 줄에 있으면 그대로 두고(사용자가 입력한 페이지 번호 유지), 아니면 줄의 첫 페이지로 한다.
        if (CurrentPageIndex < bestRow.FirstPage || CurrentPageIndex > bestRow.LastPage)
        {
            SetPropertyInternally(CurrentPageIndexProperty, bestRow.FirstPage);
        }
    }

    #endregion

    #region 렌더링 요청

    /// <summary>페이지를 화면 해상도로 렌더링할 때의 픽셀 크기.</summary>
    private (int Width, int Height) GetPagePixelSize(int pageIndex)
    {
        var rect = _layout.PageRects[pageIndex];
        return (Math.Max(1, (int)Math.Round(rect.Width * _dpiScale)),
            Math.Max(1, (int)Math.Round(rect.Height * _dpiScale)));
    }

    /// <summary>페이지 전체 비트맵의 픽셀 크기. 너무 크면 <see cref="MaxFullPagePixels"/>로 줄인다.</summary>
    private static (int Width, int Height) LimitFullPageSize(int width, int height)
    {
        var pixels = (long)width * height;
        if (pixels <= MaxFullPagePixels)
        {
            return (width, height);
        }

        var scale = Math.Sqrt((double)MaxFullPagePixels / pixels);
        return (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
    }

    private void UpdateRenderRequests()
    {
        if (_scheduler is null || _layout.PageCount == 0 || _viewport.Width <= 0 || !IsLoaded)
        {
            return;
        }

        var visible = VisibleDocumentRect;
        var (first, last) = VisiblePages;
        var requests = new List<RenderRequest>();

        // 1순위: 화면에 보이는 페이지 전체 비트맵
        for (var i = first; i <= last; i++)
        {
            AddFullPageRequest(i, requests);
        }

        // 2순위: 크게 확대했을 때 화면에 보이는 부분의 고해상도 렌더링
        for (var i = first; i <= last; i++)
        {
            AddDetailRequest(i, visible, requests);
        }

        // 3순위: 미리 렌더링
        foreach (var page in GetPrefetchPages(visible, first, last))
        {
            AddFullPageRequest(page, requests);
        }

        // 화면에서 벗어난 페이지의 상세 영역은 버린다.
        foreach (var page in _details.Keys.Where(p => p < first || p > last).ToList())
        {
            _details.Remove(page);
        }

        _scheduler.Update(requests);
    }

    /// <summary>
    /// 미리 렌더링할 페이지 (현재 화면에 가까운 순서).
    /// 연속 스크롤은 앞뒤 한 화면 분량, 페이지 단위는 앞뒤 줄.
    /// </summary>
    private IEnumerable<int> GetPrefetchPages(LayoutRect visible, int first, int last)
    {
        if (IsPageMode)
        {
            // 휠을 빠르게 돌려 여러 장을 넘길 때를 대비해 다음 줄, 이전 줄, 다다음 줄 순서로 미리 렌더링한다.
            var (windowFirst, windowLast) = WindowPages;
            var next = windowLast + 1;
            var afterNext = next < _layout.PageCount ? _layout.GetRowOfPage(next).LastPage + 1 : next;
            foreach (var start in new[] { next, windowFirst - 1, afterNext })
            {
                if (start < 0 || start >= _layout.PageCount)
                {
                    continue;
                }

                var row = _layout.GetRowOfPage(start);
                for (var i = row.FirstPage; i <= row.LastPage; i++)
                {
                    yield return i;
                }
            }

            yield break;
        }

        var (prefetchFirst, prefetchLast) =
            _layout.GetPagesInRange(visible.Y - visible.Height, visible.Bottom + visible.Height);
        for (var d = 1; first - d >= prefetchFirst || last + d <= prefetchLast; d++)
        {
            if (last + d <= prefetchLast)
            {
                yield return last + d;
            }

            if (first - d >= prefetchFirst && first - d >= 0)
            {
                yield return first - d;
            }
        }
    }

    private void AddFullPageRequest(int pageIndex, List<RenderRequest> requests)
    {
        if (_failedPages.Contains(pageIndex))
        {
            return;
        }

        var (width, height) = GetPagePixelSize(pageIndex);
        (width, height) = LimitFullPageSize(width, height);

        var hasImage = _pageImages.TryPeek(pageIndex, out var image);
        if (hasImage && (_zoomSettling || (image.PageWidth == width && image.PageHeight == height)))
        {
            // 확대/축소 중에는 기존 비트맵을 늘려서 쓰고, 새로 렌더링하지 않는다.
            return;
        }

        requests.Add(RenderRequest.FullPage(pageIndex, width, height));
    }

    private void AddDetailRequest(int pageIndex, LayoutRect visible, List<RenderRequest> requests)
    {
        var (width, height) = GetPagePixelSize(pageIndex);
        if (_zoomSettling || _failedPages.Contains(pageIndex) || (long)width * height <= MaxFullPagePixels)
        {
            return;
        }

        var pageRect = _layout.PageRects[pageIndex];
        var needed = ToPagePixels(pageRect.Intersect(visible), pageRect, width, height);
        if (needed.Area == 0)
        {
            return;
        }

        if (_details.TryGetValue(pageIndex, out var detail)
            && detail.Request.PageWidth == width
            && detail.Request.Region.Contains(needed))
        {
            return;
        }

        // 조금씩 스크롤할 때마다 다시 렌더링하지 않도록 화면보다 넓게 렌더링한다.
        var margin = new LayoutRect(
            visible.X - visible.Width / 4, visible.Y - visible.Height / 4,
            visible.Width * 1.5, visible.Height * 1.5);
        var region = ToPagePixels(pageRect.Intersect(margin), pageRect, width, height);
        requests.Add(new RenderRequest(pageIndex, width, height, region));
    }

    /// <summary>문서 좌표 사각형을 페이지 픽셀 좌표 사각형으로 바꾼다.</summary>
    private static PixelRect ToPagePixels(LayoutRect r, LayoutRect pageRect, int pageWidth, int pageHeight)
    {
        if (r.Width <= 0 || r.Height <= 0)
        {
            return default;
        }

        var sx = pageWidth / pageRect.Width;
        var sy = pageHeight / pageRect.Height;
        var left = Math.Clamp((int)Math.Floor((r.X - pageRect.X) * sx), 0, pageWidth);
        var top = Math.Clamp((int)Math.Floor((r.Y - pageRect.Y) * sy), 0, pageHeight);
        var right = Math.Clamp((int)Math.Ceiling((r.Right - pageRect.X) * sx), 0, pageWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((r.Bottom - pageRect.Y) * sy), 0, pageHeight);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    // 스레드 풀 스레드에서 호출된다.
    private void OnRenderCompleted(int version, RenderRequest request, PageBitmap bitmap)
    {
        BitmapSource source;
        using (bitmap)
        {
            source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgr32, null,
                bitmap.Buffer, bitmap.Stride);
            source.Freeze();
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (version != _documentVersion)
            {
                return;
            }

            if (request.IsFullPage)
            {
                _pageImages.Set(request.PageIndex, new PageImage(source, request.PageWidth, request.PageHeight));
            }
            else if (GetPagePixelSize(request.PageIndex).Width == request.PageWidth)
            {
                _details[request.PageIndex] = new DetailImage(source, request);
            }

            InvalidateVisual();
        });
    }

    private void OnRenderFailed(int version, RenderRequest request)
    {
        if (version == _documentVersion && _failedPages.Add(request.PageIndex))
        {
            InvalidateVisual();
        }
    }

    #endregion

    #region 그리기

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? Window.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? Window.Height : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize != _viewport)
        {
            _viewport = finalSize;
            if (ComputeZoomForMode() is { } zoom)
            {
                ApplyZoom(zoom, null);
            }

            ClampOffsets();
            OnViewChanged();
        }

        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _dpiScale = newDpi.DpiScaleX;
        OnViewChanged();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // 빈 곳에서도 마우스 휠 이벤트를 받도록 투명 배경을 그린다.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var (first, last) = VisiblePages;
        for (var i = first; i <= last; i++)
        {
            DrawPage(dc, i);
        }
    }

    private void DrawPage(DrawingContext dc, int pageIndex)
    {
        var (width, height) = GetPagePixelSize(pageIndex);
        var rect = DocumentToViewSnapped(_layout.PageRects[pageIndex], width, height);

        dc.DrawRectangle(ShadowBrush, null, new Rect(rect.X + 1, rect.Y + 2, rect.Width, rect.Height));
        dc.DrawRectangle(PageBrush, null, rect);

        if (_pageImages.TryGetValue(pageIndex, out var image))
        {
            dc.DrawImage(image.Bitmap, rect);
        }

        if (_details.TryGetValue(pageIndex, out var detail) && detail.Request.PageWidth == width)
        {
            var region = detail.Request.Region;
            var scale = 1 / _dpiScale;
            dc.DrawImage(detail.Bitmap, new Rect(
                rect.X + region.X * scale, rect.Y + region.Y * scale, region.Width * scale, region.Height * scale));
        }

        if (_failedPages.Contains(pageIndex))
        {
            var text = new FormattedText("이 페이지를 표시할 수 없습니다.", CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, MessageTypeface, 14, Brushes.Gray, _dpiScale);
            dc.DrawText(text, new Point(rect.X + (rect.Width - text.Width) / 2, rect.Y + 24));
        }

        dc.DrawRectangle(null, PageBorderPen, rect);
    }

    #endregion

    #region 마우스

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Document is null)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // 한 칸에 10%씩. 정밀 터치패드의 핀치 확대/축소도 Ctrl+휠로 들어온다.
            var zoom = Zoom * Math.Pow(1.1, e.Delta / 120.0);
            SetPropertyInternally(ZoomModeProperty, ZoomMode.Custom);
            ApplyZoom(zoom, e.GetPosition(this));
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            SetHorizontalOffset(_offsetX - e.Delta * WheelScrollPerNotch / 120);
        }
        else if (IsPageMode && e.Delta != 0 && e.Delta % Mouse.MouseWheelDeltaForOneLine == 0)
        {
            // 일반 마우스 휠은 한 칸에 120씩 들어온다(빠르게 돌리면 여러 칸이 한 번에 올 수 있다).
            // 칸마다 페이지 안을 스크롤하거나, 끝에 닿아 있으면 페이지를 넘겨서 돌린 칸 수만큼 넘어가게 한다.
            var direction = -Math.Sign(e.Delta);
            var notches = Math.Abs(e.Delta) / Mouse.MouseWheelDeltaForOneLine;
            for (var i = 0; i < notches; i++)
            {
                ScrollVertically(direction * WheelScrollPerNotch);
            }
        }
        else
        {
            // 정밀 터치패드(와 고해상도 휠)는 작은 Delta를 여러 번 보내므로 Delta에 비례해서 스크롤한다.
            ScrollByWheel(-e.Delta * WheelScrollPerNotch / 120);
        }

        e.Handled = true;
    }

    #endregion

    #region IScrollInfo

    public bool CanVerticallyScroll { get; set; }

    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth => Window.Width;

    public double ExtentHeight => Window.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => _offsetX;

    public double VerticalOffset => _offsetY;

    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => ScrollVertically(-LineScrollAmount);

    public void LineDown() => ScrollVertically(LineScrollAmount);

    // 페이지 단위 모드에서 가로로 스크롤할 것이 없으면 ←/→로 페이지를 넘긴다.
    // 우→좌 책에서는 ←가 다음 페이지다.
    public void LineLeft()
    {
        if (!IsPageMode || MaxOffsetX > 0 || !TurnPage(RightToLeft ? 1 : -1, toEnd: false))
        {
            SetHorizontalOffset(_offsetX - LineScrollAmount);
        }
    }

    public void LineRight()
    {
        if (!IsPageMode || MaxOffsetX > 0 || !TurnPage(RightToLeft ? -1 : 1, toEnd: false))
        {
            SetHorizontalOffset(_offsetX + LineScrollAmount);
        }
    }

    public void PageUp() => ScrollVertically(-PageScrollAmount);

    public void PageDown() => ScrollVertically(PageScrollAmount);

    public void PageLeft() => SetHorizontalOffset(_offsetX - _viewport.Width);

    public void PageRight() => SetHorizontalOffset(_offsetX + _viewport.Width);

    public void MouseWheelUp() => ScrollByWheel(-WheelScrollPerNotch);

    public void MouseWheelDown() => ScrollByWheel(WheelScrollPerNotch);

    public void MouseWheelLeft() => SetHorizontalOffset(_offsetX - WheelScrollPerNotch);

    public void MouseWheelRight() => SetHorizontalOffset(_offsetX + WheelScrollPerNotch);

    // 한 화면씩 넘길 때 이전 화면의 끝부분이 조금 남도록 한다.
    private double PageScrollAmount => Math.Max(LineScrollAmount, _viewport.Height - LineScrollAmount);

    public void SetHorizontalOffset(double offset)
    {
        var old = _offsetX;
        _offsetX = offset;
        ClampOffsets();
        if (old != _offsetX)
        {
            OnViewChanged();
        }
    }

    public void SetVerticalOffset(double offset)
    {
        var old = _offsetY;
        _offsetY = offset;
        ClampOffsets();
        if (old != _offsetY)
        {
            OnViewChanged();
        }
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    #endregion

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        var pen = new Pen(CreateFrozenBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>페이지 전체 비트맵. 페이지를 PageWidth x PageHeight 픽셀로 렌더링한 결과다.</summary>
    private sealed record PageImage(BitmapSource Bitmap, int PageWidth, int PageHeight);

    /// <summary>크게 확대했을 때 화면에 보이는 부분만 원래 해상도로 렌더링한 결과.</summary>
    private sealed record DetailImage(BitmapSource Bitmap, RenderRequest Request);
}
