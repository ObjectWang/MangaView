using System.Text.Json;
using System.Text.Json.Serialization;

namespace MangaView.Core;

public sealed record Bookmark(
    string Id,
    string SourceKey,
    string Name,
    int PageIndex,
    ReadingMode Mode,
    ScrollAnchor Anchor,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>按数据源保存命名书签；JSON 文件损坏时回退为空列表。</summary>
public sealed class BookmarkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private List<Bookmark> _items = new();
    private bool _loaded;

    public string FilePath { get; }

    public BookmarkStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public IReadOnlyList<Bookmark> Get(string sourceKey)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _items
                .Where(b => string.Equals(b.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(b => b.PageIndex)
                .ThenBy(b => b.CreatedUtc)
                .ToArray();
        }
    }

    public Bookmark Add(string sourceKey, string name, int pageIndex, ReadingMode mode, ScrollAnchor anchor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var now = DateTime.UtcNow;
        var bookmark = new Bookmark(Guid.NewGuid().ToString("N"), sourceKey, name.Trim(),
            Math.Max(0, pageIndex), mode, anchor, now, now);
        lock (_gate)
        {
            EnsureLoaded();
            _items.Add(bookmark);
            SaveLocked();
        }
        return bookmark;
    }

    public bool Rename(string sourceKey, string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            EnsureLoaded();
            int index = FindIndexLocked(sourceKey, id);
            if (index < 0) return false;
            _items[index] = _items[index] with { Name = name.Trim(), UpdatedUtc = DateTime.UtcNow };
            SaveLocked();
            return true;
        }
    }

    public bool Delete(string sourceKey, string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            int index = FindIndexLocked(sourceKey, id);
            if (index < 0) return false;
            _items.RemoveAt(index);
            SaveLocked();
            return true;
        }
    }

    private int FindIndexLocked(string sourceKey, string id) => _items.FindIndex(b =>
        string.Equals(b.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(b.Id, id, StringComparison.Ordinal));

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _items = new List<Bookmark>();
        if (File.Exists(FilePath))
        {
            try
            {
                using var stream = File.OpenRead(FilePath);
                _items = JsonSerializer.Deserialize<List<Bookmark>>(stream, JsonOptions) ?? new();
            }
            catch (JsonException)
            {
                _items = new List<Bookmark>();
            }
        }
        _loaded = true;
    }

    private void SaveLocked()
    {
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = FilePath + ".tmp";
        using (var stream = File.Create(temp))
            JsonSerializer.Serialize(stream, _items, JsonOptions);
        if (File.Exists(FilePath)) File.Delete(FilePath);
        File.Move(temp, FilePath);
    }
}

public enum RecentKind
{
    Image,
    Folder,
    Archive,
}

public sealed record RecentEntry(
    string Path,
    RecentKind Kind,
    string DisplayName,
    DateTime LastOpenedUtc);

/// <summary>最近打开记录，最多保留 20 条，按最后打开时间倒序。</summary>
public sealed class RecentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private List<RecentEntry> _items = new();
    private bool _loaded;

    public string FilePath { get; }

    public RecentStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public IReadOnlyList<RecentEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _items.ToArray();
            }
        }
    }

    public void Add(string path, RecentKind kind, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = System.IO.Path.GetFullPath(path);
        lock (_gate)
        {
            EnsureLoaded();
            _items.RemoveAll(e => string.Equals(e.Path, fullPath, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, new RecentEntry(fullPath, kind,
                displayName ?? System.IO.Path.GetFileName(fullPath), DateTime.UtcNow));
            if (_items.Count > 20) _items.RemoveRange(20, _items.Count - 20);
            SaveLocked();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            EnsureLoaded();
            _items.Clear();
            SaveLocked();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _items = new List<RecentEntry>();
        if (File.Exists(FilePath))
        {
            try
            {
                using var stream = File.OpenRead(FilePath);
                _items = JsonSerializer.Deserialize<List<RecentEntry>>(stream, JsonOptions) ?? new();
            }
            catch (JsonException)
            {
                _items = new List<RecentEntry>();
            }
        }
        _loaded = true;
    }

    private void SaveLocked()
    {
        string? directory = System.IO.Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = FilePath + ".tmp";
        using (var stream = File.Create(temp))
            JsonSerializer.Serialize(stream, _items, JsonOptions);
        if (File.Exists(FilePath)) File.Delete(FilePath);
        File.Move(temp, FilePath);
    }
}
