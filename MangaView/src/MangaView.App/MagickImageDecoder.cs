using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace MangaView.App;

/// <summary>
/// Magick.NET 后备解码器：用于 WIC 不可用的 AVIF/HEIC、PSD/PSB 与常见 RAW 容器。
/// 输出统一转换为 WPF Bgra32 位图，并在后台线程完成后冻结。
/// </summary>
public static class MagickImageDecoder
{
    public static (int Width, int Height) ProbeSize(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var info = new MagickImageInfo(path);
            return (checked((int)info.Width), checked((int)info.Height));
        }
        catch
        {
            using var image = new MagickImage(path);
            return (checked((int)image.Width), checked((int)image.Height));
        }
    }

    public static BitmapSource Decode(string path, int decodeWidth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var image = new MagickImage(path);
        image.AutoOrient();

        if (decodeWidth > 0 && image.Width > (uint)decodeWidth)
        {
            uint targetWidth = (uint)decodeWidth;
            uint targetHeight = (uint)Math.Max(1,
                Math.Round((double)image.Height * targetWidth / image.Width));
            image.Resize(targetWidth, targetHeight);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (image.ColorSpace != ColorSpace.sRGB) image.ColorSpace = ColorSpace.sRGB;
        if (!image.HasAlpha) image.Alpha(AlphaOption.Opaque);

        int width = checked((int)image.Width);
        int height = checked((int)image.Height);
        byte[] pixels = image.GetPixels().ToByteArray(PixelMapping.BGRA)
            ?? throw new InvalidOperationException("无法读取图像像素。");
        int stride = checked(width * 4);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
