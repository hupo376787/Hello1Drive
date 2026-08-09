using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hello1Drive.Models;

namespace Hello1Drive.Services;

/// <summary>
/// Persistent cache for files opened from OneDrive. Before reusing a cached file,
/// a lightweight Graph metadata request compares the remote version token (eTag/cTag,
/// or size + lastModifiedDateTime as fallback). The file body is downloaded again only
/// when the remote item changed or the cached copy is missing.
/// </summary>
public sealed class FileCacheService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _itemLocks = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string CacheRoot { get; }

    public FileCacheService()
    {
        CacheRoot = ResolveCacheRoot();
        TryCreateDirectory(CacheRoot);
    }

    public async Task<string> GetOrDownloadAsync(
        DriveItemModel item,
        IOneDriveService oneDrive,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        if (!item.IsFile || string.IsNullOrWhiteSpace(item.Id))
            throw new ArgumentException("只能缓存 OneDrive 文件。", nameof(item));

        var gate = _itemLocks.GetOrAdd(item.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The folder listing already carries eTag/cTag. If that version matches the
            // cached metadata, reuse the local body immediately with zero Graph round-trip.
            // A subsequent folder refresh supplies a new version token when the cloud file
            // changes, so stale cached content is naturally invalidated on the next open.
            if (TryGetCachedPath(item, out var immediateCachedPath))
            {
                TouchMetadata(item.Id);
                progress?.Report(1.0);
                return immediateCachedPath;
            }

            DriveItemModel remoteItem;
            try
            {
                // We only need an extra metadata request when the current listing cannot
                // prove that the local cached body is the same cloud version.
                remoteItem = await oneDrive.GetItemMetadataAsync(item.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                if (!TryGetCachedPath(item, out var offlineCachedPath))
                    throw;

                // If the network is temporarily unavailable, an already cached file is
                // still more useful than failing the preview completely.
                TouchMetadata(item.Id);
                progress?.Report(1.0);
                return offlineCachedPath;
            }

            if (TryGetCachedPath(remoteItem, out var cachedPath))
            {
                TouchMetadata(remoteItem.Id);
                progress?.Report(1.0);
                return cachedPath;
            }

            var itemDirectory = GetItemDirectory(remoteItem.Id);
            TryCreateDirectory(itemDirectory);
            CleanItemDirectory(itemDirectory);

            var extension = NormalizeExtension(Path.GetExtension(remoteItem.Name));
            var finalPath = Path.Combine(itemDirectory, "content" + extension);
            var tempPath = Path.Combine(itemDirectory, "content.tmp");

            await using (var output = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await oneDrive.DownloadFileAsync(remoteItem.Id, output, progress, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, finalPath, overwrite: true);
            await SaveMetadataAsync(new CacheMetadata
            {
                ItemId = remoteItem.Id,
                FileName = remoteItem.Name,
                VersionToken = remoteItem.VersionToken,
                Size = remoteItem.Size,
                LastModifiedDateTime = remoteItem.LastModifiedDateTime,
                ContentPath = finalPath,
                LastAccessUtc = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);

            progress?.Report(1.0);
            return finalPath;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        try
        {
            var directory = GetItemDirectory(itemId);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Cache invalidation must never break the cloud operation that caused it.
        }
    }

    public void Clear()
    {
        try
        {
            if (Directory.Exists(CacheRoot))
                Directory.Delete(CacheRoot, recursive: true);
            Directory.CreateDirectory(CacheRoot);
        }
        catch
        {
            // Best effort only.
        }
    }

    public long GetCacheSizeBytes()
    {
        try
        {
            if (!Directory.Exists(CacheRoot))
                return 0;
            return Directory.EnumerateFiles(CacheRoot, "*", SearchOption.AllDirectories)
                .Sum(static path =>
                {
                    try { return new FileInfo(path).Length; }
                    catch { return 0L; }
                });
        }
        catch
        {
            return 0;
        }
    }

    private bool TryGetCachedPath(DriveItemModel item, out string path)
    {
        path = string.Empty;
        try
        {
            var metadataPath = GetMetadataPath(item.Id);
            if (!File.Exists(metadataPath))
                return false;

            var json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<CacheMetadata>(json, _jsonOptions);
            if (metadata is null ||
                !string.Equals(metadata.ItemId, item.Id, StringComparison.Ordinal) ||
                !string.Equals(metadata.VersionToken, item.VersionToken, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(metadata.ContentPath) ||
                !File.Exists(metadata.ContentPath))
            {
                return false;
            }

            path = metadata.ContentPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TouchMetadata(string itemId)
    {
        try
        {
            var metadataPath = GetMetadataPath(itemId);
            if (!File.Exists(metadataPath))
                return;

            var metadata = JsonSerializer.Deserialize<CacheMetadata>(File.ReadAllText(metadataPath), _jsonOptions);
            if (metadata is null)
                return;
            metadata.LastAccessUtc = DateTimeOffset.UtcNow;
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
        }
        catch
        {
            // Cosmetic LRU information only.
        }
    }

    private async Task SaveMetadataAsync(CacheMetadata metadata, CancellationToken cancellationToken)
    {
        var metadataPath = GetMetadataPath(metadata.ItemId);
        await using var stream = File.Create(metadataPath);
        await JsonSerializer.SerializeAsync(stream, metadata, _jsonOptions, cancellationToken);
    }

    private string GetItemDirectory(string itemId) => Path.Combine(CacheRoot, Hash(itemId));
    private string GetMetadataPath(string itemId) => Path.Combine(GetItemDirectory(itemId), "metadata.json");

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 18)
            return ".bin";

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            if (extension.Contains(c))
                return ".bin";
        }

        return extension;
    }

    private static string ResolveCacheRoot()
    {
        try
        {
            // Desktop portable builds keep cache beside the executable, as requested.
            // Android/iOS must use their sandboxed application-data directory.
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var portable = Path.Combine(AppContext.BaseDirectory, "cache", "files");
                try
                {
                    Directory.CreateDirectory(portable);
                    return portable;
                }
                catch
                {
                    // Installed desktop apps can live in a read-only directory. Fall back
                    // rather than making preview/download functionality unusable.
                }
            }

            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = Path.GetTempPath();
            return Path.Combine(root, "Hello1Drive", "cache", "files");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "Hello1Drive", "cache", "files");
        }
    }

    private static void TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // The next file operation will surface a useful error if storage is unavailable.
        }
    }

    private static void CleanItemDirectory(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
                File.Delete(file);
        }
        catch
        {
            // Best effort; writing the new content can still succeed.
        }
    }

    private sealed class CacheMetadata
    {
        public string ItemId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string VersionToken { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTimeOffset? LastModifiedDateTime { get; set; }
        public string ContentPath { get; set; } = string.Empty;
        public DateTimeOffset LastAccessUtc { get; set; }
    }
}
