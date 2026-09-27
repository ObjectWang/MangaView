using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MangaView.Core;

namespace MangaView.App;

public partial class MainWindow
{
    private readonly ReadingProgressStore _progressStore = new(GetProgressPath());
    private readonly DispatcherTimer _progressSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private CancellationTokenSource? _sessionCts;
    private FileSystemWatcher? _watcher;
    private string? _sourceKey;
    private string? _currentArchive;
    private ReadingDirection _direction = ReadingDirection.LeftToRight;
    private bool _doubleCoverPage = true;
    private double _doubleGap = 12;
    private bool _suppressProgressSave;
    private bool _isRefreshing;
    private int _loadGeneration;
    private ScrollAnchor _lastWebtoonAnchor = new(0, 0);
    private bool _webtoonAnchorDirty;

    private void InitializeM2Session()
    {
        try
        {
            _progressStore.Load();
        }
        catch
        {
            // 进度存储不可用时仍允许浏览，只是不恢复历史位置。
        }

        _progressSaveTimer.Tick += (_, _) =>
        {
            _progressSaveTimer.Stop();
            SaveProgressNow();
        };
        _reloadTimer.Tick += async (_, _) =>
        {
            _reloadTimer.Stop();
            await ReloadCurrentSourceAsync();
        };
    }

    private void ShutdownM2Session()
    {
        _progressSaveTimer.Stop();
        _reloadTimer.Stop();
        _watcher?.Dispose();
        _sessionCts?.Cancel();
        SaveProgressNow();
    }

    private static string GetLocalDataRoot()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable("MANGAVIEW_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return Path.GetFullPath(overrideRoot);

        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        return Path.Combine(root, "MangaView");
    }

    private static string GetProgressPath() => Path.Combine(GetLocalDataRoot(), "progress.json");

    private void OnOpenArchiveClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开 CBZ / ZIP 漫画",
            Filter = "漫画压缩包|*.cbz;*.zip|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            _ = LoadPathAsync(dialog.FileName);
    }

    private async Task LoadFolderAsync(string folder, string? initialFile = null)
    {
        _sessionCts?.Cancel();
        _sessionCts = new CancellationTokenSource();
        CancellationToken ct = _sessionCts.Token;

        try
        {
            IReadOnlyList<string> files = await Task.Run(
                () => ImageCatalog.EnumerateImages(folder, _recursive), ct);
            ct.ThrowIfCancellationRequested();
            if (files.Count == 0)
            {
                ShowEmptySource("没有找到图片");
                return;
            }

            int initialIndex = initialFile is null ? 0 : ImageCatalog.IndexOfPath(files, initialFile);
            if (initialIndex < 0) initialIndex = 0;

            string fullFolder = Path.GetFullPath(folder);
            string sourceKey = $"folder:{fullFolder}|recursive:{_recursive}";
            string title = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullFolder));
            _currentFolder = folder;
            _currentArchive = null;
            _currentFile = initialFile;

            ConfigureWatcher(fullFolder, filter: null, recursive: _recursive);
            await LoadFilesAsync(sourceKey, title, files, initialIndex, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowLoadError("打开文件夹失败", ex);
        }
    }

    private async Task LoadArchiveAsync(string archivePath)
    {
        _sessionCts?.Cancel();
        _sessionCts = new CancellationTokenSource();
        CancellationToken ct = _sessionCts.Token;

        try
        {
            string fullPath = Path.GetFullPath(archivePath);
            ScanText.Text = "解压中…";
            string cacheRoot = Path.Combine(GetLocalDataRoot(), "cache", "cbz");
            CbzExtractionResult result = await CbzArchive.ExtractAsync(fullPath, cacheRoot,
                CbzExtractionOptions.Default, ct);
            ct.ThrowIfCancellationRequested();

            if (result.Files.Count == 0)
            {
                ShowEmptySource("压缩包中没有支持的图片");
                return;
            }

            _currentFolder = null;
            _currentArchive = fullPath;
            ConfigureWatcher(Path.GetDirectoryName(fullPath), Path.GetFileName(fullPath), recursive: false);
            await LoadFilesAsync($"archive:{fullPath}", Path.GetFileName(fullPath), result.Files, 0, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidDataException ex)
        {
            ScanText.Text = "";
            MessageBox.Show(this, $"压缩包损坏、加密或格式异常：{ex.Message}", "MangaView",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            ShowLoadError("打开压缩包失败", ex);
        }
    }

    private async Task LoadFilesAsync(string sourceKey, string title, IReadOnlyList<string> files,
        int initialIndex, CancellationToken ct)
    {
        int generation = ++_loadGeneration;
        _sourceKey = sourceKey;
        ReadingProgress? saved = _settings.RememberProgress ? _progressStore.Get(sourceKey) : null;
        _lastWebtoonAnchor = saved?.Anchor ?? new ScrollAnchor(initialIndex, 0);
        _webtoonAnchorDirty = false;
        _suppressProgressSave = true;

        try
        {
            _pages.Clear();
            for (int i = 0; i < files.Count; i++)
                _pages.Add(new ImagePage(i, files[i], Path.GetFileName(files[i]), PlaceholderWidth, PlaceholderHeight));

            int startIndex = saved is null
                ? Math.Clamp(initialIndex, 0, _pages.Count - 1)
                : Math.Clamp(saved.PageIndex, 0, _pages.Count - 1);
            _currentPageIndex = startIndex;
            ApplySavedSettings(saved);
            ApplyPlaylist(startIndex);
            RestoreSavedNavigation(saved);
            RecordCurrentSourceRecent();
            UpdateThumbnailPages();
            RefreshBookmarksMenu();

            EmptyHint.Visibility = Visibility.Collapsed;
            Title = $"MangaView — {title}";
            ScanText.Text = $"扫描中 0/{files.Count}";

            var probed = new ImagePage[files.Count];
            int[] failureCount = { 0 };
            await Task.Run(() =>
            {
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var (width, height) = WpfDecodeWorker.ProbeSize(files[i]);
                        probed[i] = new ImagePage(i, files[i], Path.GetFileName(files[i]), width, height);
                    }
                    catch
                    {
                        Interlocked.Increment(ref failureCount[0]);
                        probed[i] = new ImagePage(i, files[i], Path.GetFileName(files[i]), PlaceholderWidth, PlaceholderHeight);
                    }

                    if (i % 20 == 0 || i == files.Count - 1)
                    {
                        int done = i + 1;
                        _ = Dispatcher.InvokeAsync(() => ScanText.Text = $"扫描中 {done}/{files.Count}");
                    }
                }
            }, ct);

            ct.ThrowIfCancellationRequested();
            if (generation != _loadGeneration) return;

            _pages.Clear();
            _pages.AddRange(probed);
            RebuildActiveCanvasAfterScan(saved);
            ScanText.Text = failureCount[0] == 0
                ? $"共 {_pages.Count} 页"
                : $"共 {_pages.Count} 页 · {failureCount[0]} 张读取失败";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _loadGeneration) ShowLoadError("加载图片失败", ex);
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                _suppressProgressSave = false;
                ScheduleProgressSave();
            }
        }
    }

    private void ApplySavedSettings(ReadingProgress? saved)
    {
        _mode = saved is not null && Enum.IsDefined(saved.Mode) ? saved.Mode : _settings.DefaultMode;
        _direction = saved is not null && Enum.IsDefined(saved.Direction)
            ? saved.Direction
            : _settings.DefaultDirection;
        _doubleCoverPage = saved?.DoubleCoverPage ?? _settings.DefaultDoubleCoverPage;
        _doubleGap = saved is { DoubleGap: >= 0 } ? saved.DoubleGap : 12;
        Webtoon.Gap = Math.Max(0, _settings.WebtoonGap);
        Webtoon.SetZoomFactor(saved?.WebtoonZoom ?? 1.0);
        UpdateDoubleSettingsUi();

        if (_mode == ReadingMode.SinglePage && saved is not null)
            SinglePage.RestoreZoom(saved.ZoomMode, saved.ZoomScale);
    }

    private void RestoreSavedNavigation(ReadingProgress? saved)
    {
        if (saved is null || _pages.Count == 0) return;
        int page = Math.Clamp(saved.PageIndex, 0, _pages.Count - 1);
        switch (_mode)
        {
            case ReadingMode.SinglePage:
                SinglePage.SetPageIndex(page);
                if (saved.ZoomMode == ZoomMode.OriginalSize || saved.ZoomScale > 0)
                    SinglePage.RestoreZoom(saved.ZoomMode, saved.ZoomScale);
                break;
            case ReadingMode.Webtoon:
                JumpWebtoonToAnchor(saved.Anchor.PageIndex >= 0
                    ? saved.Anchor
                    : new ScrollAnchor(page, 0));
                break;
            case ReadingMode.DoublePage:
                DoublePage.GoToPage(page);
                break;
        }
    }

    private void RebuildActiveCanvasAfterScan(ReadingProgress? saved)
    {
        switch (_mode)
        {
            case ReadingMode.Webtoon:
            {
                ScrollAnchor anchor = _webtoonAnchorDirty && _lastWebtoonAnchor.PageIndex >= 0
                    ? _lastWebtoonAnchor
                    : saved?.Anchor ?? new ScrollAnchor(_currentPageIndex, 0);
                Webtoon.SetPages(_pages);
                JumpWebtoonToAnchor(anchor);
                break;
            }
            case ReadingMode.DoublePage:
            {
                int page = Math.Clamp(_currentPageIndex, 0, Math.Max(0, _pages.Count - 1));
                DoublePage.Configure(_direction, _doubleCoverPage, _doubleGap);
                DoublePage.SetPages(_pages);
                DoublePage.GoToPage(page);
                UpdateThumbnailPages();
                break;
            }
        }
    }

    private void ScheduleProgressSave()
    {
        if (!_settings.RememberProgress || _suppressProgressSave || _sourceKey is null || _pages.Count == 0) return;
        _progressSaveTimer.Stop();
        _progressSaveTimer.Start();
    }

    private void SaveProgressNow()
    {
        if (!_settings.RememberProgress || _sourceKey is null || _pages.Count == 0) return;
        try
        {
            int page = Math.Clamp(_currentPageIndex, 0, _pages.Count - 1);
            var progress = new ReadingProgress(
                _sourceKey,
                _mode,
                page,
                _mode == ReadingMode.Webtoon ? Webtoon.CurrentAnchor : new ScrollAnchor(page, 0),
                _direction,
                _doubleCoverPage,
                _doubleGap,
                DateTime.UtcNow,
                SinglePage.Transform.Mode,
                SinglePage.Transform.Scale,
                _mode == ReadingMode.Webtoon ? Webtoon.ZoomFactor : 1.0);
            _progressStore.Update(progress);
            _progressStore.Save();
        }
        catch
        {
            // 保存失败不应影响阅读或关闭流程。
        }
    }

    private void OnLeftToRightClick(object sender, RoutedEventArgs e) => SetReadingDirection(ReadingDirection.LeftToRight);

    private void OnRightToLeftClick(object sender, RoutedEventArgs e) => SetReadingDirection(ReadingDirection.RightToLeft);

    private void SetReadingDirection(ReadingDirection direction)
    {
        _direction = direction;
        UpdateDoubleSettingsUi();
        if (_mode == ReadingMode.DoublePage)
            DoublePage.Configure(_direction, _doubleCoverPage, _doubleGap);
        UpdateModeUi();
        ScheduleProgressSave();
    }

    private void OnToggleDoubleCoverClick(object sender, RoutedEventArgs e)
    {
        _doubleCoverPage = DoubleCoverMenuItem.IsChecked;
        if (_mode == ReadingMode.DoublePage)
            DoublePage.Configure(_direction, _doubleCoverPage, _doubleGap);
        ScheduleProgressSave();
    }

    private void OnDoubleGapClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } &&
            double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double gap))
        {
            _doubleGap = gap;
            if (_mode == ReadingMode.DoublePage)
                DoublePage.Configure(_direction, _doubleCoverPage, _doubleGap);
            ScheduleProgressSave();
        }
    }

    private void UpdateDoubleSettingsUi()
    {
        LeftToRightMenuItem.IsChecked = _direction == ReadingDirection.LeftToRight;
        RightToLeftMenuItem.IsChecked = _direction == ReadingDirection.RightToLeft;
        DoubleCoverMenuItem.IsChecked = _doubleCoverPage;
    }

    private void MoveDoubleSpread(int delta)
    {
        if (_mode != ReadingMode.DoublePage) return;
        bool moved = delta > 0 ? DoublePage.NextSpread() : DoublePage.PreviousSpread();
        if (moved) ScheduleProgressSave();
    }

    private void JumpWebtoonToPage(int page) =>
        JumpWebtoonToAnchor(new ScrollAnchor(Math.Max(0, page), 0));

    private void JumpWebtoonToAnchor(ScrollAnchor anchor)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_mode != ReadingMode.Webtoon || _pages.Count == 0) return;
            ScrollHost.UpdateLayout();
            double offset = Webtoon.OffsetFromAnchor(anchor);
            ScrollHost.ScrollToVerticalOffset(offset);
            Webtoon.OnViewScrolled(offset, ScrollHost.ViewportHeight);
        }, DispatcherPriority.Loaded);
    }

    private void HandleDoublePageKey(KeyEventArgs e, bool ctrl, bool shift)
    {
        switch (e.Key)
        {
            case Key.Left:
                MoveDoubleSpread(_direction == ReadingDirection.LeftToRight ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Right:
                MoveDoubleSpread(_direction == ReadingDirection.LeftToRight ? 1 : -1);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveDoubleSpread(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveDoubleSpread(1);
                e.Handled = true;
                break;
            case Key.Space:
                MoveDoubleSpread(shift ? -1 : 1);
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
        }
    }

    private void ConfigureWatcher(string? directory, string? filter, bool recursive)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                Filter = string.IsNullOrWhiteSpace(filter) ? "*.*" : filter,
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                               NotifyFilters.Size | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnWatchedSourceChanged;
            watcher.Created += OnWatchedSourceChanged;
            watcher.Deleted += OnWatchedSourceChanged;
            watcher.Renamed += OnWatchedSourceChanged;
            _watcher = watcher;
        }
        catch
        {
            _watcher = null;
        }
    }

    private void OnWatchedSourceChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _reloadTimer.Stop();
            _reloadTimer.Start();
        }, DispatcherPriority.Background);
    }

    private async Task ReloadCurrentSourceAsync()
    {
        if (_isRefreshing || _sourceKey is null || _pages.Count == 0) return;
        _isRefreshing = true;
        try
        {
            SaveProgressNow();
            string? currentPath = _currentPageIndex >= 0 && _currentPageIndex < _pages.Count
                ? _pages[_currentPageIndex].Path
                : null;
            if (_currentArchive is not null)
            {
                await LoadArchiveAsync(_currentArchive);
            }
            else if (_currentFolder is not null)
            {
                await LoadFolderAsync(_currentFolder, currentPath);
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void ShowEmptySource(string message)
    {
        _watcher?.Dispose();
        _watcher = null;
        _currentFolder = null;
        _currentArchive = null;
        _pages.Clear();
        _currentPageIndex = -1;
        _sourceKey = null;
        RefreshBookmarksMenu();
        SinglePage.SetPages(_pages, 0);
        Webtoon.SetPages(_pages);
        DoublePage.SetPages(_pages);
        UpdateThumbnailPages();
        EmptyHint.Visibility = Visibility.Visible;
        Title = "MangaView";
        ScanText.Text = message;
        PageInfoText.Text = "未打开";
    }

    private void ShowLoadError(string title, Exception ex)
    {
        ScanText.Text = "";
        MessageBox.Show(this, $"{title}：{ex.Message}", "MangaView",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
