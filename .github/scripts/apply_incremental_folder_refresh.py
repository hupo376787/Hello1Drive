from pathlib import Path


def read(path):
    return Path(path).read_text(encoding='utf-8')


def write(path, text):
    Path(path).write_text(text, encoding='utf-8', newline='\n')


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 match, got {count}')
    return text.replace(old, new, 1)


# 1) DriveItemModel: update metadata in place so unchanged object identity/bitmaps survive cloud revalidation.
path = 'src/Hello1Drive.Core/Models/GraphModels.cs'
text = read(path)
marker = '''    public void Dispose()\n    {\n        ThumbnailImage?.Dispose();\n        ThumbnailImage = null;\n        GalleryImage?.Dispose();\n        GalleryImage = null;\n    }'''
method = '''    /// <summary>\n    /// Applies fresh Graph metadata while preserving this model instance and its transient UI state.\n    /// Folder revalidation can therefore update one visible row without replacing every cached model\n    /// or throwing away already-decoded thumbnails. Callers only invoke this when meaningful metadata\n    /// changed, so the forwarded notifications stay proportional to the actual cloud diff.\n    /// </summary>\n    public void ApplyMetadataFrom(DriveItemModel source)\n    {\n        if (source is null || ReferenceEquals(this, source))\n            return;\n\n        Id = source.Id;\n        Name = source.Name;\n        Size = source.Size;\n        WebUrl = source.WebUrl;\n        CreatedDateTime = source.CreatedDateTime;\n        LastModifiedDateTime = source.LastModifiedDateTime;\n        ETag = source.ETag;\n        CTag = source.CTag;\n        Folder = source.Folder;\n        File = source.File;\n        RemoteItem = source.RemoteItem;\n        SpecialFolder = source.SpecialFolder;\n        ParentReference = source.ParentReference;\n        Deleted = source.Deleted;\n        Root = source.Root;\n        Thumbnails = source.Thumbnails;\n\n        // The Graph fields above are intentionally plain DTO properties. Publish the small set of\n        // UI-facing/computed notifications only for models that the diff engine found changed.\n        OnPropertyChanged(nameof(Name));\n        OnPropertyChanged(nameof(Size));\n        OnPropertyChanged(nameof(WebUrl));\n        OnPropertyChanged(nameof(CreatedDateTime));\n        OnPropertyChanged(nameof(LastModifiedDateTime));\n        OnPropertyChanged(nameof(ETag));\n        OnPropertyChanged(nameof(CTag));\n        OnPropertyChanged(nameof(Folder));\n        OnPropertyChanged(nameof(File));\n        OnPropertyChanged(nameof(RemoteItem));\n        OnPropertyChanged(nameof(SpecialFolder));\n        OnPropertyChanged(nameof(ParentReference));\n        OnPropertyChanged(nameof(Deleted));\n        OnPropertyChanged(nameof(Root));\n        OnPropertyChanged(nameof(Thumbnails));\n        OnPropertyChanged(nameof(IsFolder));\n        OnPropertyChanged(nameof(IsFile));\n        OnPropertyChanged(nameof(IsDeleted));\n        OnPropertyChanged(nameof(IsDriveRoot));\n        OnPropertyChanged(nameof(ChildCount));\n        OnPropertyChanged(nameof(MimeType));\n        OnPropertyChanged(nameof(Extension));\n        OnPropertyChanged(nameof(IsImage));\n        OnPropertyChanged(nameof(IsVideo));\n        OnPropertyChanged(nameof(IsAudio));\n        OnPropertyChanged(nameof(IsPdf));\n        OnPropertyChanged(nameof(IsArchive));\n        OnPropertyChanged(nameof(IsWord));\n        OnPropertyChanged(nameof(IsExcel));\n        OnPropertyChanged(nameof(IsPowerPoint));\n        OnPropertyChanged(nameof(IsUrlShortcut));\n        OnPropertyChanged(nameof(IsMedia));\n        OnPropertyChanged(nameof(IsText));\n        OnPropertyChanged(nameof(SupportsThumbnail));\n        OnPropertyChanged(nameof(HasWebUrl));\n        OnPropertyChanged(nameof(IsGenericFile));\n        OnPropertyChanged(nameof(VersionToken));\n        OnPropertyChanged(nameof(ThumbnailUrl));\n        OnPropertyChanged(nameof(TypeDisplay));\n        OnPropertyChanged(nameof(SizeDisplay));\n        OnPropertyChanged(nameof(ModifiedDisplay));\n        OnPropertyChanged(nameof(IconText));\n        OnPropertyChanged(nameof(FileBadgeText));\n        OnPropertyChanged(nameof(ShowMobileFileBadge));\n        OnPropertyChanged(nameof(ShowVideoThumbnailBadge));\n    }\n\n''' + marker
text = replace_once(text, marker, method, 'DriveItemModel.ApplyMetadataFrom')
write(path, text)


# 2) Desktop surface: expose exact item top so the view can keep the same visual anchor after inserts/deletes.
path = 'src/Hello1Drive.Core/Controls/DesktopVirtualFileSurface.cs'
text = read(path)
old = '''    public (int First, int Last) GetVisibleRange() =>\n        CalculateVisibleRange(_viewportOffsetY, _viewportHeight, Math.Max(1, _viewportWidth));\n\n    public DriveItemModel? GetItemAt(Point point)'''
new = '''    public (int First, int Last) GetVisibleRange() =>\n        CalculateVisibleRange(_viewportOffsetY, _viewportHeight, Math.Max(1, _viewportWidth));\n\n    public double GetItemTop(int index)\n    {\n        if (index <= 0)\n            return 0;\n        if (Mode == FileViewMode.Details)\n            return index * DetailsRowHeight;\n\n        var metrics = GetGridMetrics(LayoutWidth);\n        return (index / Math.Max(1, metrics.Columns)) * (metrics.Height + GridSpacing);\n    }\n\n    public DriveItemModel? GetItemAt(Point point)'''
text = replace_once(text, old, new, 'Desktop item top helper')
write(path, text)


# 3) ViewModel: current-folder revalidation uses stable ID diff instead of Clear()+rebuild.
path = 'src/Hello1Drive.Core/ViewModels/MainViewModel.cs'
text = read(path)

text = replace_once(
    text,
    '''    private bool _startupSnapshotRestored;\n    private string _startupSnapshotAccountId = string.Empty;''',
    '''    private bool _startupSnapshotRestored;\n    private string _startupSnapshotAccountId = string.Empty;\n    // Tracks which folder the currently rendered stable slots belong to. A force-remote refresh of\n    // that same folder must keep the cached list on screen until the complete cloud diff is ready.\n    private string _presentedFolderCacheKey = "__ROOT__";''',
    'presented folder key field')

text = replace_once(
    text,
    '''    public event EventHandler<FolderNavigationEventArgs>? FolderNavigating;\n    public event EventHandler<FolderNavigationEventArgs>? FolderLoaded;''',
    '''    public event EventHandler<FolderNavigationEventArgs>? FolderNavigating;\n    public event EventHandler<FolderNavigationEventArgs>? FolderLoaded;\n    public event EventHandler? FolderItemsIncrementalChanging;\n    public event EventHandler? FolderItemsIncrementalChanged;''',
    'incremental events')

# Startup snapshot already has a rendered list; mark its folder as the presentation anchor.
text = replace_once(
    text,
    '''        var cacheKey = FolderCacheKey(CurrentFolderId);\n        _folderCache[cacheKey] = new FolderCacheEntry(''',
    '''        var cacheKey = FolderCacheKey(CurrentFolderId);\n        _presentedFolderCacheKey = cacheKey;\n        _folderCache[cacheKey] = new FolderCacheEntry(''',
    'snapshot presented key')

# When the persistent local index replaces the small startup snapshot, merge it in place too.
old = '''                StoreFolderCache(restoredCacheKey, restoredLocalSnapshot.Items, null, restoredLocalSnapshot.TotalCount);\n                if (_folderCache.TryGetValue(restoredCacheKey, out var restoredEntry))\n                    restoredEntry.LastValidatedUtc = restoredLocalSnapshot.LastSyncedUtc ?? DateTimeOffset.MinValue;\n                SetCurrentFolderTotalItemCount(restoredLocalSnapshot.TotalCount);\n                ApplyFolderItems(restoredLocalSnapshot.Items, restoredLocalSnapshot.TotalCount);'''
new = '''                SetCurrentFolderTotalItemCount(restoredLocalSnapshot.TotalCount);\n                ApplyFolderItemsIncrementally(restoredLocalSnapshot.Items, restoredLocalSnapshot.TotalCount, restoredCacheKey);\n                if (_folderCache.TryGetValue(restoredCacheKey, out var restoredEntry))\n                    restoredEntry.LastValidatedUtc = restoredLocalSnapshot.LastSyncedUtc ?? DateTimeOffset.MinValue;'''
text = replace_once(text, old, new, 'startup local-index merge')

# A force-remote request of the folder already on screen must not replace it with page one.
# Keep the cached list visible and let the existing full metadata enumerator produce one final diff.
needle = '''        _nextChildrenLink = page.NextLink;\n        HasMoreItems = page.HasMore;\n        var totalCount = _currentFolderTotalItemCount;'''
insert = '''        var refreshesPresentedFolder = forceRemote &&\n            string.Equals(_presentedFolderCacheKey, cacheKey, StringComparison.Ordinal);\n        if (refreshesPresentedFolder)\n        {\n            if (sizeSortFallback)\n                StatusText = "当前账户后端不支持大小排序，已对当前文件夹改用系统默认顺序";\n            else\n                StatusText = $"{(_currentFolderTotalItemCount ?? _allItems.Count)} 个项目 · 正在同步";\n\n            FolderLoaded?.Invoke(this, new FolderNavigationEventArgs(reason, cacheKey));\n            StartFolderMetadataSync(\n                folderId,\n                cacheKey,\n                navigationVersion,\n                orderBy,\n                seedItems: page.Items,\n                nextLink: page.NextLink,\n                streamIntoPlaceholders: false);\n            return;\n        }\n\n        _nextChildrenLink = page.NextLink;\n        HasMoreItems = page.HasMore;\n        var totalCount = _currentFolderTotalItemCount;'''
text = replace_once(text, needle, insert, 'force remote current-folder handoff')

# Replace the final completed-folder reconciliation callback with an idle-safe retry and incremental diff.
anchor = '            token.ThrowIfCancellationRequested();\n            var finalCount = collected.Count;'
pos = text.index(anchor)
start = text.index('            await Dispatcher.UIThread.InvokeAsync(() =>\n', pos)
end_marker = '            }, DispatcherPriority.Background);'
end = text.index(end_marker, start) + len(end_marker)
old_block = text[start:end]
new_block = '''            var reconciliationDone = false;\n            while (!reconciliationDone)\n            {\n                token.ThrowIfCancellationRequested();\n                await WaitForMetadataPresentationWindowAsync(DateTime.UtcNow, token).ConfigureAwait(false);\n\n                var reconcileResult = await Dispatcher.UIThread.InvokeAsync(() =>\n                {\n                    if (navigationVersion != _folderNavigationVersion ||\n                        FolderCacheKey(CurrentFolderId) != cacheKey)\n                        return -1;\n\n                    // The disk/index save above can take long enough for a new wheel/fling gesture\n                    // to start. Re-check on the UI thread and let input finish before touching slots.\n                    if ((IsMobilePlatform && _mobileListScrolling) ||\n                        (!IsMobilePlatform && _desktopListScrolling))\n                        return 0;\n\n                    _nextChildrenLink = null;\n                    HasMoreItems = false;\n                    SetCurrentFolderTotalItemCount(finalCount);\n\n                    if (streamIntoPlaceholders)\n                    {\n                        ReconcileMobileSlotCount(finalCount);\n                        if (_folderCache.TryGetValue(cacheKey, out var entry))\n                        {\n                            entry.NextLink = null;\n                            entry.TotalItemCount = finalCount;\n                            entry.LastAccessUtc = DateTimeOffset.UtcNow;\n                            entry.LastValidatedUtc = DateTimeOffset.UtcNow;\n                        }\n                        StatusText = $"{finalCount} 个项目";\n                        ScheduleStartupSnapshotSave();\n                        return 1;\n                    }\n\n                    // No cloud-visible change: keep every model, slot, thumbnail and scroll anchor.\n                    if (FolderItemsEquivalent(_allItems, collected))\n                    {\n                        if (_folderCache.TryGetValue(cacheKey, out var unchangedEntry))\n                        {\n                            unchangedEntry.NextLink = null;\n                            unchangedEntry.TotalItemCount = finalCount;\n                            unchangedEntry.LastAccessUtc = DateTimeOffset.UtcNow;\n                            unchangedEntry.LastValidatedUtc = DateTimeOffset.UtcNow;\n                        }\n                        DisposeItemThumbnails(collected);\n                        StatusText = $"{finalCount} 个项目";\n                        ScheduleStartupSnapshotSave();\n                        return 1;\n                    }\n\n                    // Do not move items under an active long-press/multi-selection. The durable local\n                    // index already contains the new metadata and the next navigation can show it.\n                    if (SelectionCount > 0)\n                    {\n                        DisposeItemThumbnails(collected);\n                        StatusText = $"{finalCount} 个项目";\n                        return 1;\n                    }\n\n                    ApplyFolderItemsIncrementally(collected, finalCount, cacheKey);\n                    StatusText = $"{finalCount} 个项目";\n                    return 1;\n                }, DispatcherPriority.Background);\n\n                if (reconcileResult < 0)\n                    return;\n                reconciliationDone = reconcileResult > 0;\n                if (!reconciliationDone)\n                    await Task.Delay(40, token).ConfigureAwait(false);\n            }'''
text = text[:start] + new_block + text[end:]

# Insert the incremental merge engine before the legacy background helper.
marker = '    private async Task RefreshFolderInBackgroundAsync(string? folderId, string cacheKey, long navigationVersion)\n'
helper = r'''    private void ApplyFolderItemsIncrementally(
        IReadOnlyList<DriveItemModel> incomingItems,
        int finalCount,
        string cacheKey)
    {
        // Normalize duplicate Graph rows before comparing order. IDs are the stable OneDrive identity.
        var incoming = new List<DriveItemModel>(incomingItems.Count);
        var incomingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in incomingItems)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || incomingIds.Add(item.Id))
            {
                incoming.Add(item);
            }
            else
            {
                item.Dispose();
            }
        }

        var orderChanged = _allItems.Count != incoming.Count;
        if (!orderChanged)
        {
            for (var i = 0; i < incoming.Count; i++)
            {
                if (!string.Equals(_allItems[i].Id, incoming[i].Id, StringComparison.Ordinal))
                {
                    orderChanged = true;
                    break;
                }
            }
        }

        // Capture the top visible item before any position changes. MainView restores the same ID
        // after reconciliation, so inserts/deletes above the viewport do not move what the user sees.
        if (orderChanged)
            FolderItemsIncrementalChanging?.Invoke(this, EventArgs.Empty);

        var existingById = new Dictionary<string, DriveItemModel>(StringComparer.Ordinal);
        foreach (var current in _allItems)
        {
            if (!string.IsNullOrWhiteSpace(current.Id))
                existingById.TryAdd(current.Id, current);
        }

        var merged = new List<DriveItemModel>(incoming.Count);
        foreach (var fresh in incoming)
        {
            if (!string.IsNullOrWhiteSpace(fresh.Id) && existingById.Remove(fresh.Id, out var existing))
            {
                if (!FolderItemEquivalent(existing, fresh))
                {
                    var oldVersion = existing.VersionToken;
                    existing.ApplyMetadataFrom(fresh);
                    if (!string.Equals(oldVersion, existing.VersionToken, StringComparison.Ordinal))
                    {
                        // Keep the currently displayed bitmap during the diff to avoid a placeholder
                        // flash, but invalidate persistent bodies so a future load uses the new version.
                        _thumbnailCache.Invalidate(existing.Id);
                        _fileCache.Invalidate(existing.Id);
                    }
                }

                // The existing object owns all transient state (thumbnail/gallery/selection). The
                // fresh Graph DTO is no longer needed after its metadata has been copied.
                fresh.Dispose();
                existing.IsMobileSelectionMode = MobileSelectionModeActive;
                merged.Add(existing);
            }
            else
            {
                fresh.IsMobileSelectionMode = MobileSelectionModeActive;
                merged.Add(fresh);
            }
        }

        var removedItems = existingById.Values.ToArray();

        _allItems.Clear();
        _allItems.AddRange(merged);
        _currentItemIds.Clear();
        foreach (var item in merged)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
                _currentItemIds.Add(item.Id);
        }

        var keyword = SearchText.Trim();
        var visible = string.IsNullOrWhiteSpace(keyword)
            ? merged.ToArray()
            : merged.Where(item => item.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var slotCount = string.IsNullOrWhiteSpace(keyword)
            ? Math.Max(finalCount, visible.Length)
            : visible.Length;

        // Preserve the stable VirtualDriveItemSlot collection. Only positions whose item identity
        // actually changed are rebound; a rename/size/date update keeps the exact same slot/model.
        ReconcileMobileSlotCount(slotCount);
        for (var i = 0; i < MobileItems.Count; i++)
        {
            var nextItem = i < visible.Length ? visible[i] : null;
            if (!ReferenceEquals(MobileItems[i].Item, nextItem))
                MobileItems[i].SetItem(nextItem, compactNotification: !IsMobilePlatform);
        }

        // Desktop UI renders VirtualItems directly; Items is only a compatibility shadow.
        if (!IsMobilePlatform)
        {
            Items.Clear();
            Items.AddRange(visible);
        }

        if (_folderCache.TryGetValue(cacheKey, out var entry))
        {
            entry.Items.Clear();
            entry.Items.AddRange(merged);
            entry.NextLink = null;
            entry.TotalItemCount = finalCount;
            entry.LastAccessUtc = DateTimeOffset.UtcNow;
            entry.LastValidatedUtc = DateTimeOffset.UtcNow;
        }
        else
        {
            _folderCache[cacheKey] = new FolderCacheEntry(
                merged.ToList(),
                null,
                finalCount,
                DateTimeOffset.UtcNow,
                GetGraphOrderBy());
            TrimFolderCache(cacheKey);
        }

        _presentedFolderCacheKey = cacheKey;
        _nextChildrenLink = null;
        HasMoreItems = false;
        SetCurrentFolderTotalItemCount(finalCount);
        CurrentLocation = string.Join(" / ", Breadcrumbs.Select(x => x.Name));

        // Native adapters own decoded bitmaps and listen for this lightweight page/list signal.
        // Desktop reuses the existing thumbnail objects and only queues genuinely new visible files.
        if (UsesNativeMobileFileList)
        {
            OnPropertyChanged(nameof(MobileItems));
        }
        else if (!IsMobilePlatform)
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            var candidates = new List<DriveItemModel>();
            foreach (var index in _desktopThumbnailVisibleSlotIndices)
            {
                if (index < 0 || index >= MobileItems.Count || MobileItems[index].Item is not { } item ||
                    string.IsNullOrWhiteSpace(item.Id))
                    continue;

                wanted.Add(item.Id);
                if (item.SupportsThumbnail && !item.HasThumbnailImage)
                    candidates.Add(item);
            }
            _desktopThumbnailWantedIds = wanted;
            if (candidates.Count > 0)
                StartThumbnailLoading(candidates, requireVisibleOnDesktop: true);
        }

        // Removed rows are no longer referenced by slots/cache. Preserve a currently open preview;
        // otherwise release transient bitmaps after the new scene is already wired up.
        foreach (var removed in removedItems)
        {
            if (_mobileThumbnailLruNodes.Remove(removed.Id, out var node))
                _mobileThumbnailLru.Remove(node);
            if (!ReferenceEquals(PreviewItem, removed))
                removed.Dispose();
        }

        RememberCurrentFolderViewMode();
        RememberCurrentFolderSortRule();
        if (RememberLastFolder)
            CaptureCurrentFolderMemory();
        _ = _settingsService.SaveAsync();
        if (IsAuthenticated && !string.IsNullOrWhiteSpace(CurrentAccountId))
            ScheduleStartupSnapshotSave();

        if (orderChanged)
            FolderItemsIncrementalChanged?.Invoke(this, EventArgs.Empty);
    }

'''
text = replace_once(text, marker, helper + marker, 'incremental helper')

# If this legacy helper is ever called, use the same incremental path rather than a whole reset.
text = replace_once(
    text,
    '''                StoreFolderCache(cacheKey, remotePage.Items, remotePage.NextLink, _currentFolderTotalItemCount);\n                ApplyFolderItems(remotePage.Items);''',
    '''                if (!remotePage.HasMore)\n                    ApplyFolderItemsIncrementally(remotePage.Items, remotePage.Items.Count, cacheKey);\n                else\n                    StartFolderMetadataSync(folderId, cacheKey, navigationVersion, GetGraphOrderBy(), remotePage.Items, remotePage.NextLink, streamIntoPlaceholders: false);''',
    'legacy background refresh')

# Full presentation is still correct when navigating to a different folder, but record ownership.
text = replace_once(
    text,
    '''    private void ApplyFolderItems(IReadOnlyList<DriveItemModel> items, int? totalItemCount = null)\n    {\n        CancelThumbnailLoading();''',
    '''    private void ApplyFolderItems(IReadOnlyList<DriveItemModel> items, int? totalItemCount = null)\n    {\n        _presentedFolderCacheKey = FolderCacheKey(CurrentFolderId);\n        CancelThumbnailLoading();''',
    'full presentation key')

# Reuse one item comparison primitive for both the no-op fast path and the diff engine.
old = '''    private static bool FolderItemsEquivalent(IReadOnlyList<DriveItemModel> left, IReadOnlyList<DriveItemModel> right)\n    {\n        if (left.Count != right.Count)\n            return false;\n\n        for (var i = 0; i < left.Count; i++)\n        {\n            var a = left[i];\n            var b = right[i];\n            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal) ||\n                !string.Equals(a.Name, b.Name, StringComparison.Ordinal) ||\n                !string.Equals(a.VersionToken, b.VersionToken, StringComparison.Ordinal) ||\n                a.Size != b.Size ||\n                a.ChildCount != b.ChildCount)\n            {\n                return false;\n            }\n        }\n\n        return true;\n    }'''
new = '''    private static bool FolderItemEquivalent(DriveItemModel a, DriveItemModel b) =>\n        string.Equals(a.Id, b.Id, StringComparison.Ordinal) &&\n        string.Equals(a.Name, b.Name, StringComparison.Ordinal) &&\n        string.Equals(a.VersionToken, b.VersionToken, StringComparison.Ordinal) &&\n        a.Size == b.Size &&\n        a.ChildCount == b.ChildCount;\n\n    private static bool FolderItemsEquivalent(IReadOnlyList<DriveItemModel> left, IReadOnlyList<DriveItemModel> right)\n    {\n        if (left.Count != right.Count)\n            return false;\n\n        for (var i = 0; i < left.Count; i++)\n        {\n            if (!FolderItemEquivalent(left[i], right[i]))\n                return false;\n        }\n\n        return true;\n    }'''
text = replace_once(text, old, new, 'folder equivalence helper')

write(path, text)


# 4) MainView: capture and restore the top visible item around a structural cloud diff.
path = 'src/Hello1Drive.Core/Views/MainView.axaml.cs'
text = read(path)
text = replace_once(
    text,
    '''    private readonly Dictionary<string, Vector> _folderScrollPositions = new(StringComparer.Ordinal);\n    private readonly Dictionary<string, int> _nativeFolderScrollPositions = new(StringComparer.Ordinal);\n    private TopLevel? _topLevel;''',
    '''    private readonly Dictionary<string, Vector> _folderScrollPositions = new(StringComparer.Ordinal);\n    private readonly Dictionary<string, int> _nativeFolderScrollPositions = new(StringComparer.Ordinal);\n    private string? _incrementalRefreshAnchorId;\n    private int _incrementalRefreshAnchorIndex = -1;\n    private double _incrementalRefreshAnchorOffset;\n    private TopLevel? _topLevel;''',
    'view anchor fields')

text = replace_once(
    text,
    '''            vm.FolderNavigating += Vm_FolderNavigating;\n            vm.FolderLoaded += Vm_FolderLoaded;''',
    '''            vm.FolderNavigating += Vm_FolderNavigating;\n            vm.FolderLoaded += Vm_FolderLoaded;\n            vm.FolderItemsIncrementalChanging += Vm_FolderItemsIncrementalChanging;\n            vm.FolderItemsIncrementalChanged += Vm_FolderItemsIncrementalChanged;''',
    'view anchor subscriptions')

text = replace_once(
    text,
    '''        if (DataContext is MainViewModel mobileVm)\n        {\n            mobileVm.SetMobileListScrolling(false);\n            mobileVm.SetDesktopListScrolling(false);\n        }''',
    '''        if (DataContext is MainViewModel mobileVm)\n        {\n            mobileVm.FolderItemsIncrementalChanging -= Vm_FolderItemsIncrementalChanging;\n            mobileVm.FolderItemsIncrementalChanged -= Vm_FolderItemsIncrementalChanged;\n            mobileVm.SetMobileListScrolling(false);\n            mobileVm.SetDesktopListScrolling(false);\n        }''',
    'view anchor unsubscriptions')

marker = '    private void Vm_FolderNavigating(object? sender, FolderNavigationEventArgs e)\n'
handlers = r'''    private void Vm_FolderItemsIncrementalChanging(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        _incrementalRefreshAnchorId = null;
        _incrementalRefreshAnchorIndex = -1;
        _incrementalRefreshAnchorOffset = 0;

        if (UsesNativeMobileFileList)
        {
            var index = _nativeMobileFileListHost?.LastFirstVisibleIndex ?? -1;
            var item = vm.GetMobileItemAtIndex(index);
            if (item is null || string.IsNullOrWhiteSpace(item.Id))
                return;

            _incrementalRefreshAnchorId = item.Id;
            _incrementalRefreshAnchorIndex = index;
            return;
        }

        if (IsMobilePlatform)
            return;

        var (first, _) = DesktopFileSurface.GetVisibleRange();
        var firstItem = vm.GetMobileItemAtIndex(first);
        if (firstItem is null || string.IsNullOrWhiteSpace(firstItem.Id))
            return;

        _incrementalRefreshAnchorId = firstItem.Id;
        _incrementalRefreshAnchorIndex = first;
        _incrementalRefreshAnchorOffset =
            DesktopVirtualScrollViewer.Offset.Y - DesktopFileSurface.GetItemTop(first);
    }

    private void Vm_FolderItemsIncrementalChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm || string.IsNullOrWhiteSpace(_incrementalRefreshAnchorId))
            return;

        var anchorId = _incrementalRefreshAnchorId;
        var oldIndex = _incrementalRefreshAnchorIndex;
        var offsetWithinItem = _incrementalRefreshAnchorOffset;
        _incrementalRefreshAnchorId = null;
        _incrementalRefreshAnchorIndex = -1;
        _incrementalRefreshAnchorOffset = 0;

        var newIndex = -1;
        for (var i = 0; i < vm.MobileItems.Count; i++)
        {
            if (string.Equals(vm.MobileItems[i].Item?.Id, anchorId, StringComparison.Ordinal))
            {
                newIndex = i;
                break;
            }
        }

        if (newIndex < 0 || newIndex == oldIndex)
            return;

        if (UsesNativeMobileFileList)
        {
            Dispatcher.UIThread.Post(() => _nativeMobileFileListHost?.ScrollToPosition(newIndex), DispatcherPriority.Loaded);
            return;
        }

        if (IsMobilePlatform)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var targetY = Math.Max(0, DesktopFileSurface.GetItemTop(newIndex) + offsetWithinItem);
            DesktopVirtualScrollViewer.Offset = new Vector(DesktopVirtualScrollViewer.Offset.X, targetY);
            SyncDesktopVirtualSurfaceViewport(DesktopVirtualScrollViewer);
        }, DispatcherPriority.Loaded);
    }

'''
text = replace_once(text, marker, handlers + marker, 'view anchor handlers')
write(path, text)

print('Incremental folder refresh patch applied.')
