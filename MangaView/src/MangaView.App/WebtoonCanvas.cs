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
    private IReadOnlyList<ImagePage> _pages = Array.Empty<ImagePage>();
    private WebtoonLayout? _layout;
    private double _gap = 8;
    private double _offset;
    private double _viewportHeight;
    private readonly LruCache<BitmapSource> _cache = new(512L * 1024 * 1024);
    private readonly DecodeScheduler _scheduler = new(new WpfDecodeWorker(), maxConcurrent: 2);
    private readonly HashSet<string> _failed = new();

    public event Action<double>? OffsetChangeRequested;
    public event Action<int, int>? CurrentPageChanged;
    public int CurrentPageIndex { get; private set; } = -1;
    public int PageCount => _pages.Count;

    public WebtoonCanvas()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        _cache.ItemEvicted += (_, _, _) => Dispatcher.BeginInvoke(InvalidateVisual);
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
        UpdateCurrentPage(verticalOffset + viewportHeight * 0.5);
    }

    public void JumpToPage(int index)
    {
        if (_layout is null || _pages.Count == 0) return;
        int i = Math.Clamp(index, 0, _pages.Count - 1);
        OffsetChangeRequested?.Invoke(_layout.PageTop(i));
    }

    private void RebuildLayout(bool preserveAnchor)
    {
        double width = Math.Max(1, ActualWidth);
        WebtoonLayout? old = _layout;
        ScrollAnchor anchor = old is not null && preserveAnchor ? old.GetAnchor(_offset) : default;
        _layout = new WebtoonLayout(_pages, width, _gap);
        Height = _layout.TotalHeight;
        if (preserveAnchor && old is not null && _layout.Pages.Count > 0)
        {
            // 页内偏移按新旧页高比例缩放，尽量保持视觉锚点
            int pi = Math.Clamp(anchor.PageIndex, 0, _layout.Pages.Count - 1);
            double oldH = old.PageHeight(pi);
            double ratio = oldH > 0 ? anchor.InPageOffset / oldH : 0;
            OffsetChangeRequested?.Invoke(_layout.PageTop(pi) + ratio * _layout.PageHeight(pi));
        }
        else
        {
            OffsetChangeRequested?.Invoke(0);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.WidthChanged)
        {
            RebuildLayout(preserveAnchor: true);
            InvalidateVisual();
        }
    }

    private void RequestDecodes(double top, double bottom)
    {
        int viewportWidth = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        int[] visible = _layout!.PagesIntersecting(top, bottom).ToArray();
        foreach (int i in visible)
            Request(i, viewportWidth, DecodePriority.Viewport);
        if (visible.Length > 0)
        {
            if (visible[0] > 0)
                Request(visible[0] - 1, viewportWidth, DecodePriority.Preload);
            if (visible[^1] < _pages.Count - 1)
                Request(visible[^1] + 1, viewportWidth, DecodePriority.Preload);
        }
    }

    private void Request(int index, int viewportWidth, DecodePriority priority)
    {
        var page = _pages[index];
        // 不放大小图：解码宽度 = min(视口宽, 原图宽)
        int decodeWidth = Math.Min(viewportWidth, Math.Max(1, page.Width));
        string key = CacheKey(index, decodeWidth);
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
            if (t.Result is BitmapSource bmp)
            {
                long bytes = (long)Math.Max(1, bmp.PixelWidth) * Math.Max(1, bmp.PixelHeight) * 4;
                _cache.Put(key, bmp, bytes);
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
        double width = Math.Max(1, ActualWidth);

        foreach (int i in _layout.PagesIntersecting(top, bottom))
        {
            double pageTop = _layout.PageTop(i);
            double pageHeight = _layout.PageHeight(i);
            Rect rect = new(0, pageTop, width, pageHeight);
            int decodeWidth = Math.Min((int)Math.Ceiling(width), Math.Max(1, _pages[i].Width));
            string key = CacheKey(i, decodeWidth);

            if (_cache.TryGet(key, out var bmp))
            {
                dc.DrawImage(bmp, rect);
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

    private static string CacheKey(int index, int decodeWidth) => $"p{index}:w{decodeWidth}";
}


