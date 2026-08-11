using System.Text.Json;
using Hello1Drive.Models;

namespace Hello1Drive.Services;

public sealed class StartupSnapshotService
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        IgnoreReadOnlyProperties = true,
        PropertyNameCaseInsensitive = true
    };

    public string CachePath { get; }

    public StartupSnapshotService()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;

        var directory = Path.Combine(root, "Hello1Drive");
        Directory.CreateDirectory(directory);
        CachePath = Path.Combine(directory, "startup-cache.json");
    }

    public StartupSnapshot? TryLoad()
    {
        try
        {
            if (!File.Exists(CachePath))
                return null;

            var json = File.ReadAllText(CachePath);
            var snapshot = JsonSerializer.Deserialize<StartupSnapshot>(json, _jsonOptions);
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.AccountId))
                return null;

            snapshot.Breadcrumbs ??= [];
            snapshot.Items ??= [];
            if (snapshot.Breadcrumbs.Count == 0)
                snapshot.Breadcrumbs.Add(new RememberedBreadcrumb { Name = "OneDrive", ItemId = null });

            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(StartupSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var tempPath = CachePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempPath, CachePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Startup cache is only a performance optimization. Failure must not
            // affect authentication or normal OneDrive navigation.
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(CachePath))
                File.Delete(CachePath);
            var tempPath = CachePath + ".tmp";
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // Best effort only.
        }
    }
}

public sealed class StartupSnapshot
{
    public int Version { get; set; } = 1;
    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string AccountId { get; set; } = string.Empty;
    public string UserDisplayName { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string QuotaText { get; set; } = string.Empty;
    public long QuotaUsedBytes { get; set; }
    public long QuotaTotalBytes { get; set; }
    public List<RememberedBreadcrumb> Breadcrumbs { get; set; } = [];
    public List<StartupDriveItem> Items { get; set; } = [];
    public string? NextLink { get; set; }
    public int? TotalItemCount { get; set; }
}


public sealed class StartupDriveItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? WebUrl { get; set; }
    public DateTimeOffset? CreatedDateTime { get; set; }
    public DateTimeOffset? LastModifiedDateTime { get; set; }
    public string? ETag { get; set; }
    public string? CTag { get; set; }
    public bool IsFolder { get; set; }
    public int ChildCount { get; set; }
    public string? MimeType { get; set; }
    public string? SpecialFolderName { get; set; }

    public static StartupDriveItem FromModel(DriveItemModel item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Size = item.Size,
        WebUrl = item.WebUrl,
        CreatedDateTime = item.CreatedDateTime,
        LastModifiedDateTime = item.LastModifiedDateTime,
        ETag = item.ETag,
        CTag = item.CTag,
        IsFolder = item.IsFolder,
        ChildCount = item.ChildCount,
        MimeType = item.MimeType,
        SpecialFolderName = item.IsPersonalVault ? "vault" : item.SpecialFolder?.Name
    };

    public DriveItemModel ToModel()
    {
        var model = new DriveItemModel
        {
            Id = Id,
            Name = Name,
            Size = Size,
            WebUrl = WebUrl,
            CreatedDateTime = CreatedDateTime,
            LastModifiedDateTime = LastModifiedDateTime,
            ETag = ETag,
            CTag = CTag,
            SpecialFolder = string.IsNullOrWhiteSpace(SpecialFolderName)
                ? null
                : new SpecialFolderFacet { Name = SpecialFolderName }
        };

        if (IsFolder)
            model.Folder = new FolderFacet { ChildCount = ChildCount };
        else
            model.File = new FileFacet { MimeType = MimeType };

        return model;
    }
}
