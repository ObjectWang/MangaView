using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MangaView.Core;

namespace MangaView.App;

/// <summary>
/// 智能双页画布：
/// - 独立封面页：第一页单独显示，第二页开始两两配对；
/// - 左到右 / 右到左阅读方向；
/// - 双页各自按高度适配并垂直居中，中间间距可调；
/// - 只解码当前跨页，按字节预算 LRU 缓存。
/// </summary>
public sealed class DoublePageCanvas : FrameworkElement
{
    private IReadOnlyList<ImagePage> _pages = Array.Empty<ImagePage>();
    private DoublePageLayout? _layout;
    private ReadingDirection _direction = ReadingDirection.LeftToRight;
    private bool _coverPage = true;
    private double _gap = 12;
    private int _spreadIndex;
    private double _viewportWidth;
    private double _viewportHeight;
    private readonly LruCache<object> _cache = new(512L * 1024 * 1024);
    private readonly DecodeScheduler _scheduler = new(new WpfDecodeWorker(), maxConcurrent: 2);
    private readonly HashSet<string> _failed = new();
    private readonly HashSet<AnimatedImageSource> _animatedSubscriptions = new();

    public event Action<int, int>? CurrentPageChanged;
    public int CurrentPageIndex { get; private set; } = -1;
    public int CurrentSpreadIndex => _spreadIndex;
    public int SpreadCount => _layout?.SpreadCount ?? 0;
    public int PageCount => _pages.Count;

    public DoublePageCanvas()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        _cache.ItemEvicted += (_, value, _) =>
        {
            if (value is IDisposable disposable) disposable.Dispose();
            Dispatcher.BeginInvoke(InvalidateVisual);
        };
    }

    public void SetPages(IReadOnlyList<ImagePage> pages)
    {
        DetachAnimated();
        _pages = pages ?? Array.Empty<ImagePage>();
        _cache.Clear();
        _failed.Clear();
        _spreadIndex = 0;
        RebuildLayout(preservePage: false);
        InvalidateVisual();
        RaiseCurrentPageChanged();
    }

    public void SetViewport(double width, double height)
    {
        double nextWidth = Math.Max(0, width);
        double nextHeight = Math.Max(0, height);
        if (Math.Abs(nextWidth - _viewportWidth) < 0.5 &&
            Math.Abs(nextHeight - _viewportHeight) < 0.5)
            return;
        _viewportWidth = nextWidth;
        _viewportHeight = nextHeight;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Configure(ReadingDirection direction, bool coverPage, double gap)
    {
        _direction = direction;
        _coverPage = coverPage;
        _gap = Math.Max(0, gap);
        RebuildLayout(preservePage: true);
        InvalidateVisual();
        RaiseCurrentPageChanged();
    }

    public void GoToPage(int pageIndex)
    {
        if (_layout is null || _layout.SpreadCount == 0) return;
        _spreadIndex = Math.Clamp(
            _layout.SpreadIndexForPage(Math.Clamp(pageIndex, 0, Math.Max(0, _pages.Count - 1))),
            0, _layout.SpreadCount - 1);
        InvalidateVisual();
        RaiseCurrentPageChanged();
    }

    public bool NextSpread()
    {
        if (_layout is null || _spreadIndex >= _layout.SpreadCount - 1) return false;
        _spreadIndex++;
        InvalidateVisual();
        RaiseCurrentPageChanged();
        return true;
    }

    public bool PreviousSpread()
    {
        if (_layout is null || _spreadIndex <= 0) return false;
        _spreadIndex--;
        InvalidateVisual();
        RaiseCurrentPageChanged();
        return true;
    }

    private void RebuildLayout(bool preservePage)
    {
        int currentPage = preservePage ? CurrentPageIndex : 0;
        _layout = new DoublePageLayout(_pages, _coverPage, _direction);
        if (_layout.SpreadCount == 0)
        {
            _spreadIndex = 0;
            return;
        }
        _spreadIndex = Math.Clamp(
            _layout.SpreadIndexForPage(Math.Clamp(currentPage, 0, Math.Max(0, _pages.Count - 1))),
            0, _layout.SpreadCount - 1);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = _viewportWidth > 0
            ? _viewportWidth
            : double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        double height = _viewportHeight > 0
            ? _viewportHeight
            : double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height;
        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_layout is null || _layout.SpreadCount == 0 || _pages.Count == 0) return;

        var spread = _layout.SpreadAt(_spreadIndex);
        double width = Math.Max(1, ActualWidth);
        double height = Math.Max(1, RenderSize.Height);

        if (spread.IsSingle)
        {
            int index = Math.Max(spread.LeftPageIndex, spread.RightPageIndex);
            DrawPage(dc, index, new Rect(0, 0, width, height), centeredSingle: true);
            return;
        }

        double cellWidth = Math.Max(1, (width - _gap) / 2);
        DrawPage(dc, spread.LeftPageIndex, new Rect(0, 0, cellWidth, height), centeredSingle: false);
        DrawPage(dc, spread.RightPageIndex, new Rect(cellWidth + _gap, 0, cellWidth, height), centeredSingle: false);
    }

    private void DrawPage(DrawingContext dc, int index, Rect cell, bool centeredSingle)
    {
        if (index < 0 || index >= _pages.Count) return;
        var page = _pages[index];
        int decodeWidth = Math.Min((int)Math.Ceiling(cell.Width), Math.Max(1, page.Width));
        string key = CacheKey(page, decodeWidth);

        if (_cache.TryGet(key, out var decoded))
        {
            Rect rect = FitRect(page, cell, centeredSingle);
            dc.DrawImage(GetFrame(decoded), rect);
        }
        else
        {
            DrawPlaceholder(dc, index, cell);
            Request(index, decodeWidth);
        }
    }

    private static Rect FitRect(ImagePage page, Rect cell, bool centeredSingle)
    {
        if (page.Width <= 0 || page.Height <= 0) return cell;
        // 单页/封面使用整个可用区域；双页限制在半宽单元格内。
        double scale = Math.Min(cell.Width / page.Width, cell.Height / page.Height);
        double w = page.Width * scale;
        double h = page.Height * scale;
        double x = cell.Left + (cell.Width - w) / 2;
        double y = cell.Top + (cell.Height - h) / 2;
        return new Rect(x, y, w, h);
    }

    private void Request(int index, int decodeWidth)
    {
        var page = _pages[index];
        string key = CacheKey(page, decodeWidth);
        if (_failed.Contains(key) || _cache.TryGet(key, out _)) return;

        var request = new DecodeRequest(key, index, page.Path, decodeWidth, DecodePriority.Viewport);
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

    private void DrawPlaceholder(DrawingContext dc, int index, Rect cell)
    {
        var bg = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        bg.Freeze();
        var rect = FitRect(_pages[index], cell, centeredSingle: false);
        dc.DrawRectangle(bg, null, rect);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
        pen.Freeze();
        dc.DrawRectangle(null, pen, rect);

        var ft = new FormattedText(
            $"第 {index + 1} 页加载中…",
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            16,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(
            Math.Max(cell.Left, cell.Left + (cell.Width - ft.Width) / 2),
            Math.Max(cell.Top, cell.Top + (cell.Height - ft.Height) / 2)));
    }

    private void RaiseCurrentPageChanged()
    {
        int current = _layout is { SpreadCount: > 0 }
            ? _layout.SpreadAt(_spreadIndex).CurrentPage(_direction)
            : -1;
        CurrentPageIndex = current;
        CurrentPageChanged?.Invoke(current, _pages.Count);
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

    private static string CacheKey(ImagePage page, int decodeWidth) =>
        $"d{page.Index}:w{decodeWidth}:{page.Path}";
}
