from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1) Desktop fixed slots only need one Item notification when a background Graph page hydrates.
slot = ROOT / "src/Hello1Drive.Core/Models/VirtualDriveItemSlot.cs"
replace_once(
    slot,
    '''    public void SetItem(DriveItemModel? item)
    {
        if (ReferenceEquals(_item, item))
            return;

        if (_item is not null)
            _item.PropertyChanged -= Item_PropertyChanged;

        _item = item;

        if (_item is not null)
            _item.PropertyChanged += Item_PropertyChanged;

        RaiseAllForwardedProperties();
    }''',
    '''    public void SetItem(DriveItemModel? item, bool compactNotification = false)
    {
        if (ReferenceEquals(_item, item))
            return;

        if (_item is not null)
            _item.PropertyChanged -= Item_PropertyChanged;

        _item = item;

        if (_item is not null)
            _item.PropertyChanged += Item_PropertyChanged;

        // Desktop uses one self-drawn control per realized slot and reads the DriveItemModel
        // directly. A 200-item Graph page therefore needs only one notification per slot instead
        // of the 15+ forwarded binding notifications required by the legacy/mobile XAML surface.
        // This keeps a background page hydration short enough for pointer/wheel input to preempt.
        if (compactNotification)
        {
            OnPropertyChanged(nameof(Item));
            return;
        }

        RaiseAllForwardedProperties();
    }''',
    "compact slot hydration notification",
)


# 2) Self-drawn desktop items subscribe to the underlying item directly and invalidate once for
# thumbnail/selection changes instead of once for every forwarded slot property.
control = ROOT / "src/Hello1Drive.Core/Controls/DesktopFileItemControl.cs"
replace_once(
    control,
    '''    private VirtualDriveItemSlot? _slot;
    private bool _hovered;''',
    '''    private VirtualDriveItemSlot? _slot;
    private DriveItemModel? _item;
    private bool _hovered;''',
    "desktop control item field",
)
replace_once(
    control,
    '''    private void AttachSlot(VirtualDriveItemSlot? slot)
    {
        if (ReferenceEquals(_slot, slot))
            return;

        if (_slot is not null)
            _slot.PropertyChanged -= Slot_PropertyChanged;

        _slot = slot;

        if (_slot is not null)
            _slot.PropertyChanged += Slot_PropertyChanged;

        InvalidateVisual();
    }

    private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();''',
    '''    private void AttachSlot(VirtualDriveItemSlot? slot)
    {
        if (ReferenceEquals(_slot, slot))
            return;

        if (_slot is not null)
            _slot.PropertyChanged -= Slot_PropertyChanged;

        AttachItem(null);
        _slot = slot;

        if (_slot is not null)
        {
            _slot.PropertyChanged += Slot_PropertyChanged;
            AttachItem(_slot.Item);
        }

        InvalidateVisual();
    }

    private void AttachItem(DriveItemModel? item)
    {
        if (ReferenceEquals(_item, item))
            return;

        if (_item is not null)
            _item.PropertyChanged -= Item_PropertyChanged;

        _item = item;
        if (_item is not null)
            _item.PropertyChanged += Item_PropertyChanged;
    }

    private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // SetItem raises Item first. Attach once to the new model and ignore all legacy forwarded
        // property notifications; the self-drawn desktop control reads the model directly.
        if (e.PropertyName == nameof(VirtualDriveItemSlot.Item))
        {
            AttachItem(_slot?.Item);
            InvalidateVisual();
        }
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ThumbnailImage already implies HasThumbnailImage/HasNoThumbnailImage/video-badge state.
        // Reacting to those forwarded companion notifications caused four redraw invalidations for
        // one decoded bitmap. Selection is the only other live visual state used on desktop.
        if (e.PropertyName is nameof(DriveItemModel.ThumbnailImage) or nameof(DriveItemModel.IsMobileSelected))
            InvalidateVisual();
    }''',
    "desktop control filtered model notifications",
)


# 3) Make thumbnail presentation input-friendly and shrink the amount of desktop decode work that
# can still be running when a new wheel/scrollbar gesture begins.
vm = ROOT / "src/Hello1Drive.Core/ViewModels/MainViewModel.cs"
replace_once(
    vm,
    '''    // Four desktop workers are enough to fill a three-viewport look-ahead window without
    // letting cached bitmap decode compete too aggressively with Avalonia layout/rendering.
    private readonly SemaphoreSlim _desktopThumbnailWorkGate = new(4, 4);''',
    '''    // Keep only two desktop decode/download workers active. Bitmap.DecodeToWidth itself is
    // not interruptible mid-decode, so a larger worker pool can keep several CPU-heavy decodes
    // running for a short time after the user starts scrolling.
    private readonly SemaphoreSlim _desktopThumbnailWorkGate = new(2, 2);''',
    "desktop thumbnail worker cap",
)
replace_once(
    vm,
    '''            MobileItems[index].SetItem(item);''',
    '''            MobileItems[index].SetItem(item, compactNotification: !IsMobilePlatform);''',
    "compact desktop background slot hydration",
)
replace_once(
    vm,
    '''            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Use the O(1) id set rather than List.Contains() on every thumbnail completion.
                // In thousand-item folders the old linear check became measurable UI-thread work.
                if (cancellationToken.IsCancellationRequested || !_currentItemIds.Contains(item.Id) ||
                    (IsMobilePlatform && (requireVisibleOnMobile
                        ? !IsMobileThumbnailVisible(item)
                        : !IsMobileThumbnailWanted(item))) ||
                    (!IsMobilePlatform && requireVisibleOnDesktop && !IsDesktopThumbnailWanted(item)))
                {
                    bitmap?.Dispose();
                    bitmap = null;
                    return;
                }

                item.ThumbnailImage?.Dispose();
                item.ThumbnailImage = bitmap;
                TouchMobileThumbnail(item);
                bitmap = null;
            });''',
    '''            // A decoded desktop bitmap is cosmetic. Never let its UI hand-off compete with
            // wheel/pointer input: wait while scrolling and post the final model swap at Background
            // priority. If a new gesture starts after the wait but before this callback executes,
            // skip this presentation; idle recovery will cheaply decode it again from disk cache.
            if (!IsMobilePlatform)
            {
                while (_desktopListScrolling)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(24, cancellationToken).ConfigureAwait(false);
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsMobilePlatform && _desktopListScrolling)
                    return;

                // Use the O(1) id set rather than List.Contains() on every thumbnail completion.
                // In thousand-item folders the old linear check became measurable UI-thread work.
                if (cancellationToken.IsCancellationRequested || !_currentItemIds.Contains(item.Id) ||
                    (IsMobilePlatform && (requireVisibleOnMobile
                        ? !IsMobileThumbnailVisible(item)
                        : !IsMobileThumbnailWanted(item))) ||
                    (!IsMobilePlatform && requireVisibleOnDesktop && !IsDesktopThumbnailWanted(item)))
                {
                    bitmap?.Dispose();
                    bitmap = null;
                    return;
                }

                item.ThumbnailImage?.Dispose();
                item.ThumbnailImage = bitmap;
                TouchMobileThumbnail(item);
                bitmap = null;
            }, DispatcherPriority.Background);''',
    "background priority thumbnail presentation",
)
replace_once(
    vm,
    '''                Dispatcher.UIThread.Post(UpdateCacheStatus);''',
    '''                Dispatcher.UIThread.Post(UpdateCacheStatus, DispatcherPriority.Background);''',
    "background priority cache status",
)


# 4) Split each 200-item background metadata page into small desktop presentation slices. Network
# enumeration still runs at full page size; only UI slot hydration is sliced so wheel input can run
# between chunks instead of waiting for one long Dispatcher callback.
replace_once(
    vm,
    '''                            AppendBackgroundMetadataPage(0, first.Items, first.NextLink);
                            firstApplied = true;''',
    '''                            AppendBackgroundMetadataPageHeader(0, first.Items, first.NextLink);
                            firstApplied = true;''',
    "first metadata page header",
)
replace_once(
    vm,
    '''                    }
                }
            }

            while (!string.IsNullOrWhiteSpace(cursor))''',
    '''                    }

                    if (firstApplied && navigationVersion == _folderNavigationVersion &&
                        FolderCacheKey(CurrentFolderId) == cacheKey &&
                        !(IsMobilePlatform && !string.IsNullOrWhiteSpace(SearchText)))
                    {
                        if (!await PresentBackgroundSlotsInSlicesAsync(
                                0, first.Items, cacheKey, navigationVersion, token).ConfigureAwait(false))
                            return;
                    }
                }
            }

            while (!string.IsNullOrWhiteSpace(cursor))''',
    "slice first background page",
)
replace_once(
    vm,
    '''                            AppendBackgroundMetadataPage(offset, page.Items, page.NextLink);
                            applied = true;''',
    '''                            AppendBackgroundMetadataPageHeader(offset, page.Items, page.NextLink);
                            applied = true;''',
    "subsequent metadata page header",
)
replace_once(
    vm,
    '''                    }
                }
            }

            token.ThrowIfCancellationRequested();
            var finalCount = collected.Count;''',
    '''                    }

                    if (applied && navigationVersion == _folderNavigationVersion &&
                        FolderCacheKey(CurrentFolderId) == cacheKey &&
                        !(IsMobilePlatform && !string.IsNullOrWhiteSpace(SearchText)))
                    {
                        if (!await PresentBackgroundSlotsInSlicesAsync(
                                offset, page.Items, cacheKey, navigationVersion, token).ConfigureAwait(false))
                            return;
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            var finalCount = collected.Count;''',
    "slice subsequent background pages",
)
replace_once(
    vm,
    '''    private void AppendBackgroundMetadataPage(int offset, IReadOnlyList<DriveItemModel> pageItems, string? nextLink)
    {
        _nextChildrenLink = nextLink;
        HasMoreItems = !string.IsNullOrWhiteSpace(nextLink);

        if (_folderCache.TryGetValue(FolderCacheKey(CurrentFolderId), out var entry))
        {
            entry.Items.AddRange(pageItems);
            entry.NextLink = nextLink;
            entry.LastAccessUtc = DateTimeOffset.UtcNow;
        }

        _allItems.AddRange(pageItems);
        foreach (var pageItem in pageItems)
        {
            if (!string.IsNullOrWhiteSpace(pageItem.Id))
                _currentItemIds.Add(pageItem.Id);
            pageItem.IsMobileSelectionMode = MobileSelectionModeActive;
        }

        if (IsMobilePlatform && !string.IsNullOrWhiteSpace(SearchText))
        {
            ApplyFilterAndSort();
        }
        else
        {
            AppendLoadedPageToVisibleItems(pageItems);
            FillMobileSlots(offset, pageItems);
        }

    }''',
    '''    private void AppendBackgroundMetadataPageHeader(int offset, IReadOnlyList<DriveItemModel> pageItems, string? nextLink)
    {
        _nextChildrenLink = nextLink;
        HasMoreItems = !string.IsNullOrWhiteSpace(nextLink);

        if (_folderCache.TryGetValue(FolderCacheKey(CurrentFolderId), out var entry))
        {
            entry.Items.AddRange(pageItems);
            entry.NextLink = nextLink;
            entry.LastAccessUtc = DateTimeOffset.UtcNow;
        }

        _allItems.AddRange(pageItems);
        foreach (var pageItem in pageItems)
        {
            if (!string.IsNullOrWhiteSpace(pageItem.Id))
                _currentItemIds.Add(pageItem.Id);
            pageItem.IsMobileSelectionMode = MobileSelectionModeActive;
        }

        if (IsMobilePlatform && !string.IsNullOrWhiteSpace(SearchText))
        {
            ApplyFilterAndSort();
            return;
        }

        // Items is a non-visual desktop compatibility shadow. One AddRange is cheap; the expensive
        // fixed-slot presentation is deliberately sliced below.
        AppendLoadedPageToVisibleItems(pageItems);
    }

    private async Task<bool> PresentBackgroundSlotsInSlicesAsync(
        int offset,
        IReadOnlyList<DriveItemModel> pageItems,
        string cacheKey,
        long navigationVersion,
        CancellationToken cancellationToken)
    {
        // Mobile native lists are already highly optimized and rely on the page-level MobileItems
        // signal, so keep their existing whole-page hydration behavior.
        if (IsMobilePlatform)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (navigationVersion == _folderNavigationVersion && FolderCacheKey(CurrentFolderId) == cacheKey)
                    FillMobileSlots(offset, pageItems);
            }, DispatcherPriority.Background);
            return navigationVersion == _folderNavigationVersion;
        }

        const int sliceSize = 24;
        for (var sliceStart = 0; sliceStart < pageItems.Count; sliceStart += sliceSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(sliceSize, pageItems.Count - sliceStart);
            var slice = new DriveItemModel[count];
            for (var i = 0; i < count; i++)
                slice[i] = pageItems[sliceStart + i];

            var presented = false;
            while (!presented)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Do not even enqueue a background slot mutation while input is active.
                while (_desktopListScrolling)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(24, cancellationToken).ConfigureAwait(false);
                }

                var result = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (navigationVersion != _folderNavigationVersion || FolderCacheKey(CurrentFolderId) != cacheKey)
                        return -1;
                    if (_desktopListScrolling)
                        return 0;

                    FillMobileSlots(offset + sliceStart, slice);
                    return 1;
                }, DispatcherPriority.Background);

                if (result < 0)
                    return false;
                presented = result > 0;
                if (!presented)
                    await Task.Delay(24, cancellationToken).ConfigureAwait(false);
            }
        }

        return true;
    }''',
    "slice background slot presentation helper",
)

print("Desktop input-priority patch applied")
