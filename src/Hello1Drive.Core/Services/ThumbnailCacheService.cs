using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hello1Drive.Models;

namespace Hello1Drive.Services;

/// <summary>
/// Persistent disk cache for OneDrive image/video thumbnails.
/// Cache validity is tied to the DriveItem version token, so a cloud-side edit
/// naturally invalidates the previous thumbnail on the next folder listing.
/// </summary>
public sealed class ThumbnailCacheService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _itemLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ThumbnailCacheIndexEntry> _memoryIndex = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string CacheRoot { get; }

    public ThumbnailCacheService()
    {
        CacheRoot = ResolveCacheRoot();
        TryCreateDirectory(CacheRoot);
    }

    /// <summary>
    /// Returns a valid cached thumbnail path without making any network request.
    /// </summary>
    public bool TryGetCachedPath(DriveItemModel item, out string path)
    {
        path = string.Empty;
        if (!item.SupportsThumbnail || string.IsNullOrWhiteSpace(item.Id))
            return false;

        try
        {
            var versionedPath = GetVersionedContentPath(item.Id, item.VersionToken);
            if (File.Exists(versionedPath))
            {
                path = versionedPath;
                _memoryIndex[item.Id] = new ThumbnailCacheIndexEntry(item.VersionToken, versionedPath);
                return true;
            }

            if (_memoryIndex.TryGetValue(item.Id, out var indexed) &&
                string.Equals(indexed.VersionToken, item.VersionToken, StringComparison.Ordinal) &&
                File.Exists(indexed.ContentPath))
            {
                path = indexed.ContentPath;
                return true;
            }

            var metadataPath = GetMetadataPath(item.Id);
            if (!File.Exists(metadataPath))
                return false;

            var metadata = JsonSerializer.Deserialize<ThumbnailCacheMetadata>(File.ReadAllText(metadataPath), _jsonOptions);
            if (metadata is null ||
                !string.Equals(metadata.ItemId, item.Id, StringComparison.Ordinal) ||
                !string.Equals(metadata.VersionToken, item.VersionToken, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(metadata.ContentPath) ||
                !File.Exists(metadata.ContentPath))
            {
                return false;
            }

            path = metadata.ContentPath;

            // Migrate the legacy fixed filename lazily. New-version cache hits can then be
            // validated with one File.Exists() and no JSON read/parse during a scroll.
            try
            {
                var migratedPath = GetVersionedContentPath(item.Id, item.VersionToken);
                if (!string.Equals(path, migratedPath, StringComparison.Ordinal) && !File.Exists(migratedPath))
                {
                    File.Copy(path, migratedPath, overwrite: false);
                    path = migratedPath;
                }
            }
            catch
            {
                // The legacy cache remains valid even if migration cannot be completed.
            }

            _memoryIndex[item.Id] = new ThumbnailCacheIndexEntry(metadata.VersionToken, path);
            if (DateTimeOffset.UtcNow - metadata.LastAccessUtc > TimeSpan.FromMinutes(10))
                TouchMetadata(metadataPath, metadata);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reuses a valid disk thumbnail when possible; otherwise downloads one once
    /// and persists it for future app sessions.
    /// </summary>
    public async Task<string?> GetOrDownloadAsync(
        DriveItemModel item,
        IOneDriveService oneDrive,
        CancellationToken cancellationToken = default)
    {
        if (!item.SupportsThumbnail || string.IsNullOrWhiteSpace(item.Id))
            return null;

        if (TryGetCachedPath(item, out var alreadyCached))
            return alreadyCached;

        var gate = _itemLocks.GetOrAdd(item.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another concurrent loader may have populated it while we waited.
            if (TryGetCachedPath(item, out alreadyCached))
                return alreadyCached;

            var bytes = await oneDrive.GetThumbnailAsync(item, cancellationToken).ConfigureAwait(false);
            if (bytes is not { Length: > 0 })
                return null;

            var itemDirectory = GetItemDirectory(item.Id);
            TryCreateDirectory(itemDirectory);
            CleanItemDirectory(itemDirectory);

            // Version the content filename so a future cache lookup can validate it with a
            // single File.Exists() instead of opening/parsing metadata.json for every item.
            var finalPath = GetVersionedContentPath(item.Id, item.VersionToken);
            var tempPath = Path.Combine(itemDirectory, "thumbnail.tmp");
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, finalPath, overwrite: true);

            await SaveMetadataAsync(new ThumbnailCacheMetadata
            {
                ItemId = item.Id,
                FileName = item.Name,
                VersionToken = item.VersionToken,
                Size = item.Size,
                LastModifiedDateTime = item.LastModifiedDateTime,
                ContentPath = finalPath,
                LastAccessUtc = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);

            _memoryIndex[item.Id] = new ThumbnailCacheIndexEntry(item.VersionToken, finalPath);
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

        _memoryIndex.TryRemove(itemId, out _);
        try
        {
            var directory = GetItemDirectory(itemId);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best effort only. Cache cleanup must not break cloud operations.
        }
    }

    public void Clear()
    {
        _memoryIndex.Clear();
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

    private async Task SaveMetadataAsync(ThumbnailCacheMetadata metadata, CancellationToken cancellationToken)
    {
        var metadataPath = GetMetadataPath(metadata.ItemId);
        await using var stream = File.Create(metadataPath);
        await JsonSerializer.SerializeAsync(stream, metadata, _jsonOptions, cancellationToken);
    }

    private void TouchMetadata(string metadataPath, ThumbnailCacheMetadata metadata)
    {
        try
        {
            metadata.LastAccessUtc = DateTimeOffset.UtcNow;
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
        }
        catch
        {
            // Last-access time is informational only.
        }
    }

    private string GetItemDirectory(string itemId) => Path.Combine(CacheRoot, Hash(itemId));
    private string GetMetadataPath(string itemId) => Path.Combine(GetItemDirectory(itemId), "metadata.json");
    private string GetVersionedContentPath(string itemId, string versionToken) =>
        Path.Combine(GetItemDirectory(itemId), $"thumbnail-{Hash(versionToken)[..20]}.bin");

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ResolveCacheRoot()
    {
        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var portable = Path.Combine(AppContext.BaseDirectory, "cache", "thumbnails");
                try
                {
                    Directory.CreateDirectory(portable);
                    return portable;
                }
                catch
                {
                    // Installed desktop apps may live in a read-only location.
                }
            }

            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = Path.GetTempPath();
            return Path.Combine(root, "Hello1Drive", "cache", "thumbnails");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "Hello1Drive", "cache", "thumbnails");
        }
    }

    private static void TryCreateDirectory(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch { }
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
            // Best effort; the new thumbnail write may still succeed.
        }
    }

    private sealed record ThumbnailCacheIndexEntry(string VersionToken, string ContentPath);

    private sealed class ThumbnailCacheMetadata
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
