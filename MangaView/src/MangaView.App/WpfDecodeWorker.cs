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
        return Task.Run(() =>
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (request.DecodeWidth > 0)
                image.DecodePixelWidth = request.DecodeWidth;
            image.UriSource = new Uri(request.Path);
            image.EndInit();
            image.Freeze();
            return (object?)image;
        }, cancellationToken);
    }

    /// <summary>仅解析图片头获取宽高（DelayCreation 不解码像素），用于构建虚拟滚动布局。</summary>
    public static (int Width, int Height) ProbeSize(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 8192, FileOptions.Asynchronous);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }
}
