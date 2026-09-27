using MangaView.Core;
using System.IO;
using System.Windows.Media.Imaging;

namespace MangaView.App;

/// <summary>
/// WPF 解码 Worker：按“适应宽度”的目标宽度用 WIC 解码（DecodePixelWidth），
/// 从源头降低长图内存占用；返回的位图已 Freeze，可跨线程使用。
/// </summary>
public sealed class WpfDecodeWorker : IDecodeWorker
{
    public Task<object?> DecodeAsync(DecodeRequest request, CancellationToken cancellationToken)
    {
        return Task.Run(() => (object?)Decode(request, cancellationToken), cancellationToken);
    }

    private static object Decode(DecodeRequest request, CancellationToken cancellationToken)
    {
        if (IsHeif(request.Path))
        {
            try
            {
                return HeifImageDecoder.Decode(request.Path, request.DecodeWidth, cancellationToken);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // 某些 HEIF 变体可能由系统 WIC 扩展处理，继续走通用回退链。
            }
        }

        try
        {
            AnimatedImageSource? animated = AnimatedImageSource.TryLoad(request.Path, cancellationToken);
            if (animated is not null) return animated;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (request.DecodeWidth > 0)
                image.DecodePixelWidth = request.DecodeWidth;
            image.UriSource = new Uri(request.Path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // WIC 对 HEIC/PSD/RAW 等依赖系统扩展；缺失时统一回退到 Magick.NET。
            return MagickImageDecoder.Decode(request.Path, request.DecodeWidth, cancellationToken);
        }
    }

    /// <summary>仅解析图片头获取宽高（DelayCreation 不解码像素），用于构建虚拟滚动布局。</summary>
    public static (int Width, int Height) ProbeSize(string path)
    {
        if (IsHeif(path))
        {
            try
            {
                return HeifImageDecoder.ProbeSize(path);
            }
            catch
            {
                // 继续尝试 WIC / Magick。
            }
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, FileOptions.Asynchronous);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return MagickImageDecoder.ProbeSize(path, CancellationToken.None);
        }
    }

    private static bool IsHeif(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);
    }
}
