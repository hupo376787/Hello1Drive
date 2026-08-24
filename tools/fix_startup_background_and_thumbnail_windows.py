from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one match, found {count}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1) Apply non-OneDrive backgrounds immediately instead of waiting for authentication / folder sync.
main_view = ROOT / "src/Hello1Drive.Core/Views/MainView.axaml.cs"
replace_once(
    main_view,
    '''            ApplyStartupBackgroundShell(vm);

            // Start initialization immediately, but keep the splash only for a short, fixed
            // first-frame interval. With a startup snapshot the cached directory is already
            // restored behind the splash, so network synchronization must not lengthen it.
            var initializeTask = vm.InitializeAsync();''',
    '''            ApplyStartupBackgroundShell(vm);

            // Local / URL backgrounds do not depend on OneDrive authentication. Start resolving
            // them immediately so the real wallpaper is already visible when the splash fades,
            // instead of waiting several seconds for account/folder synchronization to finish.
            var startupBackgroundTask = vm.Settings.BackgroundMode == WindowBackgroundMode.OneDriveFolder
                ? Task.CompletedTask
                : ApplyWindowBackgroundAsync();

            // Start initialization immediately, but keep the splash only for a short, fixed
            // first-frame interval. With a startup snapshot the cached directory is already
            // restored behind the splash, so network synchronization must not lengthen it.
            var initializeTask = vm.InitializeAsync();''',
    "start background before initialization",
)
replace_once(
    main_view,
    '''            await initializeTask;

            // URL / local-folder / OneDrive backgrounds are decorative and can involve disk or
            // network I/O. Load them only after the OneDrive startup path has been released.
            _ = ApplyWindowBackgroundAsync();
            _ = TryResumePersistedTransfersAsync(vm);''',
    '''            await initializeTask;

            // OneDrive-folder wallpaper needs an authenticated Graph session, so resolve only
            // that mode after initialization. Other modes were already started before the splash.
            if (vm.Settings.BackgroundMode == WindowBackgroundMode.OneDriveFolder)
                _ = ApplyWindowBackgroundAsync();
            else
                _ = startupBackgroundTask;

            _ = TryResumePersistedTransfersAsync(vm);''',
    "avoid delayed duplicate startup background",
)

# 2) Desktop: treat one viewport before/after the realized viewport as the wanted thumbnail window.
replace_once(
    main_view,
    '''        vm.UpdateDesktopRealizedThumbnails(visibleSlotIndices, visibleItems, allowNetwork);
    }

    private async Task RecoverVisibleDesktopThumbnailsAfterIdleAsync(''',
    '''        // Keep one complete viewport before and after the current viewport warm. The current
        // items stay first in visibleItems so they enter the shared worker gate before look-ahead
        // candidates. Slot indices are included even when metadata has not arrived yet; the VM's
        // page hydration path will pick them up as soon as their DriveItemModel becomes available.
        if (visibleSlotIndices.Count > 0 && vm.MobileItems.Count > 0)
        {
            var visibleFirst = visibleSlotIndices.Min();
            var visibleLast = visibleSlotIndices.Max();
            var pageSize = Math.Max(1, visibleLast - visibleFirst + 1);
            var windowFrom = Math.Max(0, visibleFirst - pageSize);
            var windowToExclusive = Math.Min(vm.MobileItems.Count, visibleLast + pageSize + 1);

            for (var index = windowFrom; index < windowToExclusive; index++)
            {
                if (seenSlots.Add(index))
                    visibleSlotIndices.Add(index);

                if (vm.MobileItems[index].Item is { } item &&
                    !string.IsNullOrWhiteSpace(item.Id) &&
                    seenItems.Add(item.Id))
                {
                    visibleItems.Add(item);
                }
            }
        }

        vm.UpdateDesktopRealizedThumbnails(visibleSlotIndices, visibleItems, allowNetwork);
    }

    private async Task RecoverVisibleDesktopThumbnailsAfterIdleAsync(''',
    "desktop adjacent viewport thumbnails",
)

# 3) Tell native mobile surfaces when fixed slots receive metadata. CollectionChanged does not fire for SetItem.
vm = ROOT / "src/Hello1Drive.Core/ViewModels/MainViewModel.cs"
replace_once(
    vm,
    '''        if (intersectsThumbnailWindow)
            RefreshMobileThumbnailWantedIds();

        if (!IsMobilePlatform && desktopVisibleIds is not null)''',
    '''        // Native Android/iOS lists use fixed VirtualDriveItemSlot instances. Hydrating an
        // existing slot does not raise MobileItems.CollectionChanged, so publish one lightweight
        // page-level signal after SetItem calls. Native adapters use it to retry the current +/-1
        // viewport thumbnail window when metadata arrives after their first layout pass.
        if (UsesNativeMobileFileList)
            OnPropertyChanged(nameof(MobileItems));

        if (intersectsThumbnailWindow)
            RefreshMobileThumbnailWantedIds();

        if (!IsMobilePlatform && desktopVisibleIds is not null)''',
    "native slot hydration signal",
)
replace_once(
    vm,
    '''    private readonly SemaphoreSlim _desktopThumbnailWorkGate = new(6, 6);''',
    '''    // Four desktop workers are enough to fill a three-viewport look-ahead window without
    // letting cached bitmap decode compete too aggressively with Avalonia layout/rendering.
    private readonly SemaphoreSlim _desktopThumbnailWorkGate = new(4, 4);''',
    "desktop thumbnail worker cap",
)

# 4) Android: retry the three-screen native thumbnail window after first layout and page hydration.
android = ROOT / "src/Hello1Drive.Android/Services/AndroidNativeMobileFileListFactory.cs"
replace_once(
    android,
    '''    private void Root_LayoutChange(object? sender, View.LayoutChangeEventArgs e)
    {
        if (!_disposed)
            PositionFloatingUpload();
    }''',
    '''    private void Root_LayoutChange(object? sender, View.LayoutChangeEventArgs e)
    {
        if (_disposed)
            return;

        PositionFloatingUpload();
        _recycler.PostDelayed(() =>
        {
            if (!_disposed && !_scrolling)
                _adapter.StartVisibleThumbnailWork();
        }, 32);
    }''',
    "Android first-layout thumbnail prefetch",
)
replace_once(
    android,
    '''        if (e.PropertyName == nameof(MainViewModel.ShowFloatingUploadButton))
        {
            SyncFloatingUpload();
            return;
        }''',
    '''        if (e.PropertyName == nameof(MainViewModel.MobileItems))
        {
            // Fixed slots may have been placeholders during the previous viewport scan. Retry
            // after each hydrated Graph page so previous/current/next viewport thumbnails really
            // start as soon as their metadata becomes available.
            _recycler.PostDelayed(() =>
            {
                if (!_disposed && !_scrolling)
                    _adapter.StartVisibleThumbnailWork();
            }, 24);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.ShowFloatingUploadButton))
        {
            SyncFloatingUpload();
            return;
        }''',
    "Android hydrated-page thumbnail retry",
)

# 5) iOS: same retry behavior for UICollectionView.
ios = ROOT / "src/Hello1Drive.iOS/Services/IosNativeMobileFileListFactory.cs"
replace_once(
    ios,
    '''        if (e.PropertyName == nameof(MainViewModel.ShowFloatingUploadButton))
        {
            SyncFloatingUpload();
            return;
        }''',
    '''        if (e.PropertyName == nameof(MainViewModel.MobileItems))
        {
            _collection.BeginInvokeOnMainThread(() =>
            {
                if (_disposed || _scrolling)
                    return;
                _collection.LayoutIfNeeded();
                _source.StartVisibleThumbnailWork();
            });
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.ShowFloatingUploadButton))
        {
            SyncFloatingUpload();
            return;
        }''',
    "iOS hydrated-page thumbnail retry",
)
replace_once(
    ios,
    '''        _collection.Frame = _root.Bounds;
        PositionFloatingUpload();
    }''',
    '''        _collection.Frame = _root.Bounds;
        PositionFloatingUpload();
        _collection.BeginInvokeOnMainThread(() =>
        {
            if (_disposed || _scrolling)
                return;
            _collection.LayoutIfNeeded();
            _source.StartVisibleThumbnailWork();
        });
    }''',
    "iOS first-layout thumbnail prefetch",
)

print("Applied startup background, desktop viewport and native thumbnail hydration fixes.")
