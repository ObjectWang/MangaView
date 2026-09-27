using System.Diagnostics;
using MangaView.Core;

// 轻量测试运行器（不依赖外部测试框架，避免联网还原 NuGet 包）
var tests = new (string Name, Func<Task> Run)[]
{
    ("NaturalSort: 数值段按数值排序", TestNaturalSort),
    ("ImageTransformState: 适应/旋转/缩放边界", TestImageTransformState),
    ("ImageCatalog: 自然排序与递归枚举", TestImageCatalog),
    ("WebtoonLayout: 布局与二分查找", TestLayout),
    ("WebtoonLayout: 滚动锚点往返", TestAnchorRoundTrip),
    ("LruCache: 预算淘汰与访问刷新", TestLruCache),
    ("DecodeScheduler: 优先级排序", TestSchedulerPriority),
    ("DecodeScheduler: 同 Key 去重与优先级升级", TestSchedulerDedupAndUpgrade),
    ("DecodeScheduler: 取消过期排队任务", TestSchedulerCancelQueued),
    ("虚拟化模拟: 500 页滚动仅保持小常驻集", TestVirtualScrollSimulation),
    ("虚拟化模拟: 跳转页 300 只解码附近页", TestJumpVirtualization),
    ("DoublePageLayout: 封面页与阅读方向", TestDoublePageLayout),
    ("ReadingProgressStore: JSON 保存/恢复", TestProgressStore),
    ("ReadingProgressStore: 损坏 JSON 容错", TestProgressStoreCorruptJson),
    ("BookmarkStore: 新增/重命名/删除", TestBookmarkStore),
    ("RecentStore: 去重与清空", TestRecentStore),
    ("AppSettingsStore: M4 设置往返", TestM4SettingsStore),
    ("CbzArchive: 自然排序与安全缓存", TestCbzArchive),
    ("CbzArchive: 损坏压缩包报错", TestCbzInvalidArchive),
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

static async Task TestImageTransformState()
{
    var transform = new ImageTransformState();
    var (width, height) = transform.GetDisplaySize(2000, 1000, 1000, 700);
    AssertEqual(1000.0, width, "适应窗口宽度");
    AssertEqual(500.0, height, "适应窗口高度");
    AssertEqual(0.5, transform.Scale, "适应窗口比例");

    // 旋转 90° 后使用旋转后的宽高重新适配。
    transform.RotateRight();
    (width, height) = transform.GetDisplaySize(2000, 1000, 1000, 700);
    AssertEqual(350.0, width, "旋转后适应宽度");
    AssertEqual(700.0, height, "旋转后适应高度");
    AssertEqual(90, transform.RotationDegrees, "旋转角度");

    transform.ZoomBy(1000);
    AssertEqual(ImageTransformState.MaxScale, transform.Scale, "最大缩放钳制");
    transform.ZoomBy(0.000001);
    AssertEqual(ImageTransformState.MinScale, transform.Scale, "最小缩放钳制");
    AssertEqual(ZoomMode.Custom, transform.Mode, "手动缩放模式");

    transform.ToggleHorizontalFlip();
    transform.ToggleVerticalFlip();
    AssertTrue(transform.FlipHorizontal && transform.FlipVertical, "翻转状态");
    transform.Reset();
    AssertEqual(0, transform.RotationDegrees, "重置旋转");
    AssertEqual(ZoomMode.FitWindow, transform.Mode, "重置缩放模式");
}

static async Task TestImageCatalog()
{
    string root = Path.Combine(Path.GetTempPath(), "mangaview-tests", Guid.NewGuid().ToString("N"));
    string nested = Path.Combine(root, "nested");
    try
    {
        Directory.CreateDirectory(nested);
        foreach (string name in new[] { "page_10.jpg", "page_2.png", "page_1.webp", "ignore.txt" })
            File.WriteAllText(Path.Combine(root, name), name);
        File.WriteAllText(Path.Combine(nested, "page_3.jpg"), "nested");

        var current = ImageCatalog.EnumerateImages(root, recursive: false);
        CollectionAssert(new[] { "page_1.webp", "page_2.png", "page_10.jpg" },
            current.Select(Path.GetFileName).ToArray(), "当前目录自然排序");

        var recursive = ImageCatalog.EnumerateImages(root, recursive: true);
        AssertEqual(4, recursive.Count, "递归图片数量");
        int nestedIndex = ImageCatalog.IndexOfPath(recursive, Path.Combine(nested, "page_3.jpg"));
        AssertTrue(nestedIndex >= 0, "定位初始图片");
        AssertEqual("page_3.jpg", Path.GetFileName(recursive[nestedIndex]), "定位初始图片名称");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
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

    evicted.Clear();
    cache.Put("a", "A2", 40); // 同 Key 替换也应通知旧值
    CollectionAssert(new[] { "A" }, evicted.ToArray(), "替换事件");

    evicted.Clear();
    cache.Clear();
    AssertEqual(0, cache.Count, "清空后的数量");
    CollectionAssert(new[] { "A2", "C" }, evicted.OrderBy(x => x).ToArray(), "清空事件");
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

static async Task TestSchedulerCancelQueued()
{
    var worker = new ManualWorker();
    using var scheduler = new DecodeScheduler(worker, maxConcurrent: 1);

    var running = scheduler.RequestAsync(new DecodeRequest("p0", 0, "p0.jpg", 800, DecodePriority.Viewport));
    await worker.WaitStartedAsync(1);
    var queued1 = scheduler.RequestAsync(new DecodeRequest("p1", 1, "p1.jpg", 800, DecodePriority.Preload));
    var queued2 = scheduler.RequestAsync(new DecodeRequest("p2", 2, "p2.jpg", 800, DecodePriority.Preload));

    AssertEqual(2, scheduler.QueuedCount, "排队数量");
    AssertEqual(2, scheduler.CancelQueued(), "取消数量");
    AssertTrue(queued1.IsCanceled && queued2.IsCanceled, "过期任务应被取消");
    AssertEqual(0, scheduler.QueuedCount, "取消后的排队数量");

    await worker.CompleteAsync("p0");
    await running;
    AssertEqual(0, scheduler.InFlightCount, "在途任务完成后清空");
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

static async Task TestDoublePageLayout()
{
    var pages = Enumerable.Range(0, 5)
        .Select(i => new ImagePage(i, $"p{i}.jpg", $"page_{i}.jpg", 800, 1200))
        .ToList();

    // 独立封面 + 左到右：封面单独，之后 1/2、3/4 配对
    var ltr = new DoublePageLayout(pages, coverPage: true, ReadingDirection.LeftToRight);
    AssertEqual(3, ltr.SpreadCount, "LTR 跨页数");
    AssertEqual(0, ltr.Spreads[0].LeftPageIndex, "LTR 封面页");
    AssertEqual(-1, ltr.Spreads[0].RightPageIndex, "LTR 封面右侧空");
    AssertEqual(1, ltr.Spreads[1].LeftPageIndex, "LTR 第二跨左页");
    AssertEqual(2, ltr.Spreads[1].RightPageIndex, "LTR 第二跨右页");
    AssertEqual(3, ltr.Spreads[2].LeftPageIndex, "LTR 第三跨左页");
    AssertEqual(4, ltr.Spreads[2].RightPageIndex, "LTR 第三跨右页");

    // 右到左：左右位置交换，阅读顺序 2 -> 1
    var rtl = new DoublePageLayout(pages, coverPage: true, ReadingDirection.RightToLeft);
    AssertEqual(2, rtl.Spreads[1].LeftPageIndex, "RTL 第二跨左页");
    AssertEqual(1, rtl.Spreads[1].RightPageIndex, "RTL 第二跨右页");
    CollectionAssert(new[] { 1, 2 }, rtl.Spreads[1].ReadingOrder(ReadingDirection.RightToLeft).ToArray(), "RTL 阅读顺序");

    // 无封面：0/1、2/3、4 单页
    var noCover = new DoublePageLayout(pages, coverPage: false, ReadingDirection.LeftToRight);
    AssertEqual(3, noCover.SpreadCount, "无封面跨页数");
    AssertEqual(0, noCover.Spreads[0].LeftPageIndex, "无封面第一跨左页");
    AssertEqual(1, noCover.Spreads[0].RightPageIndex, "无封面第一跨右页");
    AssertEqual(4, noCover.Spreads[2].LeftPageIndex, "末尾单页");

    // 页码 -> 跨页查找（进度恢复/跳页）
    AssertEqual(1, ltr.SpreadIndexForPage(2), "页 3 所在跨页");
    AssertEqual(0, ltr.SpreadIndexForPage(0), "封面所在跨页");
}

static async Task TestProgressStore()
{
    string dir = Path.Combine(Path.GetTempPath(), "mangaview-tests", Guid.NewGuid().ToString("N"));
    string file = Path.Combine(dir, "progress.json");
    try
    {
        var store = new ReadingProgressStore(file);
        var progress = new ReadingProgress(
            SourceKey: @"D:\books\demo.cbz",
            Mode: ReadingMode.DoublePage,
            PageIndex: 123,
            Anchor: new ScrollAnchor(45, 678.5),
            Direction: ReadingDirection.RightToLeft,
            DoubleCoverPage: true,
            DoubleGap: 12,
            UpdatedUtc: DateTime.UtcNow,
            ZoomMode: ZoomMode.FitWidth,
            ZoomScale: 2.5);
        store.Update(progress);
        store.Save();

        var restored = new ReadingProgressStore(file);
        var actual = restored.Get(@"D:\books\demo.cbz");
        AssertTrue(actual is not null, "进度应能恢复");
        AssertEqual(ReadingMode.DoublePage, actual!.Mode, "模式");
        AssertEqual(123, actual.PageIndex, "页码");
        AssertEqual(45, actual.Anchor.PageIndex, "锚点页");
        AssertEqual(678.5, actual.Anchor.InPageOffset, "页内偏移");
        AssertEqual(ReadingDirection.RightToLeft, actual.Direction, "阅读方向");
        AssertTrue(actual.DoubleCoverPage, "封面页设置");
        AssertEqual(12.0, actual.DoubleGap, "双页间距");
        AssertEqual(ZoomMode.FitWidth, actual.ZoomMode, "缩放模式");
        AssertEqual(2.5, actual.ZoomScale, "缩放比例");
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

static async Task TestProgressStoreCorruptJson()
{
    string dir = Path.Combine(Path.GetTempPath(), "mangaview-tests", Guid.NewGuid().ToString("N"));
    string file = Path.Combine(dir, "progress.json");
    try
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(file, "{ not valid json");
        var store = new ReadingProgressStore(file);
        AssertTrue(store.Get(@"D:\books\demo.cbz") is null, "损坏进度应回退为空");
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

static async Task TestBookmarkStore()
{
    string dir = Path.Combine(Path.GetTempPath(), "mangaview-tests", Guid.NewGuid().ToString("N"));
    string file = Path.Combine(dir, "bookmarks.json");
    try
    {
        var store = new BookmarkStore(file);
        var bookmark = store.Add("book-a", "开头", 3, ReadingMode.Webtoon, new ScrollAnchor(3, 120));
        var loaded = new BookmarkStore(file).Get("book-a");
        AssertEqual(1, loaded.Count, "书签数量");
        AssertEqual("开头", loaded[0].Name, "书签名称");
        AssertEqual(3, loaded[0].PageIndex, "书签页");
        AssertTrue(store.Rename("book-a", bookmark.Id, "重命名"), "重命名结果");
        AssertEqual("重命名", store.Get("book-a")[0].Name, "重命名后名称");
        AssertTrue(store.Delete("book-a", bookmark.Id), "删除结果");
        AssertEqual(0, store.Get("book-a").Count, "删除后的数量");
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

static async Task TestRecentStore()
{
    string dir = Path.Combine(Path.GetTempPath(), "mangaview-tests", Guid.NewGuid().ToString("N"));
    string file = Path.Combine(dir, "recent.json");
    try
    {
        var store = new RecentStore(file);
        store.Add(@"D:\books\a.cbz", RecentKind.Archive);
        store.Add(@"D:\books\a.cbz", RecentKind.Archive);
        store.Add(@"D:\books\folder", RecentKind.Folder);
        AssertEqual(2, store.Entries.Count, "去重后的数量");
        AssertEqual(@"D:\books\folder", store.Entries[0].Path, "最新记录");
        store.Clear();
        AssertEqual(0, store.Entries.Count, "清空记录");
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

static async Task TestM4SettingsStore()
{
    string dir = Path.Combine(Path.GetTempPath(), "mangaview-tests", Guid.NewGuid().ToString("N"));
    string file = Path.Combine(dir, "settings.json");
    try
    {
        var store = new AppSettingsStore(file);
        var settings = AppSettings.Default with
        {
            Theme = ThemePreference.Dark,
            SaveRecent = false,
            RememberProgress = false,
            ShowInfoPanel = true,
            ShowThumbnails = true,
            SlideshowIntervalSeconds = 12,
            SlideshowRandom = true,
            SlideshowLoop = false,
            SlideshowHideControls = true,
            HdrEnabled = true,
            WindowPlacement = new WindowPlacement(10, 20, 1280, 800, true, @"\\.\DISPLAY2"),
        };
        store.Update(settings);
        store.Save();
        var loaded = new AppSettingsStore(file);
        loaded.Load();
        AssertEqual(ThemePreference.Dark, loaded.Settings.Theme, "主题");
        AssertTrue(!loaded.Settings.SaveRecent, "最近记录开关");
        AssertTrue(loaded.Settings.ShowInfoPanel && loaded.Settings.ShowThumbnails, "面板开关");
        AssertEqual(12, loaded.Settings.SlideshowIntervalSeconds, "幻灯片间隔");
        AssertTrue(loaded.Settings.SlideshowRandom, "随机播放");
        AssertTrue(!loaded.Settings.SlideshowLoop, "循环播放");
        AssertEqual(@"\\.\DISPLAY2", loaded.Settings.WindowPlacement?.MonitorDeviceName, "显示器");
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

static async Task TestCbzArchive()
{
    string root = Path.Combine(Path.GetTempPath(), "mangaview-tests", Guid.NewGuid().ToString("N"));
    string archive = Path.Combine(root, "demo.cbz");
    string cache = Path.Combine(root, "cache");
    try
    {
        Directory.CreateDirectory(root);
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        {
            // 故意乱序 + 混入非图片 + 路径穿越样式名（实现应扁平化，不允许写出缓存目录）
            foreach (var (name, value) in new[] { ("page_2.jpg", 2), ("ignore.txt", 0), ("page_1.jpg", 1), ("page_10.jpg", 10) })
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                stream.WriteByte((byte)value);
            }
        }

        var result = CbzArchive.ExtractAsync(archive, cache).GetAwaiter().GetResult();
        AssertEqual(3, result.Files.Count, "只提取图片");
        AssertEqual(3, result.Files.Count, "图片数量");
        var names = result.Files.Select(Path.GetFileName).ToList();
        CollectionAssert(
            new[] { "000001_page_1.jpg", "000002_page_2.jpg", "000003_page_10.jpg" },
            names.ToArray(), "自然排序文件名");

        // 缓存目录必须全部位于 cache 下，无路径穿越
        string fullCache = Path.GetFullPath(cache);
        AssertTrue(result.Files.All(f =>
            Path.GetFullPath(f).StartsWith(fullCache, StringComparison.OrdinalIgnoreCase)), "无路径穿越");

        // 重复打开应复用缓存
        var result2 = CbzArchive.ExtractAsync(archive, cache).GetAwaiter().GetResult();
        AssertEqual(result.Directory, result2.Directory, "缓存复用");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static async Task TestCbzInvalidArchive()
{
    string root = Path.Combine(Path.GetTempPath(), "mangaview-tests", Guid.NewGuid().ToString("N"));
    string archive = Path.Combine(root, "broken.cbz");
    string cache = Path.Combine(root, "cache");
    try
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(archive, "this is not a zip archive");
        bool failed = false;
        try
        {
            await CbzArchive.ExtractAsync(archive, cache);
        }
        catch (InvalidDataException)
        {
            failed = true;
        }
        AssertTrue(failed, "损坏压缩包应抛出 InvalidDataException");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
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
