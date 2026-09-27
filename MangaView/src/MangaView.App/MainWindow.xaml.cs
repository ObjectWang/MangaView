using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MangaView.Core;

namespace MangaView.App;

public partial class MainWindow : Window
{
    private const int PlaceholderWidth = 800;
    private const int PlaceholderHeight = 1200;

    private readonly DispatcherTimer _fpsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<ImagePage> _pages = new();
    private int _frameCount;
    private DateTime _lastFpsSample = DateTime.UtcNow;
    private bool _darkTheme = true;
    private bool _recursive;
    private int _currentPageIndex = -1;
    private int _bubbleVersion;
    private ReadingMode _mode = ReadingMode.SinglePage;
    private WindowState _restoreState = WindowState.Normal;
    private string? _currentFolder;
    private string? _currentFile;

    private bool _isPanning;
    private Point _panStart;
    private double _panStartHorizontal;
    private double _panStartVertical;
    private bool _temporaryZoom;
    private ZoomMode _temporaryZoomMode;
    private double _temporaryZoomScale;

    public MainWindow()
    {
        InitializeComponent();

        SinglePage.CurrentPageChanged += OnSinglePageChanged;
        SinglePage.ZoomChanged += OnZoomChanged;
        Webtoon.OffsetChangeRequested += OnOffsetRequested;
        Webtoon.CurrentPageChanged += OnWebtoonPageChanged;
        DoublePage.CurrentPageChanged += OnDoublePageChanged;
        InitializeM2Session();

        _fpsTimer.Tick += OnFpsTimerTick;
        _fpsTimer.Start();
        Loaded += (_, _) =>
        {
            ApplyTheme();
            UpdateViewport();
            CompositionTarget.Rendering += OnCompositionRendering;
        };
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            _fpsTimer.Stop();
            ShutdownM2Session();
        };

        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && (File.Exists(args[1]) || Directory.Exists(args[1])))
            _ = LoadPathAsync(args[1]);
    }

    private void OnCompositionRendering(object? sender, EventArgs e) => _frameCount++;

    private void OnFpsTimerTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double fps = _frameCount / Math.Max(0.001, (now - _lastFpsSample).TotalSeconds);
        _frameCount = 0;
        _lastFpsSample = now;
        FpsText.Text = $"FPS: {fps:0}";
    }

    // ---------- 打开 / 拖拽 / 播放列表 ----------

    private void OnOpenImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开图片",
            Filter = "支持的图片|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp;*.tif;*.tiff;*.avif|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            _ = LoadPathAsync(dialog.FileName);
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择图片文件夹" };
        if (dialog.ShowDialog(this) == true)
            _ = LoadFolderAsync(dialog.FolderName);
    }

    private void OnToggleRecursiveClick(object sender, RoutedEventArgs e)
    {
        _recursive = RecursiveMenuItem.IsChecked;
        if (_currentFolder is not null)
        {
            string? currentPath = _currentPageIndex >= 0 && _currentPageIndex < _pages.Count
                ? _pages[_currentPageIndex].Path
                : _currentFile;
            _ = LoadFolderAsync(_currentFolder, currentPath);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] items || items.Length == 0) return;
        _ = LoadPathAsync(items[0]);
    }

    private async Task LoadPathAsync(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                await LoadFolderAsync(path);
                return;
            }

            if (!File.Exists(path))
            {
                MessageBox.Show(this, $"路径不存在：{path}", "MangaView", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (CbzArchive.IsSupported(path))
            {
                await LoadArchiveAsync(path);
                return;
            }

            if (!ImageCatalog.IsSupportedImage(path))
            {
                MessageBox.Show(this, "暂不支持该文件格式。", "MangaView", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string folder = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("无法确定图片所在文件夹。");
            await LoadFolderAsync(folder, path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开失败：{ex.Message}", "MangaView", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyPlaylist(int index)
    {
        UpdateViewport();
        UpdateCanvasVisibility();
        if (_mode == ReadingMode.SinglePage)
        {
            SinglePage.SetPages(_pages, index);
        }
        else if (_mode == ReadingMode.Webtoon)
        {
            Webtoon.SetPages(_pages);
            JumpWebtoonToPage(index);
        }
        else
        {
            DoublePage.Configure(_direction, _doubleCoverPage, _doubleGap);
            DoublePage.SetPages(_pages);
            DoublePage.GoToPage(index);
        }
        UpdateModeUi();
        UpdateProgressBar();
    }

    // ---------- 浏览模式 / 导航 ----------

    private void OnSingleModeClick(object sender, RoutedEventArgs e) => SetMode(ReadingMode.SinglePage);

    private void OnWebtoonModeClick(object sender, RoutedEventArgs e) => SetMode(ReadingMode.Webtoon);

    private void OnDoubleModeClick(object sender, RoutedEventArgs e) => SetMode(ReadingMode.DoublePage);

    private void SetMode(ReadingMode mode)
    {
        if (_mode == mode)
        {
            UpdateModeUi();
            return;
        }

        _mode = mode;
        UpdateCanvasVisibility();
        int page = Math.Clamp(_currentPageIndex, 0, Math.Max(0, _pages.Count - 1));

        if (mode == ReadingMode.SinglePage)
        {
            SinglePage.SetViewport(ScrollHost.ViewportWidth, ScrollHost.ViewportHeight);
            SinglePage.SetPages(_pages, page);
            Dispatcher.BeginInvoke(CenterSingleView, DispatcherPriority.Loaded);
        }
        else if (mode == ReadingMode.Webtoon)
        {
            Webtoon.SetPages(_pages);
            JumpWebtoonToPage(page);
        }
        else
        {
            DoublePage.Configure(_direction, _doubleCoverPage, _doubleGap);
            DoublePage.SetPages(_pages);
            DoublePage.GoToPage(page);
        }

        UpdateModeUi();
        UpdateProgressBar();
        ScheduleProgressSave();
    }

    private void UpdateCanvasVisibility()
    {
        SinglePage.Visibility = _mode == ReadingMode.SinglePage ? Visibility.Visible : Visibility.Collapsed;
        Webtoon.Visibility = _mode == ReadingMode.Webtoon ? Visibility.Visible : Visibility.Collapsed;
        DoublePage.Visibility = _mode == ReadingMode.DoublePage ? Visibility.Visible : Visibility.Collapsed;
        ScrollHost.HorizontalScrollBarVisibility = _mode == ReadingMode.SinglePage
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;
        ScrollHost.VerticalScrollBarVisibility = _mode == ReadingMode.DoublePage
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
    }

    private void UpdateModeUi()
    {
        SingleModeMenuItem.IsChecked = _mode == ReadingMode.SinglePage;
        WebtoonModeMenuItem.IsChecked = _mode == ReadingMode.Webtoon;
        DoubleModeMenuItem.IsChecked = _mode == ReadingMode.DoublePage;
        ModeText.Text = _mode switch
        {
            ReadingMode.SinglePage => "单页",
            ReadingMode.Webtoon => "Webtoon",
            _ => _direction == ReadingDirection.LeftToRight ? "双页 L→R" : "双页 R→L",
        };
        ZoomText.Text = _mode switch
        {
            ReadingMode.SinglePage => $"{SinglePage.ZoomPercent:0.##}%",
            ReadingMode.Webtoon => "适应宽度",
            _ => "适应窗口",
        };
        PreviousButton.IsEnabled = NextButton.IsEnabled = _pages.Count > 1;
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        if (_mode == ReadingMode.DoublePage) MoveDoubleSpread(-1);
        else GoToPage(_currentPageIndex - 1);
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_mode == ReadingMode.DoublePage) MoveDoubleSpread(1);
        else GoToPage(_currentPageIndex + 1);
    }

    private void GoToPage(int index)
    {
        if (_pages.Count == 0) return;
        int next = Math.Clamp(index, 0, _pages.Count - 1);
        if (_mode == ReadingMode.SinglePage)
        {
            SinglePage.SetPageIndex(next);
            Dispatcher.BeginInvoke(CenterSingleView, DispatcherPriority.Loaded);
        }
        else if (_mode == ReadingMode.Webtoon)
        {
            JumpWebtoonToPage(next);
        }
        else
        {
            DoublePage.GoToPage(next);
        }
    }

    private void ScrollBy(double delta) =>
        ScrollHost.ScrollToVerticalOffset(Math.Max(0, ScrollHost.VerticalOffset + delta));

    private void JumpPages(int delta) => GoToPage(_currentPageIndex + delta);

    private void OnSinglePageChanged(int index, int count) => UpdatePageState(index, count);

    private void OnWebtoonPageChanged(int index, int count) => UpdatePageState(index, count);

    private void OnDoublePageChanged(int index, int count) => UpdatePageState(index, count);

    private void UpdatePageState(int index, int count)
    {
        if (count == 0) return;
        _currentPageIndex = index;
        PageInfoText.Text = $"页 {index + 1} / {count}";
        UpdateProgressBar();
        ScheduleProgressSave();
        _ = ShowPageBubbleAsync(index, count);
    }

    private async Task ShowPageBubbleAsync(int index, int count)
    {
        int version = ++_bubbleVersion;
        PageBubble.Text = $"{index + 1} / {count}";
        PageBubbleBorder.Visibility = Visibility.Visible;
        await Task.Delay(900);
        if (version == _bubbleVersion)
            PageBubbleBorder.Visibility = Visibility.Collapsed;
    }

    // ---------- 滚轮 / 缩放 / 平移 ----------

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateViewport();
        if (_mode == ReadingMode.Webtoon)
        {
            Webtoon.OnViewScrolled(ScrollHost.VerticalOffset, ScrollHost.ViewportHeight);
            _lastWebtoonAnchor = Webtoon.CurrentAnchor;
            ScheduleProgressSave();
        }
        UpdateProgressBar();
    }

    private void UpdateViewport()
    {
        SinglePage.SetViewport(ScrollHost.ViewportWidth, ScrollHost.ViewportHeight);
        Webtoon.SetViewport(ScrollHost.ViewportWidth, ScrollHost.ViewportHeight);
        DoublePage.SetViewport(ScrollHost.ViewportWidth, ScrollHost.ViewportHeight);
    }

    private void OnOffsetRequested(double offset) =>
        Dispatcher.BeginInvoke(
            () => ScrollHost.ScrollToVerticalOffset(offset),
            DispatcherPriority.Background);

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (_mode == ReadingMode.SinglePage && ctrl)
        {
            double factor = Math.Pow(1.2, e.Delta / 120.0);
            ZoomAt(factor, e.GetPosition(ScrollHost));
            e.Handled = true;
            return;
        }

        double notches = e.Delta / 120.0;
        if (_mode == ReadingMode.SinglePage)
        {
            bool canScroll = SinglePage.DisplayHeight > ScrollHost.ViewportHeight + 1;
            bool scrollingUp = e.Delta > 0;
            bool atTop = ScrollHost.VerticalOffset <= 0.5;
            bool atBottom = ScrollHost.VerticalOffset >= ScrollHost.ScrollableHeight - 0.5;
            if (canScroll && !((scrollingUp && atTop) || (!scrollingUp && atBottom)))
            {
                ScrollBy(-notches * ScrollHost.ViewportHeight * 0.12);
            }
            else
            {
                GoToPage(_currentPageIndex + (scrollingUp ? -1 : 1));
            }
        }
        else if (_mode == ReadingMode.Webtoon)
        {
            _webtoonAnchorDirty = true;
            ScrollHost.ScrollToVerticalOffset(Math.Max(0,
                ScrollHost.VerticalOffset - notches * ScrollHost.ViewportHeight * 0.10));
        }
        else
        {
            MoveDoubleSpread(e.Delta > 0 ? -1 : 1);
        }
        e.Handled = true;
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        EnsureSingleMode();
        ZoomAt(1.25, new Point(ScrollHost.ViewportWidth / 2, ScrollHost.ViewportHeight / 2));
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        EnsureSingleMode();
        ZoomAt(1 / 1.25, new Point(ScrollHost.ViewportWidth / 2, ScrollHost.ViewportHeight / 2));
    }

    private void EnsureSingleMode()
    {
        if (_mode != ReadingMode.SinglePage) SetMode(ReadingMode.SinglePage);
    }

    private void ZoomAt(double factor, Point anchor)
    {
        if (_pages.Count == 0) return;
        double oldWidth = SinglePage.DisplayWidth;
        double oldHeight = SinglePage.DisplayHeight;
        double ratioX = oldWidth > ScrollHost.ViewportWidth + 0.5
            ? Math.Clamp((ScrollHost.HorizontalOffset + anchor.X) / oldWidth, 0, 1)
            : 0.5;
        double ratioY = oldHeight > ScrollHost.ViewportHeight + 0.5
            ? Math.Clamp((ScrollHost.VerticalOffset + anchor.Y) / oldHeight, 0, 1)
            : 0.5;

        SinglePage.ZoomBy(factor);
        ScrollHost.UpdateLayout();

        double newWidth = SinglePage.DisplayWidth;
        double newHeight = SinglePage.DisplayHeight;
        double horizontal = newWidth > ScrollHost.ViewportWidth + 0.5 ? ratioX * newWidth - anchor.X : 0;
        double vertical = newHeight > ScrollHost.ViewportHeight + 0.5 ? ratioY * newHeight - anchor.Y : 0;
        ScrollHost.ScrollToHorizontalOffset(Math.Max(0, horizontal));
        ScrollHost.ScrollToVerticalOffset(Math.Max(0, vertical));
    }

    private void OnFitWindowClick(object sender, RoutedEventArgs e) => SetSingleZoom(ZoomMode.FitWindow);

    private void OnFitWidthClick(object sender, RoutedEventArgs e) => SetSingleZoom(ZoomMode.FitWidth);

    private void OnFitHeightClick(object sender, RoutedEventArgs e) => SetSingleZoom(ZoomMode.FitHeight);

    private void OnOriginalSizeClick(object sender, RoutedEventArgs e) => SetSingleZoom(ZoomMode.OriginalSize);

    private void SetSingleZoom(ZoomMode mode)
    {
        EnsureSingleMode();
        SinglePage.SetZoomMode(mode);
        ScrollHost.UpdateLayout();
        CenterSingleView();
    }

    private void OnZoomChanged(double percent)
    {
        if (_mode == ReadingMode.SinglePage)
            ZoomText.Text = $"{percent:0.##}%";
    }

    private void CenterSingleView()
    {
        if (_mode != ReadingMode.SinglePage) return;
        ScrollHost.UpdateLayout();
        ScrollHost.ScrollToHorizontalOffset(Math.Max(0, (ScrollHost.ExtentWidth - ScrollHost.ViewportWidth) / 2));
        ScrollHost.ScrollToVerticalOffset(Math.Max(0, (ScrollHost.ExtentHeight - ScrollHost.ViewportHeight) / 2));
    }

    private void OnViewerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode != ReadingMode.SinglePage || e.ChangedButton != MouseButton.Left ||
            IsInsideScrollBar(e.OriginalSource as DependencyObject))
            return;

        _isPanning = true;
        _panStart = e.GetPosition(ScrollHost);
        _panStartHorizontal = ScrollHost.HorizontalOffset;
        _panStartVertical = ScrollHost.VerticalOffset;
        ScrollHost.CaptureMouse();
        Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void OnViewerMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        Point current = e.GetPosition(ScrollHost);
        ScrollHost.ScrollToHorizontalOffset(Math.Max(0, _panStartHorizontal - (current.X - _panStart.X)));
        ScrollHost.ScrollToVerticalOffset(Math.Max(0, _panStartVertical - (current.Y - _panStart.Y)));
        e.Handled = true;
    }

    private void OnViewerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning || e.ChangedButton != MouseButton.Left) return;
        EndPan();
        e.Handled = true;
    }

    private void OnViewerLostMouseCapture(object sender, MouseEventArgs e) => EndPan();

    private void EndPan()
    {
        _isPanning = false;
        Cursor = Cursors.Arrow;
        if (ScrollHost.IsMouseCaptured) ScrollHost.ReleaseMouseCapture();
    }

    private static bool IsInsideScrollBar(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ScrollBar) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    // ---------- 旋转 / 翻转 ----------

    private void OnRotateRightClick(object sender, RoutedEventArgs e) => RotateSingle(true);

    private void OnRotateLeftClick(object sender, RoutedEventArgs e) => RotateSingle(false);

    private void RotateSingle(bool right)
    {
        EnsureSingleMode();
        if (right) SinglePage.RotateRight();
        else SinglePage.RotateLeft();
        ScrollHost.UpdateLayout();
        CenterSingleView();
    }

    private void OnFlipHorizontalClick(object sender, RoutedEventArgs e)
    {
        EnsureSingleMode();
        SinglePage.ToggleHorizontalFlip();
    }

    private void OnFlipVerticalClick(object sender, RoutedEventArgs e)
    {
        EnsureSingleMode();
        SinglePage.ToggleVerticalFlip();
    }

    private void OnResetTransformClick(object sender, RoutedEventArgs e)
    {
        EnsureSingleMode();
        SinglePage.ResetTransform();
        ScrollHost.UpdateLayout();
        CenterSingleView();
    }

    private void OnToggleSmoothClick(object sender, RoutedEventArgs e) =>
        SinglePage.SmoothTransitions = SmoothTransitionMenuItem.IsChecked;

    // ---------- 键盘 ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        if (ctrl && e.Key == Key.O)
        {
            if (alt) OnOpenArchiveClick(this, new RoutedEventArgs());
            else if (shift) OnOpenFolderClick(this, new RoutedEventArgs());
            else OnOpenImageClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.T when ctrl:
                ToggleTheme();
                e.Handled = true;
                return;
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                return;
            case Key.Escape when WindowStyle == WindowStyle.None:
                ExitFullscreen();
                e.Handled = true;
                return;
        }

        if (_mode == ReadingMode.SinglePage) HandleSinglePageKey(e, ctrl, shift);
        else if (_mode == ReadingMode.Webtoon) HandleWebtoonKey(e, ctrl, shift);
        else HandleDoublePageKey(e, ctrl, shift);
    }

    private void HandleSinglePageKey(KeyEventArgs e, bool ctrl, bool shift)
    {
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Add:
                case Key.OemPlus:
                    OnZoomInClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.Subtract:
                case Key.OemMinus:
                    OnZoomOutClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.D0:
                case Key.NumPad0:
                    OnOriginalSizeClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.D1:
                case Key.NumPad1:
                    OnFitWindowClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.D2:
                case Key.NumPad2:
                    OnFitWidthClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.D3:
                case Key.NumPad3:
                    OnFitHeightClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.R:
                    OnResetTransformClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:
                GoToPage(_currentPageIndex - 1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.PageDown:
                GoToPage(_currentPageIndex + 1);
                e.Handled = true;
                break;
            case Key.Space:
                GoToPage(_currentPageIndex + (shift ? -1 : 1));
                e.Handled = true;
                break;
            case Key.Home:
                GoToPage(0);
                e.Handled = true;
                break;
            case Key.End:
                GoToPage(_pages.Count - 1);
                e.Handled = true;
                break;
            case Key.Up:
                ScrollBy(-80);
                e.Handled = true;
                break;
            case Key.Down:
                ScrollBy(80);
                e.Handled = true;
                break;
            case Key.R:
                RotateSingle(!shift);
                e.Handled = true;
                break;
            case Key.H:
                OnFlipHorizontalClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.V:
                OnFlipVerticalClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F:
                OnFitWindowClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.W:
                OnFitWidthClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.OemPlus:
            case Key.Add:
                OnZoomInClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                OnZoomOutClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Z when !ctrl:
                BeginTemporaryZoom();
                e.Handled = true;
                break;
        }
    }

    private void HandleWebtoonKey(KeyEventArgs e, bool ctrl, bool shift)
    {
        _webtoonAnchorDirty = true;
        double viewport = ScrollHost.ViewportHeight;
        switch (e.Key)
        {
            case Key.PageDown when ctrl:
                JumpPages(10);
                e.Handled = true;
                break;
            case Key.PageUp when ctrl:
                JumpPages(-10);
                e.Handled = true;
                break;
            case Key.PageDown:
                ScrollBy(viewport * 0.9);
                e.Handled = true;
                break;
            case Key.PageUp:
                ScrollBy(-viewport * 0.9);
                e.Handled = true;
                break;
            case Key.Space:
                ScrollBy((shift ? -1 : 1) * viewport * 0.85);
                e.Handled = true;
                break;
            case Key.Home:
                ScrollTo(0);
                e.Handled = true;
                break;
            case Key.End:
                ScrollTo(Math.Max(0, ScrollHost.ExtentHeight - ScrollHost.ViewportHeight));
                e.Handled = true;
                break;
            case Key.Left:
                GoToPage(_currentPageIndex - 1);
                e.Handled = true;
                break;
            case Key.Right:
                GoToPage(_currentPageIndex + 1);
                e.Handled = true;
                break;
        }
    }

    private void BeginTemporaryZoom()
    {
        if (_temporaryZoom || _pages.Count == 0) return;
        _temporaryZoom = true;
        _temporaryZoomMode = SinglePage.Transform.Mode;
        _temporaryZoomScale = SinglePage.Transform.Scale;
        SinglePage.SetZoomMode(ZoomMode.OriginalSize);
        ScrollHost.UpdateLayout();
        CenterSingleView();
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Z || !_temporaryZoom) return;
        _temporaryZoom = false;
        SinglePage.RestoreZoom(_temporaryZoomMode, _temporaryZoomScale);
        ScrollHost.UpdateLayout();
        CenterSingleView();
        e.Handled = true;
    }

    private void ScrollTo(double offset) => ScrollHost.ScrollToVerticalOffset(Math.Max(0, offset));

    // ---------- 跳页 / 进度条 ----------

    private void OnJumpClick(object sender, RoutedEventArgs e) => DoJump();

    private void OnJumpBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DoJump();
            e.Handled = true;
        }
    }

    private void DoJump()
    {
        if (int.TryParse(JumpBox.Text.Trim(), out int page) && page >= 1)
        {
            if (_mode == ReadingMode.Webtoon) _webtoonAnchorDirty = true;
            GoToPage(page - 1);
        }
        JumpBox.Text = "";
        Keyboard.ClearFocus();
    }

    private void OnProgressClick(object sender, MouseButtonEventArgs e)
    {
        double ratio = Math.Clamp(e.GetPosition(ScrollProgress).X / Math.Max(1, ScrollProgress.ActualWidth), 0, 1);
        if (_mode is ReadingMode.SinglePage or ReadingMode.DoublePage)
        {
            if (_pages.Count > 0) GoToPage((int)Math.Round(ratio * (_pages.Count - 1)));
            return;
        }

        double max = Math.Max(0, ScrollHost.ExtentHeight - ScrollHost.ViewportHeight);
        _webtoonAnchorDirty = true;
        ScrollHost.ScrollToVerticalOffset(ratio * max);
    }

    private void UpdateProgressBar()
    {
        if (_mode is ReadingMode.SinglePage or ReadingMode.DoublePage)
        {
            ScrollProgress.Maximum = Math.Max(1, _pages.Count - 1);
            ScrollProgress.Value = Math.Max(0, _currentPageIndex);
            return;
        }

        double max = Math.Max(1, ScrollHost.ExtentHeight - ScrollHost.ViewportHeight);
        ScrollProgress.Maximum = max;
        ScrollProgress.Value = Math.Min(ScrollHost.VerticalOffset, max);
    }

    // ---------- 主题 / 全屏 ----------

    private void OnGapClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } && double.TryParse(value, out double gap))
            Webtoon.Gap = gap;
    }

    private void OnToggleThemeClick(object sender, RoutedEventArgs e) => ToggleTheme();

    private void ToggleTheme()
    {
        _darkTheme = !_darkTheme;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        Color bg, panel, viewer, fg, dim, input, border;
        if (_darkTheme)
        {
            bg = Color.FromRgb(0x14, 0x14, 0x14); panel = Color.FromRgb(0x1E, 0x1E, 0x1E);
            viewer = Color.FromRgb(0x0E, 0x0E, 0x0E); fg = Color.FromRgb(0xE6, 0xE6, 0xE6);
            dim = Color.FromRgb(0x9A, 0xA0, 0xA6); input = Color.FromRgb(0x2A, 0x2A, 0x2A);
            border = Color.FromRgb(0x3A, 0x3A, 0x3A);
        }
        else
        {
            bg = Color.FromRgb(0xF5, 0xF5, 0xF5); panel = Color.FromRgb(0xEC, 0xEC, 0xEC);
            viewer = Color.FromRgb(0xE8, 0xE8, 0xE8); fg = Color.FromRgb(0x20, 0x20, 0x20);
            dim = Color.FromRgb(0x5F, 0x63, 0x68); input = Color.FromRgb(0xFF, 0xFF, 0xFF);
            border = Color.FromRgb(0xC9, 0xC9, 0xC9);
        }

        Brush Make(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

        Background = Make(bg);
        Foreground = Make(fg);
        TopMenu.Background = Make(panel);
        TopMenu.Foreground = Make(fg);
        BottomBar.Background = Make(panel);
        ScrollHost.Background = Make(viewer);
        foreach (var text in new[] { PageInfoText, ScanText, FpsText, EmptyHint, ZoomText, ModeText })
            text.Foreground = Make(dim);
        foreach (var button in new[] { JumpButton, ThemeButton, PreviousButton, NextButton })
        {
            button.Background = Make(input);
            button.Foreground = Make(fg);
            button.BorderBrush = Make(border);
        }
        JumpBox.Background = Make(input);
        JumpBox.Foreground = Make(fg);
        JumpBox.BorderBrush = Make(border);
        ScrollProgress.Background = Make(input);
        ScrollProgress.Foreground = Make(Color.FromRgb(0x4F, 0x8C, 0xC9));
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            ExitFullscreen();
            return;
        }

        _restoreState = WindowState;
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        Topmost = true;
        TopMenu.Visibility = Visibility.Collapsed;
        BottomBar.Visibility = Visibility.Collapsed;
    }

    private void ExitFullscreen()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowState = _restoreState;
        Topmost = false;
        TopMenu.Visibility = Visibility.Visible;
        BottomBar.Visibility = Visibility.Visible;
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();
}
