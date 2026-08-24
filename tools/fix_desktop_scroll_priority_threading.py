from pathlib import Path

path = Path(__file__).resolve().parents[1] / "src/Hello1Drive.Core/ViewModels/MainViewModel.cs"
text = path.read_text(encoding="utf-8")

replacements = [
('''                    if (firstApplied && navigationVersion == _folderNavigationVersion &&
                        FolderCacheKey(CurrentFolderId) == cacheKey &&
                        !(IsMobilePlatform && !string.IsNullOrWhiteSpace(SearchText)))
                    {
                        if (!await PresentBackgroundSlotsInSlicesAsync(
                                0, first.Items, cacheKey, navigationVersion, token).ConfigureAwait(false))
                            return;
                    }''',
'''                    if (firstApplied && !await PresentBackgroundSlotsInSlicesAsync(
                            0, first.Items, cacheKey, navigationVersion, token).ConfigureAwait(false))
                        return;'''),
('''                    if (applied && navigationVersion == _folderNavigationVersion &&
                        FolderCacheKey(CurrentFolderId) == cacheKey &&
                        !(IsMobilePlatform && !string.IsNullOrWhiteSpace(SearchText)))
                    {
                        if (!await PresentBackgroundSlotsInSlicesAsync(
                                offset, page.Items, cacheKey, navigationVersion, token).ConfigureAwait(false))
                            return;
                    }''',
'''                    if (applied && !await PresentBackgroundSlotsInSlicesAsync(
                            offset, page.Items, cacheKey, navigationVersion, token).ConfigureAwait(false))
                        return;'''),
('''        if (IsMobilePlatform)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (navigationVersion == _folderNavigationVersion && FolderCacheKey(CurrentFolderId) == cacheKey)
                    FillMobileSlots(offset, pageItems);
            }, DispatcherPriority.Background);
            return navigationVersion == _folderNavigationVersion;
        }''',
'''        if (IsMobilePlatform)
        {
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (navigationVersion != _folderNavigationVersion || FolderCacheKey(CurrentFolderId) != cacheKey)
                    return false;

                // Search-mode header presentation already rebuilt the filtered slot collection.
                if (string.IsNullOrWhiteSpace(SearchText))
                    FillMobileSlots(offset, pageItems);
                return true;
            }, DispatcherPriority.Background);
        }''')
]

for old, new in replacements:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"expected one match, found {count}")
    text = text.replace(old, new, 1)

path.write_text(text, encoding="utf-8")
print("threading cleanup applied")
