using Hello1Drive.Models;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Controls;

/// <summary>
/// Stable folder viewport anchor shared by native Android/iOS and Windows lists.
/// It preserves the original first-visible slot even when that slot is still a virtual placeholder:
/// a nearby loaded OneDrive item supplies identity, while the slot delta keeps the actual viewport
/// index unchanged instead of jumping to the helper item itself.
/// </summary>
internal readonly record struct NativeFolderViewportAnchor(
    string? ItemId,
    int ItemIndex,
    int FirstVisibleIndex);

internal static class NativeFolderViewportAnchorResolver
{
    public static NativeFolderViewportAnchor Capture(
        IList<VirtualDriveItemSlot> slots,
        int firstVisibleIndex)
    {
        if (slots.Count == 0)
            return new NativeFolderViewportAnchor(null, 0, 0);

        var first = Math.Clamp(firstVisibleIndex, 0, slots.Count - 1);
        if (slots[first].Item is { Id.Length: > 0 } exact)
            return new NativeFolderViewportAnchor(exact.Id, first, first);

        // Metadata can lag behind the fixed virtual slot collection. Find a nearby loaded item only
        // as an identity reference. The delta back to the real first-visible slot is stored as well,
        // so choosing a helper item can never move the restored viewport by several rows/a page.
        const int searchRadius = 64;
        for (var distance = 1; distance <= searchRadius; distance++)
        {
            var before = first - distance;
            if (before >= 0 && slots[before].Item is { Id.Length: > 0 } beforeItem)
                return new NativeFolderViewportAnchor(beforeItem.Id, before, first);

            var after = first + distance;
            if (after < slots.Count && slots[after].Item is { Id.Length: > 0 } afterItem)
                return new NativeFolderViewportAnchor(afterItem.Id, after, first);
        }

        return new NativeFolderViewportAnchor(null, first, first);
    }

    public static int Resolve(
        IList<VirtualDriveItemSlot> slots,
        NativeFolderViewportAnchor anchor)
    {
        if (slots.Count == 0)
            return 0;

        var target = anchor.FirstVisibleIndex;
        if (!string.IsNullOrWhiteSpace(anchor.ItemId))
        {
            for (var i = 0; i < slots.Count; i++)
            {
                if (!string.Equals(slots[i].Item?.Id, anchor.ItemId, StringComparison.Ordinal))
                    continue;

                target = i + (anchor.FirstVisibleIndex - anchor.ItemIndex);
                break;
            }
        }

        return Math.Clamp(target, 0, slots.Count - 1);
    }

    public static string FolderKey(MainViewModel viewModel) =>
        string.IsNullOrWhiteSpace(viewModel.CurrentFolderId) ? "__ROOT__" : viewModel.CurrentFolderId;
}
