using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using MangaView.App;
using MangaView.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("格式目录: M3 扩展名", TestCatalogExtensions),
    ("Magick.NET: PSD 解码", () => TestRasterFormat("psd")),
    ("Magick.NET: AVIF 解码", () => TestRasterFormat("avif")),
    ("libheif: HEIC 解码", TestHeicDecode),
    ("Magick.NET: WebP 解码", () => TestRasterFormat("webp")),
    ("Magick.NET: Animated WebP", TestAnimatedWebP),
    ("Magick.NET: RAW 预览解码", TestRawDecode),
    ("WebtoonCanvas: Ctrl 滚轮缩放锚点", TestWebtoonCanvasZoom),
};

int failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"[PASS] {name}");
    }
    catch (SkipTestException ex)
    {
        Console.WriteLine($"[SKIP] {name}: {ex.Message}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"[FAIL] {name}: {ex.Message}");
    }
}

Console.WriteLine(failed == 0 ? $"\n格式测试完成（{tests.Length} 项）。" : $"\n{failed} 项格式测试失败。");
return failed == 0 ? 0 : 1;

static void AssertTrue(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task TestCatalogExtensions()
{
    foreach (string extension in new[] { ".avif", ".webp", ".heic", ".heif", ".psd", ".psb", ".dng", ".cr2", ".nef", ".arw" })
        AssertTrue(ImageCatalog.IsSupportedImage("sample" + extension), $"应支持 {extension}");
    await Task.CompletedTask;
}

static async Task TestRasterFormat(string extension)
{
    string directory = Path.Combine(Path.GetTempPath(), "mangaview-format-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "sample." + extension);
    try
    {
        try
        {
            using var image = new MagickImage(MagickColors.Red, 320, 240);
            image.Write(path);
        }
        catch (MagickException ex)
        {
            throw new SkipTestException($"Magick.NET 无法生成 {extension} 样本：{ex.Message}");
        }

        var (width, height) = WpfDecodeWorker.ProbeSize(path);
        AssertTrue(width == 320 && height == 240, $"尺寸错误：{width}x{height}");
        var worker = new WpfDecodeWorker();
        object? decoded = await worker.DecodeAsync(
            new DecodeRequest("format-" + extension, 0, path, 0, DecodePriority.Viewport), CancellationToken.None);
        AssertTrue(decoded is BitmapSource, "解码结果不是 BitmapSource");
        AssertTrue(((BitmapSource)decoded!).PixelWidth == 320, "解码宽度错误");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

static async Task TestAnimatedWebP()
{
    string directory = Path.Combine(Path.GetTempPath(), "mangaview-format-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "animated.webp");
    try
    {
        try
        {
            using var collection = new MagickImageCollection();
            var first = new MagickImage(MagickColors.Red, 64, 64) { AnimationDelay = 10 };
            var second = new MagickImage(MagickColors.Blue, 64, 64) { AnimationDelay = 10 };
            collection.Add(first);
            collection.Add(second);
            collection.Write(path);
        }
        catch (MagickException ex)
        {
            throw new SkipTestException($"Magick.NET 无法生成 Animated WebP：{ex.Message}");
        }

        var worker = new WpfDecodeWorker();
        object? decoded = await worker.DecodeAsync(
            new DecodeRequest("animated-webp", 0, path, 0, DecodePriority.Viewport), CancellationToken.None);
        AssertTrue(decoded is AnimatedImageSource, "应返回动画帧源");
        var animated = (AnimatedImageSource)decoded!;
        AssertTrue(animated.FrameCount >= 2, "动画帧数不足");
        AssertTrue(animated.CurrentFrame.PixelWidth == 64, "动画帧宽度错误");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

static async Task TestHeicDecode()
{
    string? sample = Environment.GetEnvironmentVariable("MANGAVIEW_HEIC_SAMPLE");
    if (string.IsNullOrWhiteSpace(sample) || !File.Exists(sample))
        throw new SkipTestException("未设置 MANGAVIEW_HEIC_SAMPLE，跳过外部 HEIC 样本测试");

    var (width, height) = WpfDecodeWorker.ProbeSize(sample);
    AssertTrue(width > 0 && height > 0, "HEIC 尺寸无效");
    var direct = HeifImageDecoder.Decode(sample, 0, CancellationToken.None);
    AssertTrue(direct.PixelWidth == width && direct.PixelHeight == height,
        $"libheif 直接解码尺寸错误：probe={width}x{height} decoded={direct.PixelWidth}x{direct.PixelHeight}");
    var worker = new WpfDecodeWorker();
    object? decoded = await worker.DecodeAsync(
        new DecodeRequest("heic-sample", 0, sample, 0, DecodePriority.Viewport), CancellationToken.None);
    AssertTrue(decoded is BitmapSource, "HEIC 解码结果不是 BitmapSource");
    var bitmap = (BitmapSource)decoded!;
    AssertTrue(bitmap.PixelWidth == width && bitmap.PixelHeight == height,
        $"HEIC 解码尺寸错误：probe={width}x{height} decoded={bitmap.PixelWidth}x{bitmap.PixelHeight}");
}

static async Task TestRawDecode()
{
    string? sample = Environment.GetEnvironmentVariable("MANGAVIEW_RAW_SAMPLE");
    if (string.IsNullOrWhiteSpace(sample) || !File.Exists(sample))
        throw new SkipTestException("未设置 MANGAVIEW_RAW_SAMPLE，跳过外部 RAW 样本测试");

    var (width, height) = WpfDecodeWorker.ProbeSize(sample);
    AssertTrue(width > 0 && height > 0, "RAW 尺寸无效");
    var worker = new WpfDecodeWorker();
    object? decoded = await worker.DecodeAsync(
        new DecodeRequest("raw-sample", 0, sample, 1200, DecodePriority.Viewport), CancellationToken.None);
    AssertTrue(decoded is BitmapSource, "RAW 解码结果不是 BitmapSource");
    var bitmap = (BitmapSource)decoded!;
    AssertTrue(bitmap.PixelWidth is > 0 and <= 1200, "RAW 解码宽度无效");
    AssertTrue(bitmap.PixelHeight > 0, "RAW 解码高度无效");
}

static Task TestWebtoonCanvasZoom()
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            var canvas = new WebtoonCanvas();
            canvas.SetViewport(800, 600);
            canvas.SetPages(new[]
            {
                new ImagePage(0, "page_1.jpg", "page_1.jpg", 800, 1200),
                new ImagePage(1, "page_2.jpg", "page_2.jpg", 800, 1200),
            });
            double originalHeight = canvas.TotalHeight;
            var offsets = canvas.ZoomAt(2.0, new Point(400, 300), 0, 0);
            AssertTrue(Math.Abs(canvas.ZoomFactor - 2.0) < 0.001, "缩放因子未生效");
            AssertTrue(canvas.TotalHeight > originalHeight * 1.9, "缩放后总高度未增加");
            AssertTrue(offsets.HorizontalOffset >= 0 && offsets.VerticalOffset >= 0, "缩放锚点偏移无效");
            canvas.ResetZoom();
            AssertTrue(Math.Abs(canvas.ZoomFactor - 1.0) < 0.001, "重置缩放失败");
            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return completion.Task;
}

sealed class SkipTestException(string message) : Exception(message);
