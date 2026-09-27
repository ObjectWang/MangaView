using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MangaView.Core;

namespace MangaView.App;

/// <summary>
/// 单页看图画布：只解码当前页，支持适应窗口/宽度/高度、原始尺寸、
/// 5%–6400% 缩放、旋转、翻转和损坏图片占位。画布填充宿主视口，
/// 图片大于视口时由外层 ScrollViewer 提供平移。
/// </summary>
public sealed class SinglePageCanvas : FrameworkElement
{
    private IReadOnlyList<ImagePage> _pages = Array.Empty<ImagePage>();
    private int _currentIndex = -1;
    private int _generation;
    private Size _viewport;
    private BitmapSource? _bitmap;
    private AnimatedImageSource? _animated;
    private string? _loadedPath;
    private string? _failedKey;
    private double _pixelWidth = 1;
    private double _pixelHeight = 1;
    private double _lastReportedZoom = -1;
    private readonly ImageTransformState _transform = new();
    private readonly LruCache<object> _cache = new(128L * 1024 * 1024);
    private readonly DecodeScheduler _scheduler = new(new WpfDecodeWorker(), maxConcurrent: 2);

    public event Action<int, int>? CurrentPageChanged;
    public event Action<double>? ZoomChanged;

    public int CurrentPageIndex => _currentIndex;
    public int PageCount => _pages.Count;
    public ImageTransformState Transform => _transform;
    public bool SmoothTransitions { get; set; } = true;

    public double DisplayWidth => GetDisplaySize().Width;
    public double DisplayHeight => GetDisplaySize().Height;
    public double ZoomPercent =>
        _transform.GetEffectiveScale(_pixelWidth, _pixelHeight, _viewport.Width, _viewport.Height) * 100.0;

    public SinglePageCanvas()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        _cache.ItemEvicted += (_, value, _) =>
        {
            if (value is IDisposable disposable) disposable.Dispose();
            Dispatcher.BeginInvoke(new Action(InvalidateVisual));
        };
    }

    public void SetPages(IReadOnlyList<ImagePage> pages, int index)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _generation++;
        _pages = pages;
        _cache.Clear();
        _currentIndex = pages.Count == 0 ? -1 : Math.Clamp(index, 0, pages.Count - 1);
        LoadCurrentPage(animate: false);
        if (_currentIndex >= 0)
            CurrentPageChanged?.Invoke(_currentIndex, _pages.Count);
    }

    public void SetPageIndex(int index)
    {
        if (_pages.Count == 0) return;
        int next = Math.Clamp(index, 0, _pages.Count - 1);
        if (next == _currentIndex) return;
        _currentIndex = next;
        LoadCurrentPage(animate: true);
        CurrentPageChanged?.Invoke(_currentIndex, _pages.Count);
    }

    public void SetViewport(double width, double height)
    {
        var next = new Size(Math.Max(0, width), Math.Max(0, height));
        if (Math.Abs(next.Width - _viewport.Width) < 0.5 &&
            Math.Abs(next.Height - _viewport.Height) < 0.5)
            return;

        _viewport = next;
        if (_transform.Mode is ZoomMode.FitWindow or ZoomMode.FitWidth or ZoomMode.FitHeight)
        {
            InvalidateMeasure();
            InvalidateVisual();
            RaiseZoomChanged();
        }
    }

    public void SetZoomMode(ZoomMode mode)
    {
        _transform.SetZoomMode(mode);
        UpdateTransform();
    }

    public void RestoreZoom(ZoomMode mode, double scale)
    {
        _transform.Restore(mode, scale);
        UpdateTransform();
    }

    public void ZoomBy(double factor)
    {
        _transform.ZoomBy(factor);
        UpdateTransform();
    }

    public void RotateRight()
    {
        _transform.RotateRight();
        UpdateTransform();
    }

    public void RotateLeft()
    {
        _transform.RotateLeft();
        UpdateTransform();
    }

    public void ToggleHorizontalFlip()
    {
        _transform.ToggleHorizontalFlip();
        InvalidateVisual();
    }

    public void ToggleVerticalFlip()
    {
        _transform.ToggleVerticalFlip();
        InvalidateVisual();
    }

    public void ResetTransform()
    {
        _transform.Reset();
        UpdateTransform();
    }

    private void UpdateTransform()
    {
        InvalidateMeasure();
        InvalidateVisual();
        RaiseZoomChanged();
    }

    private void LoadCurrentPage(bool animate)
    {
        DetachAnimated();
        _bitmap = null;
        _loadedPath = null;
        _failedKey = null;
        if (_currentIndex < 0 || _currentIndex >= _pages.Count)
        {
            _pixelWidth = _pixelHeight = 1;
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        var page = _pages[_currentIndex];
        _pixelWidth = Math.Max(1, page.Width);
        _pixelHeight = Math.Max(1, page.Height);
        _failedKey = null;
        InvalidateMeasure();
        InvalidateVisual();

        string key = DecodeKey(page);
        if (_cache.TryGet(key, out var cached))
        {
            ApplyDecoded(cached, page, key, animate);
            return;
        }

        int generation = _generation;
        _scheduler.CancelQueued();
        var request = new DecodeRequest(key, _currentIndex, page.Path, 0, DecodePriority.Viewport);
        _scheduler.RequestAsync(request).ContinueWith(t =>
        {
            if (t.IsFaulted || t.IsCanceled)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation != _generation) return;
                    _failedKey = key;
                    InvalidateVisual();
                }));
                return;
            }

            if (t.Result is not { } decoded) return;
            long bytes = decoded is AnimatedImageSource animated
                ? animated.EstimatedBytes
                : (long)Math.Max(1, ((BitmapSource)decoded).PixelWidth) *
                  Math.Max(1, ((BitmapSource)decoded).PixelHeight) * 4;
            _cache.Put(key, decoded, bytes);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != _generation ||
                    _currentIndex < 0 || _currentIndex >= _pages.Count ||
                    !string.Equals(DecodeKey(_pages[_currentIndex]), key, StringComparison.Ordinal))
                    return;
                ApplyDecoded(decoded, _pages[_currentIndex], key, animate);
            }));
        }, TaskScheduler.Default);
    }

    private void ApplyDecoded(object decoded, ImagePage page, string key, bool animate)
    {
        DetachAnimated();
        BitmapSource bitmap;
        if (decoded is AnimatedImageSource animated)
        {
            _animated = animated;
            animated.FrameChanged += OnAnimatedFrameChanged;
            bitmap = animated.CurrentFrame;
        }
        else
        {
            bitmap = (BitmapSource)decoded;
        }

        _bitmap = bitmap;
        _loadedPath = page.Path;
        _failedKey = null;
        _pixelWidth = Math.Max(1, bitmap.PixelWidth);
        _pixelHeight = Math.Max(1, bitmap.PixelHeight);
        InvalidateMeasure();
        InvalidateVisual();
        RaiseZoomChanged();
        if (animate) AnimateAppear();
    }

    private void OnAnimatedFrameChanged() =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_animated is null) return;
            _bitmap = _animated.CurrentFrame;
            InvalidateVisual();
        }));

    private void DetachAnimated()
    {
        if (_animated is null) return;
        _animated.FrameChanged -= OnAnimatedFrameChanged;
        _animated = null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var display = GetDisplaySize();
        return new Size(Math.Max(_viewport.Width, display.Width), Math.Max(_viewport.Height, display.Height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_pages.Count == 0 || _currentIndex < 0)
            return;

        if (_bitmap is null)
        {
            DrawPlaceholder(dc, _failedKey is not null);
            return;
        }

        double scale = _transform.GetEffectiveScale(_pixelWidth, _pixelHeight, _viewport.Width, _viewport.Height);
        double drawWidth = _pixelWidth * scale;
        double drawHeight = _pixelHeight * scale;
        double centerX = RenderSize.Width / 2.0;
        double centerY = RenderSize.Height / 2.0;

        dc.PushTransform(new TranslateTransform(centerX, centerY));
        dc.PushTransform(new RotateTransform(_transform.RotationDegrees));
        dc.PushTransform(new ScaleTransform(
            _transform.FlipHorizontal ? -1 : 1,
            _transform.FlipVertical ? -1 : 1));
        dc.DrawImage(_bitmap, new Rect(-drawWidth / 2.0, -drawHeight / 2.0, drawWidth, drawHeight));
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private void DrawPlaceholder(DrawingContext dc, bool failed)
    {
        Color background = failed ? Color.FromRgb(0x40, 0x24, 0x24) : Color.FromRgb(0x20, 0x20, 0x20);
        var backgroundBrush = new SolidColorBrush(background);
        backgroundBrush.Freeze();
        dc.DrawRectangle(backgroundBrush, null, new Rect(RenderSize));

        var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
        borderPen.Freeze();
        dc.DrawRectangle(null, borderPen, new Rect(RenderSize));

        string text = failed ? $"第 {_currentIndex + 1} 页解码失败" : $"第 {_currentIndex + 1} 页加载中…";
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            16,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(
            Math.Max(0, (RenderSize.Width - formatted.Width) / 2.0),
            Math.Max(0, (RenderSize.Height - formatted.Height) / 2.0)));
    }

    private (double Width, double Height) GetDisplaySize() =>
        _transform.GetDisplaySize(_pixelWidth, _pixelHeight, _viewport.Width, _viewport.Height);

    private void RaiseZoomChanged()
    {
        double percent = Math.Round(ZoomPercent, 2);
        if (Math.Abs(percent - _lastReportedZoom) < 0.01) return;
        _lastReportedZoom = percent;
        ZoomChanged?.Invoke(percent);
    }

    private void AnimateAppear()
    {
        if (!SmoothTransitions || !SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            return;
        }

        BeginAnimation(OpacityProperty, null);
        Opacity = 0.15;
        BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0.15,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static string DecodeKey(ImagePage page) => $"single:{page.Path}";
}
