using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MangaView.Core;

/// <summary>CBZ/ZIP 安全解压结果。Files 已按自然排序排列。</summary>
public sealed record CbzExtractionResult(string ArchivePath, string Directory, IReadOnlyList<string> Files);

public sealed record CbzExtractionOptions(
    int MaxEntries = 10000,
    long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024,
    long MaxEntryBytes = 512L * 1024 * 1024)
{
    public static CbzExtractionOptions Default { get; } = new();
}

/// <summary>
/// CBZ/ZIP 虚拟文件夹：
/// - 不信任压缩包内路径，全部扁平化到安全文件名；
/// - 限制条目数、单文件大小与总解压大小，防压缩炸弹；
/// - 先解压到临时目录，成功后原子切换为正式缓存目录；
/// - 图片按自然排序，确保 page2 在 page10 前。
/// </summary>
public static class CbzArchive
{
    private static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".avif" };

    public static bool IsSupported(string path) =>
        string.Equals(Path.GetExtension(path), ".cbz", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    public static async Task<CbzExtractionResult> ExtractAsync(
        string archivePath,
        string cacheRoot,
        CbzExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        if (!File.Exists(archivePath)) throw new FileNotFoundException("压缩包不存在", archivePath);
        if (!IsSupported(archivePath)) throw new NotSupportedException("仅支持 .cbz / .zip");
        options ??= CbzExtractionOptions.Default;

        string archiveKey = BuildArchiveKey(archivePath);
        string finalDir = Path.Combine(cacheRoot, archiveKey);
        string completeMarker = Path.Combine(finalDir, ".complete");
        if (File.Exists(completeMarker))
        {
            return new CbzExtractionResult(archivePath, finalDir, EnumerateImages(finalDir));
        }

        Directory.CreateDirectory(cacheRoot);
        string tempDir = Path.Combine(cacheRoot, archiveKey + ".tmp");
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        Directory.CreateDirectory(tempDir);

        var files = new List<string>();
        long totalBytes = 0;
        try
        {
            await using FileStream archiveStream = File.OpenRead(archivePath);
            using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
            if (zip.Entries.Count > options.MaxEntries)
                throw new InvalidDataException($"压缩包条目过多：{zip.Entries.Count} > {options.MaxEntries}");

            int index = 0;
            var imageEntries = zip.Entries
                .Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\'))
                .Where(e => ImageExtensions.Contains(Path.GetExtension(e.FullName).ToLowerInvariant()))
                .OrderBy(e => Path.GetFileName(e.FullName.Replace('\\', '/')), NaturalSortComparer.Instance)
                .ToList();
            foreach (var entry in imageEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string baseName = Path.GetFileName(entry.FullName.Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(baseName) || baseName.Contains(".."))
                    throw new InvalidDataException($"压缩包内存在非法路径：{entry.FullName}");
                if (entry.Length > options.MaxEntryBytes)
                    throw new InvalidDataException($"压缩包内单文件过大：{entry.FullName}");
                if (totalBytes + entry.Length > options.MaxUncompressedBytes)
                    throw new InvalidDataException("压缩包总解压大小超出限制");

                string safeName = $"{index + 1:000000}_{SanitizeFileName(baseName)}";
                string destination = Path.Combine(tempDir, safeName);
                await using var source = entry.Open();
                await using var target = File.Create(destination);
                long copied = await CopyWithLimitAsync(source, target,
                    options.MaxUncompressedBytes - totalBytes, cancellationToken);
                totalBytes += copied;
                files.Add(destination);
                index++;
            }

            files.Sort((a, b) => NaturalSortComparer.Instance.Compare(
                Path.GetFileName(a), Path.GetFileName(b)));
            await File.WriteAllTextAsync(Path.Combine(tempDir, ".complete"),
                archivePath + Environment.NewLine + totalBytes, cancellationToken);
        }
        catch
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            throw;
        }

        if (Directory.Exists(finalDir)) Directory.Delete(finalDir, recursive: true);
        Directory.Move(tempDir, finalDir);
        return new CbzExtractionResult(archivePath, finalDir, files);
    }

    private static async Task<long> CopyWithLimitAsync(Stream source, Stream target, long limit,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (total + read > limit)
                throw new InvalidDataException("压缩包总解压大小超出限制");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }
        return total;
    }

    private static string BuildArchiveKey(string archivePath)
    {
        var info = new FileInfo(archivePath);
        string identity = $"{Path.GetFullPath(archivePath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        string safeName = SanitizeFileName(Path.GetFileNameWithoutExtension(archivePath));
        if (safeName.Length > 40) safeName = safeName[..40];
        return safeName + "_" + Convert.ToHexString(hash)[..16];
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = name.Select(c => invalid.Contains(c) || c < 32 ? '_' : c).ToArray();
        string result = new string(chars).TrimEnd('.');
        return string.IsNullOrEmpty(result) ? "page" : result;
    }


    private static IReadOnlyList<string> EnumerateImages(string directory) =>
        Directory.EnumerateFiles(directory)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, NaturalSortComparer.Instance)
            .ToList();
}


