using Hello1Drive.Models;

namespace Hello1Drive.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Opens the existing rename prompt for one item or a Windows-style numbered rename prompt for
    /// a multi-selection. Multi-rename preserves every file's original extension and generates
    /// "Name (1)", "Name (2)" ... in the current folder order.
    /// </summary>
    public void BeginSelectionRename(IReadOnlyList<DriveItemModel> selectedItems)
    {
        var requestedIds = selectedItems
            .Where(static item => item is not null && !string.IsNullOrWhiteSpace(item.Id))
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (requestedIds.Count == 0)
            return;

        var ordered = _allItems
            .Where(item => requestedIds.Contains(item.Id))
            .ToList();
        foreach (var item in selectedItems)
        {
            if (!string.IsNullOrWhiteSpace(item.Id) &&
                requestedIds.Contains(item.Id) &&
                ordered.All(existing => !string.Equals(existing.Id, item.Id, StringComparison.Ordinal)))
            {
                ordered.Add(item);
            }
        }

        if (ordered.Count == 1)
        {
            // The existing single-item flow already handles exact rename semantics and validation.
            SetSelectedItems(ordered);
            BeginRename();
            return;
        }

        var first = ordered[0];
        var suggestedBase = first.IsFile
            ? Path.GetFileNameWithoutExtension(first.Name)
            : first.Name;

        PromptTitle = "批量重命名";
        PromptMessage = $"将 {ordered.Count} 个项目依次重命名为“名称 (1)”、“名称 (2)”……；文件扩展名保持不变。";
        PromptText = suggestedBase;
        IsPromptInputVisible = true;
        _promptUseBusy = true;
        _promptAction = async text => await RenameSelectionWithSequenceAsync(ordered, text);
        IsPromptVisible = true;
    }

    private async Task RenameSelectionWithSequenceAsync(
        IReadOnlyList<DriveItemModel> items,
        string? requestedBaseName)
    {
        var rawBase = (requestedBaseName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rawBase))
            throw new InvalidOperationException("名称不能为空。");

        // If the user typed the first file's extension, treat it as part of the example name rather
        // than forcing that extension onto every selected file. This matches Explorer's multi-rename
        // behavior when extensions are normally hidden.
        var firstFileExtension = items.FirstOrDefault(static item => item.IsFile) is { } firstFile
            ? Path.GetExtension(firstFile.Name)
            : string.Empty;
        var typedExtension = Path.GetExtension(rawBase);
        var baseStem = !string.IsNullOrWhiteSpace(typedExtension) &&
                       !string.IsNullOrWhiteSpace(firstFileExtension) &&
                       string.Equals(typedExtension, firstFileExtension, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(rawBase)
            : rawBase;
        baseStem = baseStem.Trim();
        if (string.IsNullOrWhiteSpace(baseStem))
            throw new InvalidOperationException("名称不能为空。");

        var selectedIds = items.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        var occupiedNames = _allItems
            .Where(item => !selectedIds.Contains(item.Id))
            .Select(static item => item.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        var renamed = 0;
        var failures = new List<string>();
        var sequence = 1;

        foreach (var item in items)
        {
            var extension = item.IsFile ? Path.GetExtension(item.Name) : string.Empty;
            string candidate;
            do
            {
                candidate = $"{baseStem} ({sequence}){extension}";
                sequence++;
            }
            while (occupiedNames.Contains(candidate));

            try
            {
                var updated = await _oneDrive.RenameAsync(item.Id, candidate);
                item.ApplyMetadataFrom(updated);
                _thumbnailCache.Invalidate(item.Id);
                _fileCache.Invalidate(item.Id);
                occupiedNames.Add(candidate);
                renamed++;
            }
            catch (Exception ex)
            {
                failures.Add($"{item.Name}：{ex.Message}");
            }
        }

        var cacheKey = FolderCacheKey(CurrentFolderId);
        if (_folderCache.TryGetValue(cacheKey, out var cache))
        {
            cache.LastAccessUtc = DateTimeOffset.UtcNow;
            cache.LastValidatedUtc = DateTimeOffset.MinValue;
        }

        // The same DriveItemModel instances stay attached to the virtual slots, so successful names
        // change in-place with no folder clear/rebuild. A later normal cloud revalidation can adjust
        // ordering if this folder is sorted by name.
        ScheduleStartupSnapshotSave();
        ErrorMessage = failures.Count == 0
            ? null
            : string.Join(Environment.NewLine, failures.Take(3));
        StatusText = failures.Count == 0
            ? $"已重命名 {renamed} 个项目"
            : $"已重命名 {renamed} 项，{failures.Count} 项失败";
    }
}
