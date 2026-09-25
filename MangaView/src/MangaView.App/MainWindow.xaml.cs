using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MangaView.Core;

namespace MangaView.App;

public partial class MainWindow : Window
{
    private static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".avif" };

    private readonly DispatcherTimer _fpsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private int _frameCount;
    private DateTime _lastFpsSample = DateTime.UtcNow;
    private bool _darkTheme = true;
    private WindowState _restoreState = WindowState.Normal;
    private CancellationTokenSource? _scanCts;

    public MainWindow()
    {
        InitializeComponent();

        Webtoon.OffsetChangeRequested += OnOffsetRequested;
        Webtoon.CurrentPageChanged += OnCurrentPageChanged;

        _fpsTimer.Tick += OnFpsTimerTick;
        _fpsTimer.Start();
        Loaded += (_, _) =>
        {
            ApplyTheme();
            CompositionTarget.Rendering += OnCompositionRendering;
        };
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            _fpsTimer.Stop();
        };

        // 命令行参数：直接打开文件夹（便于验证脚本/双击测试数据）
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && Directory.Exists(args[1]))
            _ = LoadFolderAsync(args[1]);
    }

    // ---------- FPS 计数（M0 验收指标可视化） ----------

    private void OnCompositionRendering(object? sender, EventArgs e) => _frameCount++;

    private void OnFpsTimerTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double fps = _frameCount / Math.Max(0.001, (now - _lastFpsSample).TotalSeconds);
        _frameCount = 0;
        _lastFpsSample = now;
        FpsText.Text = $"FPS: {fps:0}";
    }

    // ---------- 滚动 / 导航 ----------

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        Webtoon.OnViewScrolled(ScrollHost.VerticalOffset, ScrollHost.ViewportHeight);
        UpdateProgressBar();
    }

    private void OnOffsetRequested(double offset) =>
        Dispatcher.BeginInvoke(
            () => ScrollHost.ScrollToVerticalOffset(offset),
            DispatcherPriority.Background);

    private void UpdateProgressBar()
    {
        double max = Math.Max(1, ScrollHost.ExtentHeight - ScrollHost.ViewportHeight);
        ScrollProgress.Maximum = max;
        ScrollProgress.Value = Math.Min(ScrollHost.VerticalOffset, max);
    }

    private void ScrollBy(double delta) =>
        ScrollHost.ScrollToVerticalOffset(Math.Max(0, ScrollHost.VerticalOffset + delta));

    private void ScrollTo(double offset) =>
        ScrollHost.ScrollToVerticalOffset(Math.Max(0, offset));

    private void JumpPages(int delta) => Webtoon.JumpToPage(Webtoon.CurrentPageIndex + delta);

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        double notches = e.Delta / 120.0;
        double amount = notches * ScrollHost.ViewportHeight * 0.10;
        ScrollHost.ScrollToVerticalOffset(Math.Max(0, ScrollHost.VerticalOffset - amount));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 输入框内正常编辑，不触发全局快捷键
        if (Keyboard.FocusedElement is TextBox) return;

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        double viewport = ScrollHost.ViewportHeight;

        switch (e.Key)
        {
            case Key.O when ctrl:
                OnOpenFolderClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.T when ctrl:
                ToggleTheme();
                e.Handled = true;
                break;
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape when WindowStyle == WindowStyle.None:
                ExitFullscreen();
                e.Handled = true;
                break;
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
        }
    }

    // ---------- 打开 / 拖拽 ----------

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择图片文件夹" };
        if (dlg.ShowDialog(this) == true)
            _ = LoadFolderAsync(dlg.FolderName);
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
        string first = items[0];
        if (Directory.Exists(first))
        {
            _ = LoadFolderAsync(first);
        }
        else if (File.Exists(first) &&
                 ImageExtensions.Contains(Path.GetExtension(first).ToLowerInvariant()))
        {
            _ = LoadFolderAsync(Path.GetDirectoryName(first)!);
        }
    }

    private async Task LoadFolderAsync(string folder)
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        CancellationToken ct = _scanCts.Token;
        ScanText.Text = "扫描中…";

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();
        files.Sort((a, b) => NaturalSortComparer.Instance.Compare(
            Path.GetFileName(a), Path.GetFileName(b)));

        var pages = new List<ImagePage>(files.Count);
        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (w, h) = WpfDecodeWorker.ProbeSize(files[i]);
                    lock (pages)
                    {
                        pages.Add(new ImagePage(i, files[i], Path.GetFileName(files[i]), w, h));
                    }
                    if (i % 20 == 0 || i == files.Count - 1)
                    {
                        int done = i + 1;
                        Dispatcher.Invoke(() => ScanText.Text = $"扫描中 {done}/{files.Count}");
                    }
                }
            }, ct);

            Webtoon.SetPages(pages);
            ScanText.Text = $"共 {pages.Count} 页";
            EmptyHint.Visibility = Visibility.Collapsed;
            await Dispatcher.InvokeAsync(() =>
                ScrollHost.ScrollToVerticalOffset(0), DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ScanText.Text = "";
            MessageBox.Show(this, $"打开文件夹失败：{ex.Message}", "MangaView",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- 页码 / 进度条 / 跳转 ----------

    private void OnCurrentPageChanged(int index, int count)
    {
        PageInfoText.Text = $"页 {index + 1} / {count}";
        _ = ShowPageBubbleAsync(index, count);
    }

    private async Task ShowPageBubbleAsync(int index, int count)
    {
        PageBubble.Text = $"{index + 1} / {count}";
        PageBubbleBorder.Visibility = Visibility.Visible;
        await Task.Delay(900);
        PageBubbleBorder.Visibility = Visibility.Collapsed;
    }

    private void OnProgressClick(object sender, MouseButtonEventArgs e)
    {
        double ratio = e.GetPosition(ScrollProgress).X / Math.Max(1, ScrollProgress.ActualWidth);
        double max = Math.Max(0, ScrollHost.ExtentHeight - ScrollHost.ViewportHeight);
        ScrollHost.ScrollToVerticalOffset(ratio * max);
    }

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
        if (int.TryParse(JumpBox.Text.Trim(), out int p) && p >= 1)
            Webtoon.JumpToPage(p - 1);
        JumpBox.Text = "";
        Keyboard.ClearFocus();
    }

    // ---------- 视图 / 主题 / 全屏 ----------

    private void OnGapClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string s } && double.TryParse(s, out double g))
            Webtoon.Gap = g;
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

        Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        Background = B(bg);
        Foreground = B(fg);
        TopMenu.Background = B(panel);
        TopMenu.Foreground = B(fg);
        BottomBar.Background = B(panel);
        ScrollHost.Background = B(viewer);
        PageInfoText.Foreground = B(fg);
        ScanText.Foreground = B(dim);
        FpsText.Foreground = B(dim);
        EmptyHint.Foreground = B(dim);
        JumpBox.Background = B(input);
        JumpBox.Foreground = B(fg);
        JumpBox.BorderBrush = B(border);
        JumpButton.Background = B(input);
        JumpButton.Foreground = B(fg);
        JumpButton.BorderBrush = B(border);
        ThemeButton.Background = B(input);
        ThemeButton.Foreground = B(fg);
        ThemeButton.BorderBrush = B(border);
        ScrollProgress.Background = B(input);
        ScrollProgress.Foreground = B(Color.FromRgb(0x4F, 0x8C, 0xC9));
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            ExitFullscreen();
        }
        else
        {
            _restoreState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Topmost = true;
        }
    }

    private void ExitFullscreen()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowState = _restoreState;
        Topmost = false;
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();
}

