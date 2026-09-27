namespace MangaView.Core;

/// <summary>
/// 线程安全的按字节预算 LRU 缓存，用于保存已解码位图。
/// 超预算时从最久未使用端淘汰，并通过 ItemEvicted 通知渲染层重绘。
/// </summary>
public sealed class LruCache<T> where T : class
{
    private sealed class Entry(string key, T value, long size)
    {
        public string Key = key;
        public T Value = value;
        public long Size = size;
    }

    private readonly object _gate = new();
    private readonly long _capacityBytes;
    private readonly LinkedList<Entry> _order = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new();
    private long _used;

    public LruCache(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacityBytes, 0);
        _capacityBytes = capacityBytes;
    }

    public event Action<string, T, long>? ItemEvicted;

    public int Count { get { lock (_gate) return _map.Count; } }
    public long UsedBytes { get { lock (_gate) return _used; } }
    public long CapacityBytes => _capacityBytes;

    public bool TryGet(string key, out T value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = null!;
        return false;
    }

    public void Put(string key, T value, long sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sizeBytes, 0);
        if (sizeBytes > _capacityBytes) return; // 单项超过总预算，不缓存
        List<(string Key, T Value, long Size)>? evicted = null;
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _used -= node.Value.Size;
                _order.Remove(node);
                _map.Remove(key);
                (evicted ??= new()).Add((node.Value.Key, node.Value.Value, node.Value.Size));
            }
            var newNode = _order.AddFirst(new Entry(key, value, sizeBytes));
            _map[key] = newNode;
            _used += sizeBytes;
            while (_used > _capacityBytes && _order.Last is { } last)
            {
                var e = last.Value;
                _order.RemoveLast();
                _map.Remove(e.Key);
                _used -= e.Size;
                (evicted ??= new()).Add((e.Key, e.Value, e.Size));
            }
        }
        if (evicted is not null)
            foreach (var e in evicted)
                ItemEvicted?.Invoke(e.Key, e.Value, e.Size);
    }

    public void Clear()
    {
        List<(string Key, T Value, long Size)> cleared = new();
        lock (_gate)
        {
            foreach (var entry in _order)
                cleared.Add((entry.Key, entry.Value, entry.Size));
            _order.Clear();
            _map.Clear();
            _used = 0;
        }
        foreach (var entry in cleared)
            ItemEvicted?.Invoke(entry.Key, entry.Value, entry.Size);
    }
}
