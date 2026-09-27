using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MangaView.Core;

namespace MangaView.App;

/// <summary>
/// Webtoon 虚拟滚动画布：
/// - 只解码/绘制视口相交页与上下相邻页（预加载）；
/// - 已解码位图按字节预算 LRU 缓存，超出范围自动淘汰；
/// - 解码任务通过优先级调度（视口 &gt; 预加载），后台线程完成后冻结位图回 UI 重绘；
/// - 滚动位置使用“页码 + 页内偏移”锚点，窗口宽度变化时按比例恢复视觉位置。
/// </summary>
public sealed class WebtoonCanvas : FrameworkElement
{
    private const double MinZoomFactor = 0.05;
    private const double MaxZoomFactor = 8.0;

    private IReadOnlyList<ImagePage> _pages = Array.Empty<ImagePage>();
    private WebtoonLayout? _layout;
    private double _gap = 8;
    private double _offset;
    private double _viewportWidth;
    private double _viewportHeight;
    private double _zoomFactor = 1.0;
    private readonly LruCache<object> _cache = new(512L * 1024 * 1024);
    private readonly DecodeScheduler _scheduler = new(new WpfDecodeWorker(), maxConcurrent: 2);
    private readonly HashSet<string> _failed = new();
    private readonly HashSet<AnimatedImageSource> _animatedSubscriptions = new();

    public event Action<double>? OffsetChangeRequested;
    public event Action<int, int>? CurrentPageChanged;
    public event Action<double>? ZoomChanged;
    public int CurrentPageIndex { get; private set; } = -1;
    public int PageCount => _pages.Count;
    public double TotalHeight => _layout?.TotalHeight ?? 0;
    public double ZoomFactor => _zoomFactor;
    public double ZoomPercent => _zoomFactor * 100.0;

    /// <summary>当前滚动锚点：页码 + 页内偏移，用于阅读进度保存/恢复。</summary>
    public ScrollAnchor CurrentAnchor => _layout?.GetAnchor(_offset) ?? default;

    public void JumpToAnchor(ScrollAnchor anchor)
    {
        if (_layout is null || _pages.Count == 0) return;
        OffsetChangeRequested?.Invoke(_layout.OffsetFromAnchor(anchor));
    }

    public double OffsetFromAnchor(ScrollAnchor anchor) =>
        _layout is null || _pages.Count == 0 ? 0 : _layout.OffsetFromAnchor(anchor);

    public void JumpToOffset(double offset) => OffsetChangeRequested?.Invoke(Math.Max(0, offset));

    public void SetZoomFactor(double factor)
    {
        _zoomFactor = Math.Clamp(factor, MinZoomFactor, MaxZoomFactor);
        RebuildLayout(preserveAnchor: false);
        InvalidateVisual();
        ZoomChanged?.Invoke(ZoomPercent);
    }

    public void ResetZoom()
    {
        _zoomFactor = 1.0;
        RebuildLayout(preserveAnchor: false);
        InvalidateVisual();
        ZoomChanged?.Invoke(ZoomPercent);
    }

    /// <summary>以宿主视口中的鼠标位置缩放，并返回缩放后应设置的滚动偏移。</summary>
    public (double HorizontalOffset, double VerticalOffset) ZoomAt(
        double factor, Point viewportPoint, double horizontalOffset, double verticalOffset)
    {
        if (_layout is null || _pages.Count == 0 || !double.IsFinite(factor) || factor <= 0)
            return (horizontalOffset, verticalOffset);

        WebtoonLayout oldLayout = _layout;
        ScrollAnchor anchor = oldLayout.GetAnchor(verticalOffset + viewportPoint.Y);
        int pageIndex = Math.Clamp(anchor.PageIndex, 0, _pages.Count - 1);
        double oldPageHeight = oldLayout.PageHeight(pageIndex);
        double pageRatioY = oldPageHeight > 0 ? Math.Clamp(anchor.InPageOffset / oldPageHeight, 0, 1) : 0;

        double oldDrawWidth = oldLayout.ViewportWidth;
        double oldCanvasWidth = Math.Max(_viewportWidth, oldDrawWidth);
        double oldPageLeft = Math.Max(0, (oldCanvasWidth - oldDrawWidth) / 2);
        double contentX = horizontalOffset + viewportPoint.X - oldPageLeft;
        double pageRatioX = Math.Clamp(contentX / Math.Max(1, oldDrawWidth), 0, 1);

        _zoomFactor = Math.Clamp(_zoomFactor * factor, MinZoomFactor, MaxZoomFactor);
        RebuildLayout(preserveAnchor: false);

        double newPageHeight = _layout!.PageHeight(pageIndex);
        double newDrawWidth = _layout.ViewportWidth;
        double newCanvasWidth = Math.Max(_viewportWidth, newDrawWidth);
        double newPageLeft = Math.Max(0, (newCanvasWidth - newDrawWidth) / 2);
        double newVertical = _layout.PageTop(pageIndex) + pageRatioY * newPageHeight - viewportPoint.Y;
        double newHorizontal = newPageLeft + pageRatioX * newDrawWidth - viewportPoint.X;
        ZoomChanged?.Invoke(ZoomPercent);
        return (Math.Max(0, newHorizontal), Math.Max(0, newVertical));
    }

    public void SetViewport(double width, double height)
    {
        double nextWidth = Math.Max(0, width);
        _viewportHeight = Math.Max(0, height);
        if (Math.Abs(nextWidth - _viewportWidth) < 0.5) return;
        _viewportWidth = nextWidth;
        RebuildLayout(preserveAnchor: true);
        InvalidateVisual();
    }

    public WebtoonCanvas()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        _cache.ItemEvicted += (_, value, _) =>
        {
            if (value is IDisposable disposable) disposable.Dispose();
            Dispatcher.BeginInvoke(InvalidateVisual);
        };
    }

    public double Gap
    {
        get => _gap;
        set
        {
            _gap = Math.Max(0, value);
            RebuildLayout(preserveAnchor: true);
            InvalidateVisual();
        }
    }

    public void SetPages(IReadOnlyList<ImagePage> pages)
    {
        DetachAnimated();
        _pages = pages ?? Array.Empty<ImagePage>();
        _cache.Clear();
        _failed.Clear();
        _offset = 0;
        RebuildLayout(preserveAnchor: false);
        InvalidateVisual();
        UpdateCurrentPage(0);
    }

    /// <summary>由宿主 ScrollViewer 在滚动/尺寸变化时调用：触发解码、重绘与当前页更新。</summary>
    public void OnViewScrolled(double verticalOffset, double viewportHeight)
    {
        _offset = verticalOffset;
        _viewportHeight = viewportHeight;
        if (_layout is null || _pages.Count == 0) return;
        double bottom = verticalOffset + Math.Max(1, viewportHeight);
        RequestDecodes(verticalOffset, bottom);
        InvalidateVisual();
        // Webtoon 页码按视口顶部附近判定，确保“跳转到第 N 页”后立即显示 N。
        UpdateCurrentPage(verticalOffset + Math.Min(viewportHeight * 0.15, 120));
    }

    public void JumpToPage(int index)
    {
        if (_layout is null || _pages.Count == 0) return;
        int i = Math.Clamp(index, 0, _pages.Count - 1);
        OffsetChangeRequested?.Invoke(_layout.PageTop(i));
    }

    private void RebuildLayout(bool preserveAnchor)
    {
        double viewportWidth = Math.Max(1, _viewportWidth > 0 ? _viewportWidth : ActualWidth);
        WebtoonLayout? old = _layout;
        ScrollAnchor anchor = old is not null && preserveAnchor ? old.GetAnchor(_offset) : default;
        _layout = new WebtoonLayout(_pages, viewportWidth * _zoomFactor, _gap);
        Width = Math.Max(viewportWidth, _layout.ViewportWidth);
        Height = _layout.TotalHeight;
        if (preserveAnchor && old is not null && _layout.Pages.Count > 0)
        {
            // 页内偏移按新旧页高比例缩放，尽量保持视觉锚点
            int pi = Math.Clamp(anchor.PageIndex, 0, _layout.Pages.Count - 1);
            double oldH = old.PageHeight(pi);
            double ratio = oldH > 0 ? anchor.InPageOffset / oldH : 0;
            OffsetChangeRequested?.Invoke(_layout.PageTop(pi) + ratio * _layout.PageHeight(pi));
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    private void RequestDecodes(double top, double bottom)
    {
        int[] visible = _layout!.PagesIntersecting(top, bottom).ToArray();
        foreach (int i in visible)
            Request(i, DecodePriority.Viewport);
        if (visible.Length > 0)
        {
            if (visible[0] > 0)
                Request(visible[0] - 1, DecodePriority.Preload);
            if (visible[^1] < _pages.Count - 1)
                Request(visible[^1] + 1, DecodePriority.Preload);
        }
    }

    private void Request(int index, DecodePriority priority)
    {
        var page = _pages[index];
        // 不放大小图：解码宽度 = min(缩放后的布局宽度, 原图宽)
        int layoutWidth = Math.Max(1, (int)Math.Ceiling(_layout?.ViewportWidth ?? _viewportWidth));
        int decodeWidth = Math.Min(layoutWidth, Math.Max(1, page.Width));
        string key = CacheKey(index, decodeWidth, page.Path);
        if (_failed.Contains(key) || _cache.TryGet(key, out _)) return;

        var request = new DecodeRequest(key, index, page.Path, decodeWidth, priority);
        _scheduler.RequestAsync(request).ContinueWith(t =>
        {
            if (t.IsFaulted || t.IsCanceled)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _failed.Add(key);
                    InvalidateVisual();
                });
                return;
            }
            if (t.Result is { } decoded)
            {
                long bytes = decoded is AnimatedImageSource animated
                    ? animated.EstimatedBytes
                    : (long)Math.Max(1, ((BitmapSource)decoded).PixelWidth) *
                      Math.Max(1, ((BitmapSource)decoded).PixelHeight) * 4;
                _cache.Put(key, decoded, bytes);
                Dispatcher.BeginInvoke(InvalidateVisual);
            }
        }, TaskScheduler.Default);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_layout is null || _pages.Count == 0) return;

        double top = _offset;
        double bottom = _offset + Math.Max(1, _viewportHeight > 0 ? _viewportHeight : RenderSize.Height);
        double canvasWidth = Math.Max(1, ActualWidth);
        double drawWidth = Math.Max(1, _layout.ViewportWidth);
        double drawLeft = Math.Max(0, (canvasWidth - drawWidth) / 2);

        foreach (int i in _layout.PagesIntersecting(top, bottom))
        {
            double pageTop = _layout.PageTop(i);
            double pageHeight = _layout.PageHeight(i);
            Rect rect = new(drawLeft, pageTop, drawWidth, pageHeight);
            int decodeWidth = Math.Min((int)Math.Ceiling(drawWidth), Math.Max(1, _pages[i].Width));
            string key = CacheKey(i, decodeWidth, _pages[i].Path);

            if (_cache.TryGet(key, out var decoded))
            {
                dc.DrawImage(GetFrame(decoded), rect);
            }
            else
            {
                DrawPlaceholder(dc, i, rect, _failed.Contains(key));
            }
        }
    }

    private void DrawPlaceholder(DrawingContext dc, int index, Rect rect, bool failed)
    {
        var bg = new SolidColorBrush(failed ? Color.FromRgb(0x40, 0x24, 0x24) : Color.FromRgb(0x22, 0x22, 0x22));
        bg.Freeze();
        dc.DrawRectangle(bg, null, rect);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
        pen.Freeze();
        dc.DrawRectangle(null, pen, rect);

        string text = failed ? $"第 {index + 1} 页解码失败" : $"第 {index + 1} 页加载中…";
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            16,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double tx = Math.Max(rect.Left, (rect.Width - ft.Width) / 2);
        double ty = Math.Max(rect.Top, rect.Top + rect.Height / 2 - ft.Height / 2);
        dc.DrawText(ft, new Point(tx, ty));
    }

    private void UpdateCurrentPage(double centerY)
    {
        if (_layout is null || _pages.Count == 0) return;
        int idx = Math.Clamp(_layout.FindPageAt(centerY), 0, _pages.Count - 1);
        if (idx != CurrentPageIndex)
        {
            CurrentPageIndex = idx;
            CurrentPageChanged?.Invoke(idx, _pages.Count);
        }
    }

    private BitmapSource GetFrame(object decoded)
    {
        if (decoded is not AnimatedImageSource animated) return (BitmapSource)decoded;
        if (_animatedSubscriptions.Add(animated))
            animated.FrameChanged += OnAnimatedFrameChanged;
        return animated.CurrentFrame;
    }

    private void OnAnimatedFrameChanged() =>
        Dispatcher.BeginInvoke(new Action(InvalidateVisual));

    private void DetachAnimated()
    {
        foreach (var animated in _animatedSubscriptions)
            animated.FrameChanged -= OnAnimatedFrameChanged;
        _animatedSubscriptions.Clear();
    }

    private static string CacheKey(int index, int decodeWidth, string path) =>
        $"p{index}:w{decodeWidth}:{path}";
}
