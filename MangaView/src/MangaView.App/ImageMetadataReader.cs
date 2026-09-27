using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ImageMagick;

namespace MangaView.App;

/// <summary>读取文件、EXIF、XMP 与常见 AI 生成参数，供信息面板异步显示。</summary>
public static class ImageMetadataReader
{
    private static readonly Regex AiKeyValueRegex = new(
        @"(?im)(?<key>prompt|negative[_ ]prompt|seed|model|sampler|steps|cfg(?:[_ ]scale)?)\s*[:=]\s*""?(?<value>[^""\r\n,}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static Task<string> ReadAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => Read(path, cancellationToken), cancellationToken);

    private static string Read(string path, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var info = new FileInfo(path);
        output.AppendLine("文件");
        output.AppendLine($"名称: {info.Name}");
        output.AppendLine($"路径: {info.FullName}");
        output.AppendLine($"大小: {FormatSize(info.Length)}");
        output.AppendLine($"修改: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");

        try
        {
            var imageInfo = new MagickImageInfo(path);
            output.AppendLine($"尺寸: {imageInfo.Width} × {imageInfo.Height}");
            output.AppendLine($"格式: {imageInfo.Format}");
        }
        catch (Exception ex)
        {
            output.AppendLine($"格式信息: 读取失败 ({ex.Message})");
        }

        try
        {
            using var image = new MagickImage(path);
            cancellationToken.ThrowIfCancellationRequested();
            AppendExif(image, output);
            AppendXmpAndAi(image, output);
        }
        catch (Exception ex)
        {
            output.AppendLine();
            output.AppendLine($"元数据: 读取失败 ({ex.Message})");
        }

        return output.ToString().TrimEnd();
    }

    private static void AppendExif(MagickImage image, StringBuilder output)
    {
        var exif = image.GetExifProfile();
        if (exif is null || exif.Values.Count == 0) return;

        output.AppendLine();
        output.AppendLine("EXIF");
        foreach (var value in exif.Values.Take(80))
        {
            string? text = FormatValue(value.GetValue());
            if (string.IsNullOrWhiteSpace(text)) continue;
            output.AppendLine($"{value.Tag}: {text}");
        }
    }

    private static void AppendXmpAndAi(MagickImage image, StringBuilder output)
    {
        string xmpText = string.Empty;
        try
        {
            var xmp = image.GetXmpProfile();
            if (xmp is not null)
            {
                XDocument? document = xmp.ToXDocument();
                if (document is null) return;
                xmpText = document.ToString(SaveOptions.DisableFormatting);
                output.AppendLine();
                output.AppendLine("XMP");
                output.AppendLine(TrimForDisplay(xmpText, 6000));
            }
        }
        catch
        {
        }

        string candidate = xmpText;
        foreach (string name in image.AttributeNames)
        {
            if (name.Contains("prompt", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("parameter", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("workflow", StringComparison.OrdinalIgnoreCase))
            {
                candidate += "\n" + image.GetAttribute(name);
            }
        }

        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AiKeyValueRegex.Matches(candidate))
        {
            string key = match.Groups["key"].Value.Trim().Replace(' ', '_');
            string value = match.Groups["value"].Value.Trim().Trim(',').Trim();
            if (value.Length > 0) pairs[key] = value;
        }

        if (pairs.Count == 0) return;
        output.AppendLine();
        output.AppendLine("AI 生成参数");
        foreach (var pair in pairs)
            output.AppendLine($"{pair.Key}: {TrimForDisplay(pair.Value, 1000)}");
    }

    private static string? FormatValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => TryDecodeText(bytes),
        Array array => string.Join(", ", array.Cast<object?>().Select(x => x?.ToString())),
        _ => value.ToString(),
    };

    private static string? TryDecodeText(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        string text = Encoding.UTF8.GetString(bytes).Trim('\0', ' ', '\r', '\n');
        return text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')
            ? $"<{bytes.Length} bytes>"
            : text;
    }

    private static string TrimForDisplay(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
