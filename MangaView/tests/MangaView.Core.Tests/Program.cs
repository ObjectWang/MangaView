using System.Diagnostics;
using MangaView.Core;

// 轻量测试运行器（不依赖外部测试框架，避免联网还原 NuGet 包）
var tests = new (string Name, Func<Task> Run)[]
{
    ("NaturalSort: 数值段按数值排序", TestNaturalSort),
    ("WebtoonLayout: 布局与二分查找", TestLayout),
    ("WebtoonLayout: 滚动锚点往返", TestAnchorRoundTrip),
    ("LruCache: 预算淘汰与访问刷新", TestLruCache),
    ("DecodeScheduler: 优先级排序", TestSchedulerPriority),
    ("DecodeScheduler: 同 Key 去重与优先级升级", TestSchedulerDedupAndUpgrade),
    ("虚拟化模拟: 500 页滚动仅保持小常驻集", TestVirtualScrollSimulation),
    ("虚拟化模拟: 跳转页 300 只解码附近页", TestJumpVirtualization),
};

int failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"[FAIL] {name}: {ex.Message}");
    }
}

Console.WriteLine(failed == 0
    ? $"\n全部 {tests.Length} 项测试通过。"
    : $"\n{failed}/{tests.Length} 项测试失败。");
return failed == 0 ? 0 : 1;

static void AssertTrue(bool cond, string msg)
{
    if (!cond) throw new Exception(msg);
}

static void AssertEqual<T>(T expected, T actual, string ctx = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{ctx} expected={expected} actual={actual}");
}

static async Task TestNaturalSort()
{
    var names = new[] { "page_10.png", "page_2.png", "page_1.png", "Page_3.png", "page_020.png", "a2.png", "a10.png", "a1.png" };
    Array.Sort(names, NaturalSortComparer.Instance);
    string[] expected =
    {
        "a1.png", "a2.png", "a10.png",
        "Page_3.png", "page_1.png", "page_2.png", "page_10.png", "page_020.png"
    };
    for (int i = 0; i < expected.Length; i++)
        AssertEqual(expected[i], names[i], $"自然排序[{i}]");
}

static async Task TestLayout()
{
    var pages = new List<ImagePage>
    {
        new(0, "a", "a", 100, 200),
        new(1, "b", "b", 100, 300),
        new(2, "c", "c", 50, 100),
    };
    var layout = new WebtoonLayout(pages, viewportWidth: 200, gap: 10);

    // 适应宽度 200：p0=400 高, p1=600 高, p2=400 高
    AssertEqual(0.0, layout.PageTop(0), "p0.top");
    AssertEqual(400.0, layout.PageBottom(0), "p0.bottom");
    AssertEqual(410.0, layout.PageTop(1), "p1.top");
    AssertEqual(1010.0, layout.PageBottom(1), "p1.bottom");
    AssertEqual(1020.0, layout.PageTop(2), "p2.top");
    AssertEqual(1420.0, layout.PageBottom(2), "p2.bottom");
    AssertEqual(1420.0, layout.TotalHeight, "total");

    AssertEqual(0, layout.FindPageAt(0), "find@0");
    AssertEqual(0, layout.FindPageAt(400), "find@400(页尾)");
    AssertEqual(0, layout.FindPageAt(405), "find@405(间隙)");
    AssertEqual(1, layout.FindPageAt(410), "find@410");
    AssertEqual(1, layout.FindPageAt(1010), "find@1010(页尾)");
    AssertEqual(2, layout.FindPageAt(1020), "find@1020");
    AssertEqual(2, layout.FindPageAt(1419), "find@1419");

    CollectionAssert(new[] { 0, 1 }, layout.PagesIntersecting(0, 500).ToArray(), "相交页[0,500)");
    CollectionAssert(new[] { 1 }, layout.PagesIntersecting(450, 500).ToArray(), "相交页[450,500)");
    CollectionAssert(new[] { 2 }, layout.PagesIntersecting(1020, 1220).ToArray(), "相交页[1020,1220)");
}

static async Task TestAnchorRoundTrip()
{
    var pages = new List<ImagePage> { new(0, "a", "a", 100, 200), new(1, "b", "b", 100, 200) };
    var layout = new WebtoonLayout(pages, viewportWidth: 200, gap: 0);
    // p1: 400-800
    double offset = 555;
    ScrollAnchor anchor = layout.GetAnchor(offset);
    AssertEqual(1, anchor.PageIndex, "anchor.page");
    AssertEqual(155.0, anchor.InPageOffset, "anchor.offset");
    AssertEqual(offset, layout.OffsetFromAnchor(anchor), "anchor 往返");

    // 宽度变化后按页内比例恢复（0.5 -> p1 中点）
    var layout2 = new WebtoonLayout(pages, viewportWidth: 400, gap: 0); // p1: 800-1600
    double target = layout2.PageTop(1) + 0.5 * layout2.PageHeight(1);
    AssertEqual(1200.0, target, "宽度变化后的视觉锚点");
}

static async Task TestLruCache()
{
    var cache = new LruCache<string>(capacityBytes: 100);
    List<string> evicted = new();
    cache.ItemEvicted += (_, v, _) => evicted.Add(v);

    cache.Put("a", "A", 40);
    cache.Put("b", "B", 40);
    AssertEqual(2, cache.Count, "count@2");
    AssertTrue(cache.TryGet("a", out _), "a 应存在");
    cache.Put("c", "C", 40); // 40+40+40=120 > 100，淘汰最久未用的 b
    AssertEqual(2, cache.Count, "count@3(淘汰后)");
    AssertTrue(cache.TryGet("a", out var a) && a == "A", "a 存活");
    AssertTrue(!cache.TryGet("b", out _), "b 应被淘汰");
    AssertTrue(cache.TryGet("c", out var c) && c == "C", "c 存活");
    CollectionAssert(new[] { "B" }, evicted.ToArray(), "淘汰事件");

    cache.Put("d", "D", 200); // 单项超预算
    AssertEqual(2, cache.Count, "单项超预算不缓存");
}

static async Task TestSchedulerPriority()
{
    var worker = new ManualWorker();
    using var scheduler = new DecodeScheduler(worker, maxConcurrent: 1);

    _ = scheduler.RequestAsync(new DecodeRequest("k1", 0, "p1", 800, DecodePriority.Viewport));
    _ = scheduler.RequestAsync(new DecodeRequest("k2", 1, "p2", 800, DecodePriority.Preload));
    _ = scheduler.RequestAsync(new DecodeRequest("k3", 2, "p3", 800, DecodePriority.Viewport));

    await worker.WaitStartedAsync(1);
    AssertTrue(worker.Started.Count == 1, "并发 1 时只应启动 1 个");

    await worker.CompleteAsync("k1"); // 释放后应先启动 k3（视口优先于预加载）
    await worker.WaitStartedAsync(2);
    AssertEqual("k3", worker.Started[1], "第二个启动的任务");

    await worker.CompleteAsync("k3");
    await worker.WaitStartedAsync(3);
    AssertEqual("k2", worker.Started[2], "第三个启动的任务");

    await worker.CompleteAsync("k2");
}

static async Task TestSchedulerDedupAndUpgrade()
{
    var worker = new ManualWorker();
    using var scheduler = new DecodeScheduler(worker, maxConcurrent: 2);

    var t1 = scheduler.RequestAsync(new DecodeRequest("k1", 0, "p1", 800, DecodePriority.Viewport));
    await worker.WaitStartedAsync(1);

    // k1 在途：再次请求同 Key 应复用同一任务
    var t1b = scheduler.RequestAsync(new DecodeRequest("k1", 0, "p1", 800, DecodePriority.Viewport));
    AssertTrue(ReferenceEquals(t1, t1b), "在途任务复用");
    await worker.WaitStartedAsync(1);
    AssertEqual(1, worker.Started.Count, "worker 只收到一次 k1");

    // k2 以低优先级排队后升级为视口优先级，应先于同为视口优先级但更晚入队的 k3
    _ = scheduler.RequestAsync(new DecodeRequest("k2", 1, "p2", 800, DecodePriority.Preload));
    _ = scheduler.RequestAsync(new DecodeRequest("k3", 2, "p3", 800, DecodePriority.Viewport));
    _ = scheduler.RequestAsync(new DecodeRequest("k2", 1, "p2", 800, DecodePriority.Viewport)); // 升级

    await worker.CompleteAsync("k1");
    await worker.WaitStartedAsync(3);
    AssertEqual("k2", worker.Started[1], "升级后的 k2 先启动");

    await worker.CompleteAsync("k2");
    await worker.CompleteAsync("k3");
}

static async Task TestVirtualScrollSimulation()
{
    // 模拟 App 的虚拟滚动：可见页 + 相邻预加载；LRU 预算仅容纳 ~8 页
    const int pageCount = 500;
    const double viewportWidth = 800, viewportHeight = 900, step = 700;
    var pages = Enumerable.Range(0, pageCount)
        .Select(i => new ImagePage(i, $"p{i}.jpg", $"page_{i}.jpg", 800, 2200))
        .ToList();
    var layout = new WebtoonLayout(pages, viewportWidth, gap: 0);
    AssertEqual(2200.0 * pageCount, layout.TotalHeight, "总高度");

    var worker = new CountingWorker();
    using var scheduler = new DecodeScheduler(worker, maxConcurrent: 2);
    var cache = new LruCache<object>(8L * 800 * 2200 * 4);
    var tasks = new List<Task>();

    var sw = Stopwatch.StartNew();
    for (double offset = 0; offset < layout.TotalHeight - 1; offset += step)
    {
        int[] visible = layout.PagesIntersecting(offset, offset + viewportHeight).ToArray();
        foreach (int i in visible)
            tasks.Add(Request(scheduler, cache, pages, i, (int)viewportWidth, DecodePriority.Viewport));
        if (visible.Length > 0)
        {
            if (visible[0] > 0)
                tasks.Add(Request(scheduler, cache, pages, visible[0] - 1, (int)viewportWidth, DecodePriority.Preload));
            if (visible[^1] < pageCount - 1)
                tasks.Add(Request(scheduler, cache, pages, visible[^1] + 1, (int)viewportWidth, DecodePriority.Preload));
        }
    }
    await Task.WhenAll(tasks);
    sw.Stop();

    Console.WriteLine($"    滚动 {pageCount} 页(总高 {layout.TotalHeight:F0}px): " +
        $"唯一解码 {worker.UniqueDecoded} 页, 末态缓存 {cache.Count} 页, 耗时 {sw.ElapsedMilliseconds}ms");
    AssertEqual(pageCount, worker.UniqueDecoded, "全程滚动覆盖所有页");
    AssertTrue(cache.Count <= 9, $"缓存常驻应保持小集合, 实际 {cache.Count}");
}

static async Task TestJumpVirtualization()
{
    const int pageCount = 500;
    var pages = Enumerable.Range(0, pageCount)
        .Select(i => new ImagePage(i, $"p{i}.jpg", $"page_{i}.jpg", 800, 2200))
        .ToList();
    var layout = new WebtoonLayout(pages, viewportWidth: 800, gap: 0);

    var worker = new CountingWorker();
    using var scheduler = new DecodeScheduler(worker, maxConcurrent: 2);
    var cache = new LruCache<object>(8L * 800 * 2200 * 4);
    var tasks = new List<Task>();

    double target = layout.PageTop(299); // 直接跳转到第 300 页
    foreach (int i in layout.PagesIntersecting(target, target + 900))
        tasks.Add(Request(scheduler, cache, pages, i, 800, DecodePriority.Viewport));

    await Task.WhenAll(tasks);

    Console.WriteLine($"    跳转到第 300 页: 唯一解码 {worker.UniqueDecoded} 页");
    AssertTrue(worker.UniqueDecoded <= 2, $"跳转只应解码视口页, 实际 {worker.UniqueDecoded}");
}

static Task Request(DecodeScheduler scheduler, LruCache<object> cache, List<ImagePage> pages,
    int index, int viewportWidth, DecodePriority priority)
{
    var page = pages[index];
    int decodeWidth = Math.Min(viewportWidth, page.Width);
    string key = $"p{index}:w{decodeWidth}";
    if (cache.TryGet(key, out _)) return Task.CompletedTask;
    var task = scheduler.RequestAsync(new DecodeRequest(key, index, page.Path, decodeWidth, priority));
    _ = task.ContinueWith(t =>
    {
        if (t is { IsCompletedSuccessfully: true, Result: not null })
            cache.Put(key, t.Result, (long)decodeWidth * 2200 * 4);
    });
    return task;
}

static void CollectionAssert<T>(T[] expected, T[] actual, string ctx)
{
    if (expected.Length != actual.Length || !expected.SequenceEqual(actual))
        throw new Exception($"{ctx} expected=[{string.Join(",", expected)}] actual=[{string.Join(",", actual)}]");
}

sealed class ManualWorker : IDecodeWorker
{
    private readonly object _gate = new();
    private readonly List<(string Key, TaskCompletionSource<object?> Tcs)> _items = new();
    public List<string> Started { get; } = new();

    public Task<object?> DecodeAsync(DecodeRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _items.Add((request.Key, tcs));
            Started.Add(request.Key);
            Monitor.PulseAll(_gate);
        }
        return tcs.Task;
    }

    public async Task WaitStartedAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate) if (Started.Count >= count) return;
            await Task.Delay(5);
        }
        throw new TimeoutException($"等待 {count} 个任务启动超时");
    }

    public Task CompleteAsync(string key)
    {
        TaskCompletionSource<object?>? tcs;
        lock (_gate)
        {
            tcs = _items.FirstOrDefault(i => i.Key == key).Tcs;
        }
        if (tcs is null) throw new Exception($"未找到在途任务 {key}");
        tcs.TrySetResult(null);
        return Task.CompletedTask;
    }
}

sealed class CountingWorker : IDecodeWorker
{
    private readonly HashSet<string> _decoded = new();
    public int UniqueDecoded { get { lock (_decoded) return _decoded.Count; } }

    public Task<object?> DecodeAsync(DecodeRequest request, CancellationToken ct)
    {
        lock (_decoded) _decoded.Add(request.Key);
        return Task.FromResult<object?>(new object());
    }
}


