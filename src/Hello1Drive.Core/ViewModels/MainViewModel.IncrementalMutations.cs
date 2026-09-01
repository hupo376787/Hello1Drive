namespace Hello1Drive.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Removes already-deleted OneDrive items from the currently presented folder as an ID-based
    /// incremental mutation. The native RecyclerView/UICollectionView keeps its existing viewport
    /// and decoded thumbnails; no LoadCurrentFolder/clear/rebuild cycle is involved.
    /// </summary>
    public void RemoveCurrentFolderItemsIncrementally(IEnumerable<string> itemIds)
    {
        var removedIds = itemIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (removedIds.Count == 0)
            return;

        var actuallyPresentedCount = _allItems.Count(item => removedIds.Contains(item.Id));
        if (actuallyPresentedCount == 0)
            return;

        var remainingLoadedItems = _allItems
            .Where(item => !removedIds.Contains(item.Id))
            .ToArray();

        var currentTotal = _currentFolderTotalItemCount ?? _allItems.Count;
        var finalCount = Math.Max(0, currentTotal - actuallyPresentedCount);
        var cacheKey = FolderCacheKey(CurrentFolderId);

        // Patch the cache before touching the visual slots. Returning to this folder therefore
        // cannot resurrect a just-deleted row from the old cached page. Mark validation stale so
        // the normal background revalidation can still pick up unrelated cloud-side changes later.
        if (_folderCache.TryGetValue(cacheKey, out var cache))
        {
            cache.Items.RemoveAll(item => removedIds.Contains(item.Id));
            cache.TotalItemCount = finalCount;
            cache.LastAccessUtc = DateTimeOffset.UtcNow;
            cache.LastValidatedUtc = DateTimeOffset.MinValue;
        }

        ApplyFolderItemsIncrementally(remainingLoadedItems, finalCount, cacheKey);
        SetCurrentFolderTotalItemCount(finalCount);
    }
}
