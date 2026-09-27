namespace MangaView.Core;

/// <summary>图片播放列表的枚举与自然排序；不依赖任何 UI 框架。</summary>
public static class ImageCatalog
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".avif",
    };

    public static bool IsSupportedImage(string path) =>
        !string.IsNullOrWhiteSpace(path) && SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>枚举文件夹中的图片；默认仅当前目录，递归时按相对路径自然排序。</summary>
    public static IReadOnlyList<string> EnumerateImages(string folder, bool recursive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string fullFolder = Path.GetFullPath(folder);
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        return Directory.EnumerateFiles(fullFolder, "*", option)
            .Where(IsSupportedImage)
            .OrderBy(path => Path.GetRelativePath(fullFolder, path), NaturalSortComparer.Instance)
            .ToArray();
    }

    public static int IndexOfPath(IReadOnlyList<string> files, string path)
    {
        string fullPath = Path.GetFullPath(path);
        for (int i = 0; i < files.Count; i++)
        {
            if (string.Equals(Path.GetFullPath(files[i]), fullPath, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
