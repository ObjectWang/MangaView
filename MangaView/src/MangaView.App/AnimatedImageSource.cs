using System.IO;
using System.Windows.Media.Imaging;

namespace MangaView.App;

/// <summary>
/// 已冻结的动画图片帧集合。使用后台计时器按 GIF 帧延迟循环切换，
/// 渲染层只需订阅 FrameChanged 并在 UI 线程重绘。
/// </summary>
public sealed class AnimatedImageSource : IDisposable
{
    private readonly BitmapSource[] _frames;
    private readonly TimeSpan[] _delays;
    private readonly Timer _timer;
    private int _frameIndex;
    private bool _disposed;

    public event Action? FrameChanged;

    public int FrameCount => _frames.Length;
    public BitmapSource CurrentFrame => _frames[Volatile.Read(ref _frameIndex)];
    public long EstimatedBytes
    {
        get
        {
            var frame = _frames[0];
            return (long)Math.Max(1, frame.PixelWidth) * Math.Max(1, frame.PixelHeight) * 4;
        }
    }

    private AnimatedImageSource(BitmapSource[] frames, TimeSpan[] delays)
    {
        _frames = frames;
        _delays = delays;
        _timer = new Timer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ScheduleNext();
    }

    public static AnimatedImageSource? TryLoad(string path, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
            return null;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.SequentialScan);
        var decoder = new GifBitmapDecoder(stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreImageCache,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count <= 1) return null;

        var frames = new List<BitmapSource>(decoder.Frames.Count);
        var delays = new List<TimeSpan>(decoder.Frames.Count);
        foreach (var sourceFrame in decoder.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BitmapSource frame = sourceFrame;
            if (frame.CanFreeze) frame.Freeze();
            frames.Add(frame);
            delays.Add(ReadDelay(sourceFrame));
        }
        return new AnimatedImageSource(frames.ToArray(), delays.ToArray());
    }

    private static TimeSpan ReadDelay(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata &&
                metadata.GetQuery("/grctlext/Delay") is ushort delay && delay > 0)
                return TimeSpan.FromMilliseconds(Math.Clamp(delay * 10, 20, 10000));
        }
        catch
        {
        }
        return TimeSpan.FromMilliseconds(100);
    }

    private void ScheduleNext()
    {
        if (_disposed) return;
        int next = (Volatile.Read(ref _frameIndex) + 1) % _frames.Length;
        _timer.Change(_delays[next], Timeout.InfiniteTimeSpan);
    }

    private void OnTick(object? state)
    {
        if (_disposed) return;
        Volatile.Write(ref _frameIndex, (Volatile.Read(ref _frameIndex) + 1) % _frames.Length);
        FrameChanged?.Invoke();
        ScheduleNext();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        FrameChanged = null;
    }
}
