namespace MangaView.Core;

public enum DecodePriority
{
    /// <summary>当前视口内的页，最高优先级。</summary>
    Viewport = 0,
    /// <summary>视口上下相邻的预加载页。</summary>
    Preload = 1,
    /// <summary>缩略图等低优先级任务。</summary>
    Thumbnail = 2,
}

/// <summary>解码请求：Key 为缓存键，Path 与 DecodeWidth 描述解码任务，结果对 Core 不透明。</summary>
public sealed record DecodeRequest(
    string Key,
    int PageIndex,
    string Path,
    int DecodeWidth,
    DecodePriority Priority);

/// <summary>具体解码由宿主实现（WPF: BitmapSource；测试: 假对象）。Core 不依赖任何 UI 框架。</summary>
public interface IDecodeWorker
{
    Task<object?> DecodeAsync(DecodeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 解码调度器：
/// - 并发上限（M0 默认 2，避免长图解码挤爆 IO/内存）；
/// - 同 Key 去重（在途/排队中只保留一个任务）；
/// - 优先级队列（视口 &gt; 预加载 &gt; 缩略图），排队任务可被更高优先级升级。
/// </summary>
public sealed class DecodeScheduler : IDisposable
{
    private sealed class Pending
    {
        public required DecodeRequest Request { get; set; }
        public required TaskCompletionSource<object?> Tcs { get; init; }
        public required int Sequence { get; init; }
    }

    private static readonly Comparer<Pending> PendingComparer = Comparer<Pending>.Create((a, b) =>
    {
        int c = a.Request.Priority.CompareTo(b.Request.Priority);
        return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
    });

    private readonly IDecodeWorker _worker;
    private readonly int _maxConcurrent;
    private readonly object _gate = new();
    private readonly List<Pending> _queue = new();               // 按 (优先级, 序号) 升序
    private readonly Dictionary<string, Pending> _queued = new();  // key -> 排队任务
    private readonly Dictionary<string, Task<object?>> _inFlight = new(); // key -> 在途任务
    private int _nextSequence;
    private bool _disposed;

    public DecodeScheduler(IDecodeWorker worker, int maxConcurrent = 2)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        _worker = worker;
        _maxConcurrent = maxConcurrent;
    }

    public int InFlightCount { get { lock (_gate) return _inFlight.Count; } }
    public int QueuedCount { get { lock (_gate) return _queue.Count; } }

    /// <summary>取消尚未开始的排队任务；在途任务继续完成，以便单页快速翻页时不堆积旧页。</summary>
    public int CancelQueued()
    {
        List<Pending> canceled;
        lock (_gate)
        {
            canceled = new List<Pending>(_queue);
            _queue.Clear();
            _queued.Clear();
        }

        foreach (var item in canceled)
            item.Tcs.TrySetCanceled();
        return canceled.Count;
    }

    public Task<object?> RequestAsync(DecodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_disposed)
                return Task.FromException<object?>(new ObjectDisposedException(nameof(DecodeScheduler)));

            // 同 Key 去重：在途直接复用
            if (_inFlight.TryGetValue(request.Key, out var running))
                return running;

            // 排队中：可选优先级升级
            if (_queued.TryGetValue(request.Key, out var pending))
            {
                if (request.Priority < pending.Request.Priority)
                {
                    pending.Request = request;
                    _queue.Remove(pending);
                    InsertSorted(pending);
                }
                return pending.Tcs.Task;
            }

            var item = new Pending
            {
                Request = request,
                Tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously),
                Sequence = _nextSequence++,
            };
            _queued[request.Key] = item;
            InsertSorted(item);
            PumpLocked();
            return item.Tcs.Task;
        }
    }

    private void InsertSorted(Pending item)
    {
        int idx = _queue.BinarySearch(item, PendingComparer);
        _queue.Insert(idx < 0 ? ~idx : idx, item);
    }

    private void PumpLocked()
    {
        while (_inFlight.Count < _maxConcurrent && _queue.Count > 0)
        {
            var item = _queue[0];
            _queue.RemoveAt(0);
            _queued.Remove(item.Request.Key);
            // 先注册 TCS Task（与 RequestAsync 返回的同一对象），再启动后台执行；
            // 若解码同步完成，RunAsync 的 finally 也能正确移除在途项。
            _inFlight[item.Request.Key] = item.Tcs.Task;
            _ = RunAsync(item);
        }
    }

    private async Task<object?> RunAsync(Pending item)
    {
        try
        {
            var result = await _worker.DecodeAsync(item.Request, CancellationToken.None).ConfigureAwait(false);
            item.Tcs.TrySetResult(result);
        }
        catch (Exception ex)
        {
            item.Tcs.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(item.Request.Key);
                PumpLocked();
            }
        }
        return null!;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var p in _queue)
                p.Tcs.TrySetCanceled();
            _queue.Clear();
            _queued.Clear();
        }
    }
}

