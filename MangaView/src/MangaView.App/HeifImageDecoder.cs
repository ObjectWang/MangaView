using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibHeifSharp;

namespace MangaView.App;

/// <summary>HEIC / HEIF 独立解码器，使用随应用分发的 libheif 原生运行时。</summary>
public static class HeifImageDecoder
{
    public static (int Width, int Height) ProbeSize(string path)
    {
        using var context = new HeifContext(path);
        using var handle = context.GetPrimaryImageHandle();
        return (handle.Width, handle.Height);
    }

    public static BitmapSource Decode(string path, int decodeWidth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var context = new HeifContext(path);
        using var handle = context.GetPrimaryImageHandle();
        int width = handle.Width;
        int height = handle.Height;
        if (width <= 0 || height <= 0) throw new InvalidDataException("HEIF 主图尺寸无效。");

        var options = new HeifDecodingOptions { ConvertHdrToEightBit = true };
        using var image = handle.Decode(HeifColorspace.Rgb, HeifChroma.InterleavedRgba32, options);
        var plane = image.GetPlane(HeifChannel.Interleaved);
        int sourceStride = plane.Stride;
        int sourceLength = checked(sourceStride * height);
        byte[] source = new byte[sourceLength];
        Marshal.Copy(plane.Scan0, source, 0, sourceLength);

        cancellationToken.ThrowIfCancellationRequested();
        byte[] bgra = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * sourceStride;
            int targetRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int sourceIndex = sourceRow + x * 4;
                int targetIndex = targetRow + x * 4;
                bgra[targetIndex] = source[sourceIndex + 2];
                bgra[targetIndex + 1] = source[sourceIndex + 1];
                bgra[targetIndex + 2] = source[sourceIndex];
                bgra[targetIndex + 3] = source[sourceIndex + 3];
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, bgra, checked(width * 4));
        bitmap.Freeze();

        if (decodeWidth <= 0 || width <= decodeWidth) return bitmap;
        double scale = (double)decodeWidth / width;
        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }
}
