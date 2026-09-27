using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MangaView.Core;

namespace MangaView.App;

public partial class MainWindow
{
    private readonly AppSettingsStore _settingsStore = new(GetM4Path("settings.json"));
    private readonly BookmarkStore _bookmarkStore = new(GetM4Path("bookmarks.json"));
    private readonly RecentStore _recentStore = new(GetM4Path("recent.json"));
    private readonly DispatcherTimer _slideshowTimer = new();
    private readonly DispatcherTimer _slideshowHideTimer = new() { Interval = TimeSpan.FromSeconds(2.2) };
    private readonly HashSet<int> _slideshowVisited = new();
    private AppSettings _settings = AppSettings.Default;
    private ThemePreference _themePreference = ThemePreference.System;
    private bool _slideshowActive;
    private bool _slideshowPaused;
    private bool _m4Loaded;
    private bool _infoPanelVisible;
    private bool _thumbnailVisible;
    private bool _controlsHidden;

    private static string GetM4Path(string fileName) => Path.Combine(GetLocalDataRoot(), fileName);

    private void InitializeM4Session()
    {
        _settingsStore.Load();
        _settings = _settingsStore.Settings;
        _themePreference = _settings.Theme;
        _darkTheme = ResolveDarkTheme(_themePreference);
        _infoPanelVisible = _settings.ShowInfoPanel;
        _thumbnailVisible = _settings.ShowThumbnails;

        _slideshowTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.SlideshowIntervalSeconds, 1, 30));
        _slideshowTimer.Tick += OnSlideshowTick;
        _slideshowHideTimer.Tick += (_, _) =>
        {
            _slideshowHideTimer.Stop();
            if (_slideshowActive && _settings.SlideshowHideControls && !_slideshowPaused)
                SetM4ChromeVisible(false);
        };

        Loaded += OnM4Loaded;
        Closing += OnM4Closing;
        PreviewMouseMove += OnM4MouseMove;
    }

    private void ShutdownM4Session()
    {
        _slideshowTimer.Stop();
        _slideshowHideTimer.Stop();
        SaveM4Settings();
    }

    private void OnM4Loaded(object sender, RoutedEventArgs e)
    {
        _m4Loaded = true;
        ApplySavedWindowPlacement();
        RefreshRecentMenu();
        RefreshBookmarksMenu();
        RefreshSlideshowMenuChecks();
        RefreshThemeMenuChecks();
        RememberProgressMenuItem.IsChecked = _settings.RememberProgress;
        RefreshReadingPreferenceMenuChecks();
        RefreshPanelVisibility();
        UpdateHdrStatus();
        QueueMetadataRefresh();
        UpdateThumbnailPages();
    }

    private void OnM4Closing(object? sender, System.ComponentModel.CancelEventArgs e) => SaveM4Settings();

    private void OnM4MouseMove(object sender, MouseEventArgs e)
    {
        if (!_slideshowActive || !_settings.SlideshowHideControls) return;
        SetM4ChromeVisible(true);
        _slideshowHideTimer.Stop();
        if (!_slideshowPaused) _slideshowHideTimer.Start();
    }

    private void SaveM4Settings()
    {
        if (!_m4Loaded) return;
        _settings = _settings with
        {
            DarkTheme = _darkTheme,
            Theme = _themePreference,
            SaveRecent = SaveRecentMenuItem.IsChecked,
            RememberProgress = _settings.RememberProgress,
            ShowInfoPanel = _infoPanelVisible,
            ShowThumbnails = _thumbnailVisible,
            SlideshowIntervalSeconds = (int)Math.Clamp(_slideshowTimer.Interval.TotalSeconds, 1, 30),
            SlideshowRandom = SlideshowRandomMenuItem.IsChecked,
            SlideshowLoop = SlideshowLoopMenuItem.IsChecked,
            SlideshowHideControls = SlideshowHideControlsMenuItem.IsChecked,
            WindowPlacement = CaptureWindowPlacement(),
        };
        _settingsStore.Update(_settings);
        _settingsStore.Save();
    }

    // ---------- Theme ----------

    private void OnThemeSystemClick(object sender, RoutedEventArgs e) => SetThemePreference(ThemePreference.System);

    private void OnThemeLightClick(object sender, RoutedEventArgs e) => SetThemePreference(ThemePreference.Light);

    private void OnThemeDarkClick(object sender, RoutedEventArgs e) => SetThemePreference(ThemePreference.Dark);

    private void SetThemePreference(ThemePreference preference)
    {
        _themePreference = preference;
        _darkTheme = ResolveDarkTheme(preference);
        RefreshThemeMenuChecks();
        ApplyTheme();
        SaveM4Settings();
    }

    private static bool ResolveDarkTheme(ThemePreference preference) => preference switch
    {
        ThemePreference.Dark => true,
        ThemePreference.Light => false,
        _ => IsSystemDarkTheme(),
    };

    private static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return true;
        }
    }

    private void RefreshThemeMenuChecks()
    {
        ThemeSystemMenuItem.IsChecked = _themePreference == ThemePreference.System;
        ThemeLightMenuItem.IsChecked = _themePreference == ThemePreference.Light;
        ThemeDarkMenuItem.IsChecked = _themePreference == ThemePreference.Dark;
    }

    // ---------- Recent ----------

    private void RecordCurrentSourceRecent()
    {
        if (!_settings.SaveRecent || _sourceKey is null) return;
        if (_currentArchive is not null)
            _recentStore.Add(_currentArchive, RecentKind.Archive, Path.GetFileName(_currentArchive));
        else if (_currentFolder is not null)
        {
            RecentKind kind = _currentFile is not null && File.Exists(_currentFile) ? RecentKind.Image : RecentKind.Folder;
            _recentStore.Add(kind == RecentKind.Image ? _currentFile! : _currentFolder,
                kind, kind == RecentKind.Image ? Path.GetFileName(_currentFile!) : Path.GetFileName(Path.TrimEndingDirectorySeparator(_currentFolder)));
        }
        RefreshRecentMenu();
    }

    private void RefreshRecentMenu()
    {
        if (!_m4Loaded) return;
        RecentItemsMenuItem.Items.Clear();
        var entries = _settings.SaveRecent ? _recentStore.Entries : Array.Empty<RecentEntry>();
        if (entries.Count == 0)
        {
            RecentItemsMenuItem.Items.Add(new MenuItem { Header = "(无)", IsEnabled = false });
            return;
        }

        foreach (var entry in entries)
        {
            var item = new MenuItem
            {
                Header = entry.DisplayName,
                ToolTip = entry.Path,
                Tag = entry.Path,
            };
            item.Click += OnRecentItemClick;
            RecentItemsMenuItem.Items.Add(item);
        }
    }

    private void OnRecentItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path })
            _ = LoadPathAsync(path);
    }

    private void OnClearRecentClick(object sender, RoutedEventArgs e)
    {
        _recentStore.Clear();
        RefreshRecentMenu();
    }

    private void OnToggleSaveRecentClick(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { SaveRecent = SaveRecentMenuItem.IsChecked };
        RefreshRecentMenu();
        SaveM4Settings();
    }

    private void OnDefaultSingleModeClick(object sender, RoutedEventArgs e) => SetDefaultMode(ReadingMode.SinglePage);

    private void OnDefaultWebtoonModeClick(object sender, RoutedEventArgs e) => SetDefaultMode(ReadingMode.Webtoon);

    private void OnDefaultDoubleModeClick(object sender, RoutedEventArgs e) => SetDefaultMode(ReadingMode.DoublePage);

    private void SetDefaultMode(ReadingMode mode)
    {
        _settings = _settings with { DefaultMode = mode };
        RefreshReadingPreferenceMenuChecks();
        SaveM4Settings();
    }

    private void OnDefaultLeftToRightClick(object sender, RoutedEventArgs e) =>
        SetDefaultDirection(ReadingDirection.LeftToRight);

    private void OnDefaultRightToLeftClick(object sender, RoutedEventArgs e) =>
        SetDefaultDirection(ReadingDirection.RightToLeft);

    private void SetDefaultDirection(ReadingDirection direction)
    {
        _settings = _settings with { DefaultDirection = direction };
        RefreshReadingPreferenceMenuChecks();
        SaveM4Settings();
    }

    private void OnDefaultCoverClick(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { DefaultDoubleCoverPage = DefaultCoverMenuItem.IsChecked };
        RefreshReadingPreferenceMenuChecks();
        SaveM4Settings();
    }

    private void RefreshReadingPreferenceMenuChecks()
    {
        DefaultSingleModeMenuItem.IsChecked = _settings.DefaultMode == ReadingMode.SinglePage;
        DefaultWebtoonModeMenuItem.IsChecked = _settings.DefaultMode == ReadingMode.Webtoon;
        DefaultDoubleModeMenuItem.IsChecked = _settings.DefaultMode == ReadingMode.DoublePage;
        DefaultLeftToRightMenuItem.IsChecked = _settings.DefaultDirection == ReadingDirection.LeftToRight;
        DefaultRightToLeftMenuItem.IsChecked = _settings.DefaultDirection == ReadingDirection.RightToLeft;
        DefaultCoverMenuItem.IsChecked = _settings.DefaultDoubleCoverPage;
    }

    // ---------- Bookmarks ----------

    private void OnAddBookmarkClick(object sender, RoutedEventArgs e)
    {
        if (_sourceKey is null || _pages.Count == 0) return;
        int page = Math.Clamp(_currentPageIndex, 0, _pages.Count - 1);
        string? name = TextPromptDialog.Show(this, "添加书签", "书签名称：", $"第 {page + 1} 页");
        if (string.IsNullOrWhiteSpace(name)) return;
        ScrollAnchor anchor = _mode == ReadingMode.Webtoon ? Webtoon.CurrentAnchor : new ScrollAnchor(page, 0);
        _bookmarkStore.Add(_sourceKey, name, page, _mode, anchor);
        RefreshBookmarksMenu();
    }

    private void OnManageBookmarksClick(object sender, RoutedEventArgs e)
    {
        if (_sourceKey is null) return;
        var dialog = new BookmarkManagerWindow(_bookmarkStore, _sourceKey) { Owner = this };
        dialog.ShowDialog();
        RefreshBookmarksMenu();
    }

    private void RefreshBookmarksMenu()
    {
        if (!_m4Loaded) return;
        BookmarksJumpMenuItem.Items.Clear();
        if (_sourceKey is null) return;
        var bookmarks = _bookmarkStore.Get(_sourceKey);
        if (bookmarks.Count == 0)
        {
            BookmarksJumpMenuItem.Items.Add(new MenuItem { Header = "(无)", IsEnabled = false });
            return;
        }

        foreach (var bookmark in bookmarks)
        {
            var item = new MenuItem
            {
                Header = $"{bookmark.Name}  ·  第 {bookmark.PageIndex + 1} 页",
                Tag = bookmark,
            };
            item.Click += OnBookmarkItemClick;
            BookmarksJumpMenuItem.Items.Add(item);
        }
    }

    private void OnBookmarkItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: Bookmark bookmark }) return;
        if (_mode != bookmark.Mode) SetMode(bookmark.Mode);
        if (bookmark.Mode == ReadingMode.Webtoon) JumpWebtoonToAnchor(bookmark.Anchor);
        else GoToPage(bookmark.PageIndex);
    }

    // ---------- Slideshow ----------

    private void OnSlideshowToggleClick(object sender, RoutedEventArgs e)
    {
        if (!_slideshowActive) StartSlideshow();
        else ToggleSlideshowPause();
    }

    private void OnSlideshowStopClick(object sender, RoutedEventArgs e) => StopSlideshow();

    private void OnSlideshowIntervalClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } && int.TryParse(value, out int seconds))
            SetSlideshowInterval(seconds);
    }

    private void OnSlideshowCustomIntervalClick(object sender, RoutedEventArgs e)
    {
        string? value = TextPromptDialog.Show(this, "幻灯片间隔", "秒数（1–30）：", "5");
        if (int.TryParse(value, out int seconds)) SetSlideshowInterval(seconds);
    }

    private void SetSlideshowInterval(int seconds)
    {
        _slideshowTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30));
        SaveM4Settings();
    }

    private void OnSlideshowRandomClick(object sender, RoutedEventArgs e) => SaveM4Settings();

    private void OnSlideshowLoopClick(object sender, RoutedEventArgs e) => SaveM4Settings();

    private void OnSlideshowHideControlsClick(object sender, RoutedEventArgs e)
    {
        SetM4ChromeVisible(true);
        if (_slideshowActive && SlideshowHideControlsMenuItem.IsChecked && !_slideshowPaused)
        {
            _slideshowHideTimer.Stop();
            _slideshowHideTimer.Start();
        }
        else
        {
            _slideshowHideTimer.Stop();
        }
        SaveM4Settings();
    }

    private void StartSlideshow()
    {
        if (_pages.Count == 0) return;
        SetMode(ReadingMode.SinglePage);
        _slideshowActive = true;
        _slideshowPaused = false;
        _slideshowVisited.Clear();
        _slideshowVisited.Add(_currentPageIndex);
        _slideshowTimer.Start();
        SlideshowToggleMenuItem.Header = "暂停";
        if (_settings.SlideshowHideControls)
        {
            _slideshowHideTimer.Start();
        }
    }

    private void ToggleSlideshowPause()
    {
        if (!_slideshowActive) return;
        _slideshowPaused = !_slideshowPaused;
        if (_slideshowPaused) _slideshowTimer.Stop();
        else _slideshowTimer.Start();
        SlideshowToggleMenuItem.Header = _slideshowPaused ? "继续" : "暂停";
        SetM4ChromeVisible(true);
        if (!_slideshowPaused && _settings.SlideshowHideControls)
        {
            _slideshowHideTimer.Stop();
            _slideshowHideTimer.Start();
        }
    }

    private void StopSlideshow()
    {
        _slideshowTimer.Stop();
        _slideshowHideTimer.Stop();
        _slideshowActive = false;
        _slideshowPaused = false;
        SlideshowToggleMenuItem.Header = "开始 / 暂停";
        SetM4ChromeVisible(true);
    }

    private void OnSlideshowTick(object? sender, EventArgs e)
    {
        if (_pages.Count == 0) { StopSlideshow(); return; }
        int next = ChooseSlideshowPage();
        if (next < 0) { StopSlideshow(); return; }
        _slideshowVisited.Add(next);
        GoToPage(next);
    }

    private int ChooseSlideshowPage()
    {
        if (SlideshowRandomMenuItem.IsChecked)
        {
            var candidates = Enumerable.Range(0, _pages.Count).Where(i => !_slideshowVisited.Contains(i)).ToList();
            if (candidates.Count == 0)
            {
                if (!SlideshowLoopMenuItem.IsChecked) return -1;
                _slideshowVisited.Clear();
                candidates = Enumerable.Range(0, _pages.Count).ToList();
            }
            return candidates[Random.Shared.Next(candidates.Count)];
        }

        int next = _currentPageIndex + 1;
        if (next < _pages.Count) return next;
        return SlideshowLoopMenuItem.IsChecked ? 0 : -1;
    }

    private void RefreshSlideshowMenuChecks()
    {
        SlideshowRandomMenuItem.IsChecked = _settings.SlideshowRandom;
        SlideshowLoopMenuItem.IsChecked = _settings.SlideshowLoop;
        SlideshowHideControlsMenuItem.IsChecked = _settings.SlideshowHideControls;
    }

    private void SetM4ChromeVisible(bool visible)
    {
        _controlsHidden = !visible;
        TopMenu.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BottomBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ThumbnailBar.Visibility = visible && _thumbnailVisible ? Visibility.Visible : Visibility.Collapsed;
        InfoPanel.Visibility = visible && _infoPanelVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Panels / metadata ----------

    private void OnToggleInfoPanelClick(object sender, RoutedEventArgs e)
    {
        _infoPanelVisible = InfoPanelMenuItem.IsChecked;
        RefreshPanelVisibility();
        QueueMetadataRefresh();
        SaveM4Settings();
    }

    private void OnToggleThumbnailClick(object sender, RoutedEventArgs e)
    {
        _thumbnailVisible = ThumbnailMenuItem.IsChecked;
        RefreshPanelVisibility();
        if (_thumbnailVisible) UpdateThumbnailPages();
        SaveM4Settings();
    }

    private void RefreshPanelVisibility()
    {
        bool chromeVisible = !_controlsHidden;
        InfoPanelMenuItem.IsChecked = _infoPanelVisible;
        ThumbnailMenuItem.IsChecked = _thumbnailVisible;
        InfoPanel.Visibility = chromeVisible && _infoPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        ThumbnailBar.Visibility = chromeVisible && _thumbnailVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnThumbnailPageRequested(int index) => GoToPage(index);

    private void UpdateThumbnailPages() => ThumbnailBar.SetPages(_pages);

    // ---------- Window / monitor ----------

    private void ApplySavedWindowPlacement()
    {
        var placement = _settings.WindowPlacement;
        if (placement is null) return;
        var rect = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!virtualScreen.IntersectsWith(rect)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = Math.Max(640, placement.Width);
        Height = Math.Max(480, placement.Height);
        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    private WindowPlacement CaptureWindowPlacement()
    {
        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        return new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            WindowState == WindowState.Maximized, MonitorService.GetMonitorFromWindow(new WindowInteropHelper(this).Handle)?.DeviceName);
    }

    private void OnMoveToNextMonitorClick(object sender, RoutedEventArgs e)
    {
        var monitors = MonitorService.GetMonitors();
        if (monitors.Count <= 1) return;
        var current = MonitorService.GetMonitorFromWindow(new WindowInteropHelper(this).Handle);
        int index = current is null ? -1 : monitors.ToList().FindIndex(m => m.DeviceName == current.DeviceName);
        var target = monitors[(index + 1) % monitors.Count];
        WindowState = WindowState.Normal;
        MonitorService.MoveWindowToMonitor(new WindowInteropHelper(this).Handle, target);
    }

    // ---------- HDR / color status ----------

    private void UpdateHdrStatus()
    {
        HdrStatus status = MonitorService.GetHdrStatus();
        HdrMenuItem.IsEnabled = status.Supported;
        HdrMenuItem.IsChecked = status.Enabled;
        HdrMenuItem.ToolTip = status.Supported
            ? $"Windows 高级颜色：{(status.Enabled ? "已启用" : "未启用")}，{status.BitsPerColorChannel} bit/通道"
            : "当前显示器或显卡未报告高级颜色支持";
    }

    private void OnHdrClick(object sender, RoutedEventArgs e)
    {
        HdrStatus status = MonitorService.GetHdrStatus();
        HdrMenuItem.IsChecked = status.Enabled;
        MessageBox.Show(this,
            status.Supported
                ? $"Windows 高级颜色/HDR：{(status.Enabled ? "已启用" : "未启用")}\n色深：{status.BitsPerColorChannel} bit/通道\n\nMangaView 使用 WPF 合成管线，HDR 输出会按系统 sRGB 回退。"
                : "当前显示器或显卡未报告 HDR / 高级颜色支持。",
            "HDR / 高级颜色", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool HandleM4Key(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (_slideshowActive && e.Key == Key.Escape)
        {
            StopSlideshow();
            e.Handled = true;
            return true;
        }
        if (_slideshowActive && e.Key == Key.Space)
        {
            ToggleSlideshowPause();
            e.Handled = true;
            return true;
        }
        if (e.Key == Key.F5)
        {
            OnSlideshowToggleClick(this, new RoutedEventArgs());
            e.Handled = true;
            return true;
        }
        if (e.Key == Key.Tab)
        {
            _infoPanelVisible = !_infoPanelVisible;
            RefreshPanelVisibility();
            QueueMetadataRefresh();
            SaveM4Settings();
            e.Handled = true;
            return true;
        }
        if (!ctrl && e.Key == Key.T)
        {
            _thumbnailVisible = !_thumbnailVisible;
            RefreshPanelVisibility();
            if (_thumbnailVisible) UpdateThumbnailPages();
            SaveM4Settings();
            e.Handled = true;
            return true;
        }
        if (!ctrl && e.Key == Key.B)
        {
            OnAddBookmarkClick(this, new RoutedEventArgs());
            e.Handled = true;
            return true;
        }
        if (ctrl && e.Key == Key.B)
        {
            OnManageBookmarksClick(this, new RoutedEventArgs());
            e.Handled = true;
            return true;
        }
        return false;
    }
}
