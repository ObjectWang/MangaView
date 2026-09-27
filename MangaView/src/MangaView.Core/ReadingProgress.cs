using System.Text.Json;
using System.Text.Json.Serialization;

namespace MangaView.Core;

/// <summary>单本书的阅读进度。Webtoon 使用 Anchor 恢复页内偏移；双页使用 PageIndex。</summary>
public sealed record ReadingProgress(
    string SourceKey,
    ReadingMode Mode,
    int PageIndex,
    ScrollAnchor Anchor,
    ReadingDirection Direction,
    bool DoubleCoverPage,
    double DoubleGap,
    DateTime UpdatedUtc,
    ZoomMode ZoomMode = ZoomMode.FitWindow,
    double ZoomScale = 1.0,
    double WebtoonZoom = 1.0);

public sealed class ReadingProgressStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private Dictionary<string, ReadingProgress> _items = new();
    private bool _loaded;

    public string FilePath { get; }

    public ReadingProgressStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public void Load()
    {
        lock (_gate)
        {
            _items = new Dictionary<string, ReadingProgress>();
            if (!File.Exists(FilePath)) { _loaded = true; return; }
            try
            {
                using var stream = File.OpenRead(FilePath);
                var list = JsonSerializer.Deserialize<List<ReadingProgress>>(stream, JsonOptions) ?? new();
                _items = list
                    .Where(p => !string.IsNullOrWhiteSpace(p.SourceKey))
                    .GroupBy(p => p.SourceKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                // 进度文件损坏不应阻止软件启动；后续保存会用有效数据覆盖。
                _items = new Dictionary<string, ReadingProgress>(StringComparer.OrdinalIgnoreCase);
            }
            _loaded = true;
        }
    }

    private void EnsureLoaded()
    {
        if (!_loaded) Load();
    }

    public ReadingProgress? Get(string sourceKey)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _items.TryGetValue(sourceKey, out var p) ? p : null;
        }
    }

    public void Update(ReadingProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (_gate)
        {
            EnsureLoaded();
            _items[progress.SourceKey] = progress;
        }
    }

    public void Save()
    {
        List<ReadingProgress> snapshot;
        lock (_gate)
        {
            EnsureLoaded();
            snapshot = _items.Values.OrderByDescending(p => p.UpdatedUtc).ToList();
        }
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = FilePath + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, snapshot, JsonOptions);
        }
        if (File.Exists(FilePath)) File.Delete(FilePath);
        File.Move(temp, FilePath);
    }
}

/// <summary>全局偏好设置。</summary>
public sealed record AppSettings(
    ReadingMode DefaultMode,
    ReadingDirection DefaultDirection,
    bool DefaultDoubleCoverPage,
    double WebtoonGap,
    bool DarkTheme,
    ThemePreference Theme = ThemePreference.System,
    bool SaveRecent = true,
    bool RememberProgress = true,
    bool ShowInfoPanel = false,
    bool ShowThumbnails = false,
    int SlideshowIntervalSeconds = 5,
    int WebtoonSlideshowSpeed = 40,
    bool SlideshowRandom = false,
    bool SlideshowLoop = true,
    bool SlideshowHideControls = false,
    bool HdrEnabled = false,
    WindowPlacement? WindowPlacement = null)
{
    public static AppSettings Default { get; } = new(
        ReadingMode.SinglePage,
        ReadingDirection.LeftToRight,
        true,
        8,
        true);
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed record WindowPlacement(
    double Left,
    double Top,
    double Width,
    double Height,
    bool Maximized,
    string? MonitorDeviceName);

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();

    public string FilePath { get; }
    public AppSettings Settings { get; private set; } = AppSettings.Default;

    public AppSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath)) { Settings = AppSettings.Default; return; }
            try
            {
                using var stream = File.OpenRead(FilePath);
                Settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? AppSettings.Default;
            }
            catch (JsonException)
            {
                Settings = AppSettings.Default;
            }
        }
    }

    public void Update(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate) Settings = settings;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = FilePath + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, Settings, JsonOptions);
        }
        if (File.Exists(FilePath)) File.Delete(FilePath);
        File.Move(temp, FilePath);
    }
}
