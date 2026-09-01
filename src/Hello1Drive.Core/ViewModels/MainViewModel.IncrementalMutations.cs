namespace Hello1Drive.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Removes already-deleted OneDrive items from the currently presented folder as an ID-based
    /// incremental mutation. The native RecyclerView/UICollectionView keeps its existing viewport
    /// and decoded thumbnails; no LoadCurrentFolder/clear/rebuild cycle is involved.
    /// A normal background Refresh can subsequently reconcile the cache with Graph.
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
        ApplyFolderItemsIncrementally(
            remainingLoadedItems,
            finalCount,
            FolderCacheKey(CurrentFolderId));
        SetCurrentFolderTotalItemCount(finalCount);
    }
}
