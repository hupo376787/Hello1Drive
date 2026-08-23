using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hello1Drive.Models;

namespace Hello1Drive.Services;

/// <summary>
/// Persistent metadata-only OneDrive index. It stores no file bodies and no decoded images.
/// The index is shared by all folders of one Microsoft account and is updated both by complete
/// folder enumerations and by the drive-wide Microsoft Graph delta feed.
/// </summary>
public sealed class LocalDriveIndexService
{
    public const string RootFolderKey = "__ROOT__";

    private readonly object _sync = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly Dictionary<string, AccountIndexState> _states = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };
    private readonly string _indexDirectory;
    private readonly bool _persistentStorageAvailable;

    public LocalDriveIndexService()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;

        _indexDirectory = Path.Combine(root, "Hello1Drive", "drive-index");
        try
        {
            Directory.CreateDirectory(_indexDirectory);
            _persistentStorageAvailable = true;
        }
        catch
        {
            // Browser/WASM or a locked-down platform may not expose a writable app-data folder.
            // Keep an in-memory index rather than making AppServices construction fail.
            _persistentStorageAvailable = false;
        }
    }

    public async Task EnsureAccountLoadedAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return;

        lock (_sync)
        {
            if (_states.ContainsKey(accountId))
                return;
        }

        var loaded = await Task.Run(() => LoadState(accountId), cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            if (!_states.ContainsKey(accountId))
                _states[accountId] = loaded;
        }
    }

    public async Task<LocalFolderIndexSnapshot?> GetFolderAsync(
        string accountId,
        string? folderId,
        string? orderBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return null;

        await EnsureAccountLoadedAsync(accountId, cancellationToken).ConfigureAwait(false);
        // Materializing thousands of DriveItemModel rows is deliberately kept off the UI thread.
        // The UI receives one completed immutable snapshot and then creates only lightweight slots.
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = TryGetFolder(accountId, folderId, orderBy);
            cancellationToken.ThrowIfCancellationRequested();
            return snapshot;
        }, cancellationToken).ConfigureAwait(false);
    }

    public LocalFolderIndexSnapshot? TryGetFolder(string accountId, string? folderId, string? orderBy)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return null;

        lock (_sync)
        {
            if (!_states.TryGetValue(accountId, out var state))
                return null;

            var folderKey = NormalizeFolderKey(folderId);
            if (!state.ChildrenByParent.TryGetValue(folderKey, out var ids))
            {
                var knowsFolder = folderKey == RootFolderKey
                    ? state.Document.DriveIndexComplete ||
                      state.Document.FolderCounts.ContainsKey(folderKey) ||
                      state.Document.FolderSyncedUtc.ContainsKey(folderKey)
                    : state.Document.FolderCounts.ContainsKey(folderKey) ||
                      state.Document.FolderSyncedUtc.ContainsKey(folderKey) ||
                      (state.ItemsById.TryGetValue(folderKey, out var folderRow) && folderRow.IsFolder);
                if (!knowsFolder)
                    return null;
                ids = [];
            }

            var rows = ids
                .Select(id => state.ItemsById.TryGetValue(id, out var item) ? item : null)
                .Where(static item => item is not null)
                .Cast<IndexedDriveItem>()
                .ToList();

            OrderRows(state, folderKey, rows, orderBy);

            var models = rows.Select(static row => row.ToModel()).ToArray();
            var total = state.Document.FolderCounts.TryGetValue(folderKey, out var count)
                ? Math.Max(count, models.Length)
                : models.Length;
            var complete = state.Document.DriveIndexComplete || state.Document.FolderSyncedUtc.ContainsKey(folderKey);
            var synced = state.Document.FolderSyncedUtc.TryGetValue(folderKey, out var folderSync)
                ? folderSync
                : state.Document.LastDeltaSyncUtc;

            var hasServerDefaultOrder = state.Document.OriginalOrders.ContainsKey(folderKey);
            return new LocalFolderIndexSnapshot(models, total, complete, synced, hasServerDefaultOrder);
        }
    }

    public bool HasFolder(string accountId, string? folderId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return false;

        lock (_sync)
        {
            if (!_states.TryGetValue(accountId, out var state))
                return false;
            var folderKey = NormalizeFolderKey(folderId);
            if (state.ChildrenByParent.ContainsKey(folderKey) ||
                state.Document.FolderCounts.ContainsKey(folderKey) ||
                state.Document.FolderSyncedUtc.ContainsKey(folderKey))
                return true;

            if (folderKey == RootFolderKey)
                return state.Document.DriveIndexComplete;

            return state.ItemsById.TryGetValue(folderKey, out var folderRow) && folderRow.IsFolder;
        }
    }

    public string? GetDeltaLink(string accountId)
    {
        lock (_sync)
            return _states.TryGetValue(accountId, out var state) ? state.Document.DeltaLink : null;
    }

    public string? GetRootItemId(string accountId)
    {
        lock (_sync)
            return _states.TryGetValue(accountId, out var state) ? state.Document.RootItemId : null;
    }

    public async Task SaveFolderAsync(
        string accountId,
        string? folderId,
        string? rootItemId,
        string? orderBy,
        IReadOnlyList<DriveItemModel> items,
        int? totalCount = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return;

        await EnsureAccountLoadedAsync(accountId, cancellationToken).ConfigureAwait(false);
        AccountIndexState state;
        lock (_sync)
        {
            state = _states[accountId];
            if (!string.IsNullOrWhiteSpace(rootItemId))
                state.Document.RootItemId = rootItemId;

            var folderKey = NormalizeFolderKey(folderId);
            var newIds = new HashSet<string>(items.Where(static x => !string.IsNullOrWhiteSpace(x.Id)).Select(static x => x.Id), StringComparer.Ordinal);

            if (state.ChildrenByParent.TryGetValue(folderKey, out var existingIds))
            {
                foreach (var staleId in existingIds.Where(id => !newIds.Contains(id)).ToArray())
                    RemoveItemCore(state, staleId);
            }

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Id))
                    continue;
                UpsertItemCore(state, IndexedDriveItem.FromModel(item, folderKey));

                // A parent folder listing already tells us each child folder's logical size.
                // Persist that count immediately so opening an unvisited child can establish its
                // full placeholder extent before either its own children page or the drive-wide
                // delta scan has completed.
                if (item.IsFolder)
                    state.Document.FolderCounts[item.Id] = Math.Max(0, item.ChildCount);
            }

            state.Document.FolderCounts[folderKey] = Math.Max(totalCount ?? items.Count, items.Count);
            state.Document.FolderSyncedUtc[folderKey] = DateTimeOffset.UtcNow;

            if (string.IsNullOrWhiteSpace(orderBy))
            {
                state.Document.OriginalOrders[folderKey] = items
                    .Where(static x => !string.IsNullOrWhiteSpace(x.Id))
                    .Select(static x => x.Id)
                    .ToList();
            }
        }

        await PersistStateAsync(accountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceFromFullDeltaAsync(
        string accountId,
        string rootItemId,
        IReadOnlyCollection<DriveItemModel> items,
        string deltaLink,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(deltaLink))
            return;

        await EnsureAccountLoadedAsync(accountId, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            var previous = _states[accountId];
            var next = new AccountIndexState(new LocalDriveIndexDocument
            {
                AccountId = accountId,
                RootItemId = rootItemId,
                DeltaLink = deltaLink,
                DriveIndexComplete = true,
                LastDeltaSyncUtc = DateTimeOffset.UtcNow,
                OriginalOrders = previous.Document.OriginalOrders
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value.ToList(), StringComparer.Ordinal),
                FolderSyncedUtc = new Dictionary<string, DateTimeOffset>(previous.Document.FolderSyncedUtc, StringComparer.Ordinal)
            });

            foreach (var item in items)
            {
                if (item.IsDeleted || string.IsNullOrWhiteSpace(item.Id) || string.Equals(item.Id, rootItemId, StringComparison.Ordinal))
                    continue;

                var parentKey = ParentKeyFromGraph(item.ParentReference?.Id, rootItemId);
                if (string.IsNullOrWhiteSpace(parentKey))
                    continue;

                UpsertItemCore(next, IndexedDriveItem.FromModel(item, parentKey));
                if (item.IsFolder)
                    next.Document.FolderCounts[item.Id] = Math.Max(0, item.ChildCount);
            }

            // A complete delta enumeration gives us authoritative membership even when the
            // folder facet omits childCount. Derive counts from the local hierarchy instead of
            // trusting a missing facet's default zero value.
            RecomputeFolderCountsFromMembership(next);
            CleanupStaleFolderMetadataAfterFullIndex(next);

            // Preserve known server-default orders, but remove IDs that no longer belong to
            // that folder and append newly discovered children at the tail.
            foreach (var (folderKey, order) in next.Document.OriginalOrders.ToArray())
            {
                var actual = next.ChildrenByParent.TryGetValue(folderKey, out var children)
                    ? children
                    : [];
                var filtered = order.Where(actual.Contains).Distinct(StringComparer.Ordinal).ToList();
                foreach (var id in actual)
                {
                    if (!filtered.Contains(id, StringComparer.Ordinal))
                        filtered.Add(id);
                }
                next.Document.OriginalOrders[folderKey] = filtered;
            }

            _states[accountId] = next;
        }

        await PersistStateAsync(accountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyIncrementalDeltaAsync(
        string accountId,
        string rootItemId,
        IReadOnlyList<DriveItemModel> changes,
        string deltaLink,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(deltaLink))
            return;

        await EnsureAccountLoadedAsync(accountId, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            var state = _states[accountId];
            state.Document.RootItemId = rootItemId;

            var deletedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in changes)
            {
                if (string.IsNullOrWhiteSpace(change.Id))
                    continue;

                if (change.IsDeleted)
                {
                    // Apply deletions after every non-deleted change in this delta set. That lets
                    // children that were moved out of a deleted folder update their parent first;
                    // anything still below the deleted folder can then be pruned as an orphaned
                    // subtree in one deterministic pass.
                    deletedIds.Add(change.Id);
                    continue;
                }

                if (string.Equals(change.Id, rootItemId, StringComparison.Ordinal))
                {
                    if (change.IsFolder)
                        state.Document.FolderCounts[RootFolderKey] = Math.Max(0, change.ChildCount);
                    continue;
                }

                var parentKey = ParentKeyFromGraph(change.ParentReference?.Id, rootItemId);
                if (string.IsNullOrWhiteSpace(parentKey))
                    continue;

                UpsertItemCore(state, IndexedDriveItem.FromModel(change, parentKey));
                if (change.IsFolder)
                    state.Document.FolderCounts[change.Id] = Math.Max(0, change.ChildCount);
            }

            foreach (var deletedId in deletedIds)
                RemoveItemTreeCore(state, deletedId);

            CleanupEmptyDanglingParentBuckets(state);
            state.Document.DriveIndexComplete = true;
            RecomputeFolderCountsFromMembership(state);
            state.Document.DeltaLink = deltaLink;
            state.Document.LastDeltaSyncUtc = DateTimeOffset.UtcNow;
        }

        await PersistStateAsync(accountId, cancellationToken).ConfigureAwait(false);
    }

    public void ClearMemory(string accountId)
    {
        lock (_sync)
            _states.Remove(accountId);
    }

    public void ClearAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return;

        lock (_sync)
            _states.Remove(accountId);

        if (!_persistentStorageAvailable)
            return;

        try
        {
            var path = GetIndexPath(accountId);
            if (File.Exists(path))
                File.Delete(path);
            var temporary = path + ".tmp";
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        catch
        {
            // Cache cleanup is best effort; browsing can continue from memory/Graph.
        }
    }

    public void ClearAll()
    {
        lock (_sync)
            _states.Clear();

        if (!_persistentStorageAvailable)
            return;

        try
        {
            if (!Directory.Exists(_indexDirectory))
                return;
            foreach (var file in Directory.EnumerateFiles(_indexDirectory, "*.json"))
                File.Delete(file);
            foreach (var file in Directory.EnumerateFiles(_indexDirectory, "*.tmp"))
                File.Delete(file);
        }
        catch
        {
            // Cache cleanup is best effort.
        }
    }

    private AccountIndexState LoadState(string accountId)
    {
        if (!_persistentStorageAvailable)
            return new AccountIndexState(new LocalDriveIndexDocument { AccountId = accountId });

        try
        {
            var path = GetIndexPath(accountId);
            if (!File.Exists(path))
                return new AccountIndexState(new LocalDriveIndexDocument { AccountId = accountId });

            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<LocalDriveIndexDocument>(json, _jsonOptions) ?? new LocalDriveIndexDocument();
            document.AccountId = accountId;
            document.Items ??= [];
            document.OriginalOrders ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
            document.FolderCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
            document.FolderSyncedUtc ??= new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            return new AccountIndexState(document);
        }
        catch
        {
            return new AccountIndexState(new LocalDriveIndexDocument { AccountId = accountId });
        }
    }

    private async Task PersistStateAsync(string accountId, CancellationToken cancellationToken)
    {
        if (!_persistentStorageAvailable)
            return;

        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LocalDriveIndexDocument snapshot;
            lock (_sync)
            {
                if (!_states.TryGetValue(accountId, out var state))
                    return;
                snapshot = state.CreateSerializableSnapshot();
            }

            try
            {
                Directory.CreateDirectory(_indexDirectory);
                var path = GetIndexPath(accountId);
                var temporary = path + ".tmp";
                await using (var stream = File.Create(temporary))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, _jsonOptions, cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporary, path, true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The metadata index is a performance/offline layer. A full disk, sandbox denial,
                // or transient filesystem error must never make cloud browsing fail.
            }
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private string GetIndexPath(string accountId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId))).ToLowerInvariant();
        return Path.Combine(_indexDirectory, $"{hash[..24]}.json");
    }

    private static string NormalizeFolderKey(string? folderId) => string.IsNullOrWhiteSpace(folderId) ? RootFolderKey : folderId;

    private static string? ParentKeyFromGraph(string? parentId, string rootItemId)
    {
        if (string.IsNullOrWhiteSpace(parentId))
            return null;
        return string.Equals(parentId, rootItemId, StringComparison.Ordinal) ? RootFolderKey : parentId;
    }

    private static void CleanupStaleFolderMetadataAfterFullIndex(AccountIndexState state)
    {
        static bool IsKnownFolder(AccountIndexState state, string folderKey) =>
            folderKey == RootFolderKey ||
            (state.ItemsById.TryGetValue(folderKey, out var row) && row.IsFolder);

        foreach (var key in state.Document.OriginalOrders.Keys.Where(key => !IsKnownFolder(state, key)).ToArray())
            state.Document.OriginalOrders.Remove(key);
        foreach (var key in state.Document.FolderSyncedUtc.Keys.Where(key => !IsKnownFolder(state, key)).ToArray())
            state.Document.FolderSyncedUtc.Remove(key);
    }

    private static void RecomputeFolderCountsFromMembership(AccountIndexState state)
    {
        if (!state.Document.DriveIndexComplete)
            return;

        state.Document.FolderCounts[RootFolderKey] = state.ChildrenByParent.TryGetValue(RootFolderKey, out var rootChildren)
            ? rootChildren.Count
            : 0;

        foreach (var folder in state.ItemsById.Values.Where(static item => item.IsFolder))
        {
            state.Document.FolderCounts[folder.Id] = state.ChildrenByParent.TryGetValue(folder.Id, out var children)
                ? children.Count
                : 0;
        }
    }

    private static void CleanupEmptyDanglingParentBuckets(AccountIndexState state)
    {
        foreach (var parentKey in state.ChildrenByParent.Keys.ToArray())
        {
            if (parentKey == RootFolderKey || state.ItemsById.ContainsKey(parentKey))
                continue;
            if (state.ChildrenByParent[parentKey].Count != 0)
                continue;

            state.ChildrenByParent.Remove(parentKey);
            state.Document.OriginalOrders.Remove(parentKey);
            state.Document.FolderCounts.Remove(parentKey);
            state.Document.FolderSyncedUtc.Remove(parentKey);
        }
    }

    private static void OrderRows(AccountIndexState state, string folderKey, List<IndexedDriveItem> rows, string? orderBy)
    {
        if (string.IsNullOrWhiteSpace(orderBy))
        {
            if (state.Document.OriginalOrders.TryGetValue(folderKey, out var order) && order.Count > 0)
            {
                var rank = order.Select((id, index) => (id, index)).ToDictionary(static x => x.id, static x => x.index, StringComparer.Ordinal);
                rows.Sort((a, b) =>
                {
                    var ai = rank.TryGetValue(a.Id, out var av) ? av : int.MaxValue;
                    var bi = rank.TryGetValue(b.Id, out var bv) ? bv : int.MaxValue;
                    var cmp = ai.CompareTo(bi);
                    return cmp != 0 ? cmp : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
                });
                return;
            }

            // Delta guarantees membership, not the children endpoint's display order. A stable
            // name fallback is preferable to a random dictionary order until that folder has
            // been enumerated once and an exact server-default order is recorded.
            rows.Sort(static (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            return;
        }

        var descending = orderBy.EndsWith(" desc", StringComparison.OrdinalIgnoreCase);
        var field = orderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        Comparison<IndexedDriveItem> comparison = field switch
        {
            "size" => static (a, b) => a.Size.CompareTo(b.Size),
            "lastModifiedDateTime" => static (a, b) => Nullable.Compare(a.LastModifiedDateTime, b.LastModifiedDateTime),
            _ => static (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name)
        };
        rows.Sort((a, b) => descending ? comparison(b, a) : comparison(a, b));
    }

    private static void UpsertItemCore(AccountIndexState state, IndexedDriveItem item)
    {
        if (state.ItemsById.TryGetValue(item.Id, out var previous) &&
            !string.Equals(previous.ParentKey, item.ParentKey, StringComparison.Ordinal) &&
            state.ChildrenByParent.TryGetValue(previous.ParentKey, out var previousChildren))
        {
            previousChildren.Remove(item.Id);
            RemoveFromOriginalOrder(state, previous.ParentKey, item.Id);
        }

        state.ItemsById[item.Id] = item;
        if (!state.ChildrenByParent.TryGetValue(item.ParentKey, out var children))
        {
            children = new HashSet<string>(StringComparer.Ordinal);
            state.ChildrenByParent[item.ParentKey] = children;
        }
        children.Add(item.Id);

        if (state.Document.OriginalOrders.TryGetValue(item.ParentKey, out var order) && !order.Contains(item.Id, StringComparer.Ordinal))
            order.Add(item.Id);
    }

    private static void RemoveItemTreeCore(AccountIndexState state, string itemId)
    {
        if (state.ChildrenByParent.TryGetValue(itemId, out var children))
        {
            foreach (var childId in children.ToArray())
                RemoveItemTreeCore(state, childId);
            state.ChildrenByParent.Remove(itemId);
        }

        state.Document.OriginalOrders.Remove(itemId);
        state.Document.FolderCounts.Remove(itemId);
        state.Document.FolderSyncedUtc.Remove(itemId);
        RemoveItemCore(state, itemId);
    }

    private static void RemoveItemCore(AccountIndexState state, string itemId)
    {
        if (!state.ItemsById.Remove(itemId, out var previous))
            return;

        if (state.ChildrenByParent.TryGetValue(previous.ParentKey, out var children))
            children.Remove(itemId);
        RemoveFromOriginalOrder(state, previous.ParentKey, itemId);
    }

    private static void RemoveFromOriginalOrder(AccountIndexState state, string parentKey, string itemId)
    {
        if (!state.Document.OriginalOrders.TryGetValue(parentKey, out var order))
            return;
        order.RemoveAll(id => string.Equals(id, itemId, StringComparison.Ordinal));
    }

    private sealed class AccountIndexState
    {
        public AccountIndexState(LocalDriveIndexDocument document)
        {
            Document = document;
            foreach (var item in document.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.ParentKey))
                    continue;
                ItemsById[item.Id] = item;
                if (!ChildrenByParent.TryGetValue(item.ParentKey, out var children))
                {
                    children = new HashSet<string>(StringComparer.Ordinal);
                    ChildrenByParent[item.ParentKey] = children;
                }
                children.Add(item.Id);
            }
        }

        public LocalDriveIndexDocument Document { get; }
        public Dictionary<string, IndexedDriveItem> ItemsById { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> ChildrenByParent { get; } = new(StringComparer.Ordinal);

        public LocalDriveIndexDocument CreateSerializableSnapshot() => new()
        {
            Version = Document.Version,
            AccountId = Document.AccountId,
            RootItemId = Document.RootItemId,
            DeltaLink = Document.DeltaLink,
            DriveIndexComplete = Document.DriveIndexComplete,
            LastDeltaSyncUtc = Document.LastDeltaSyncUtc,
            Items = ItemsById.Values.ToList(),
            OriginalOrders = Document.OriginalOrders.ToDictionary(static x => x.Key, static x => x.Value.ToList(), StringComparer.Ordinal),
            FolderCounts = new Dictionary<string, int>(Document.FolderCounts, StringComparer.Ordinal),
            FolderSyncedUtc = new Dictionary<string, DateTimeOffset>(Document.FolderSyncedUtc, StringComparer.Ordinal)
        };
    }
}

public sealed record LocalFolderIndexSnapshot(
    IReadOnlyList<DriveItemModel> Items,
    int TotalCount,
    bool IsComplete,
    DateTimeOffset? LastSyncedUtc,
    bool HasServerDefaultOrder);

public sealed class LocalDriveIndexDocument
{
    public int Version { get; set; } = 1;
    public string AccountId { get; set; } = string.Empty;
    public string? RootItemId { get; set; }
    public string? DeltaLink { get; set; }
    public bool DriveIndexComplete { get; set; }
    public DateTimeOffset? LastDeltaSyncUtc { get; set; }
    public List<IndexedDriveItem> Items { get; set; } = [];
    public Dictionary<string, List<string>> OriginalOrders { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> FolderCounts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, DateTimeOffset> FolderSyncedUtc { get; set; } = new(StringComparer.Ordinal);
}

public sealed class IndexedDriveItem
{
    public string Id { get; set; } = string.Empty;
    public string ParentKey { get; set; } = string.Empty;
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

    public static IndexedDriveItem FromModel(DriveItemModel item, string parentKey) => new()
    {
        Id = item.Id,
        ParentKey = parentKey,
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
