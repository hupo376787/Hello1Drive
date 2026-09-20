using System.Runtime.InteropServices;
using Hello1Drive.Models;

namespace Hello1Drive.Desktop.Services;

internal sealed partial class WindowsNativeDesktopFileListController
{
    private void RestoreSelection()
    {
        if (_viewModel is null)
            return;

        var selectedIds = _viewModel.SelectedItemsSnapshot
            .Select(static x => x.Id)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);

        _synchronizingSelection = true;
        try
        {
            _nativeSelectedIndices.Clear();

            // -1 applies the state change to all items. This avoids one SendMessage per file in
            // large virtual folders, then restores only the handful of actually selected items.
            SetItemSelected(-1, selected: false);

            if (selectedIds.Count == 0)
                return;

            for (var i = 0; i < _viewModel.VirtualItems.Count; i++)
            {
                var item = _viewModel.VirtualItems[i].Item;
                if (item is not null && selectedIds.Contains(item.Id))
                    SetItemSelected(i, selected: true);
            }
        }
        finally
        {
            _synchronizingSelection = false;
        }
    }

    private void SetItemSelected(int index, bool selected)
    {
        var state = new LVITEM
        {
            stateMask = LVIS_SELECTED,
            state = selected ? LVIS_SELECTED : 0
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<LVITEM>());
        try
        {
            Marshal.StructureToPtr(state, ptr, false);
            SendMessage(ListHandle, LVM_SETITEMSTATE, (nint)index, ptr);

            if (index < 0)
            {
                if (!selected)
                    _nativeSelectedIndices.Clear();
            }
            else if (selected)
            {
                _nativeSelectedIndices.Add(index);
            }
            else
            {
                _nativeSelectedIndices.Remove(index);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private bool IsItemSelected(int index) => _nativeSelectedIndices.Contains(index);

    private void RaiseSelectionChanged()
    {
        if (_synchronizingSelection || _viewModel is null)
            return;

        var ids = _nativeSelectedIndices
            .Where(index => index >= 0 && index < _viewModel.VirtualItems.Count)
            .OrderBy(static index => index)
            .Select(index => _viewModel.VirtualItems[index].Item?.Id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToArray();

        _host.RaiseSelectionChanged(ids);
    }

    private int HitTestIndex(nint lParam)
    {
        if (_viewModel is null)
            return -1;

        var x = unchecked((short)((long)lParam & 0xFFFF));
        var y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));

        if (_viewModel.ViewMode != FileViewMode.Details)
        {
            var metrics = CalculateNativeGridMetrics();
            if (!_cachedIconGeometryValid || _cachedIconColumnLefts.Length != metrics.Columns)
                RefreshNativeIconGeometry(metrics);
            if (!_cachedIconGeometryValid)
                return -1;

            var origin = GetNativeViewOrigin();
            var viewY = y + origin.y;
            var relativeY = viewY - _cachedIconFirstTop;
            if (relativeY < 0)
                return -1;

            var rowPitch = Math.Max(1, _cachedIconRowPitch);
            var row = relativeY / rowPitch;
            var rowOffset = relativeY % rowPitch;
            if (rowOffset >= metrics.CellHeight)
                return -1;

            var edgeMargin = ScaleInt(GridOuterMargin);
            for (var column = 0; column < metrics.Columns; column++)
            {
                if (_cachedIconColumnLefts[column] == int.MinValue)
                {
                    var columnItem = column;
                    if (columnItem >= _viewModel.VirtualItems.Count ||
                        !TryGetNativeItemViewPosition(columnItem, out var position))
                    {
                        continue;
                    }

                    var iconWidth = ScaleInt(
                        _viewModel.ViewMode == FileViewMode.ExtraLargeIcons ? ExtraArtwork : LargeArtwork);
                    var horizontalInset = Math.Max(0, (metrics.CellWidth - iconWidth) / 2);
                    _cachedIconColumnLefts[column] =
                        (position.x - horizontalInset) - _cachedIconFirstBaseLeft;
                }

                var left = _cachedIconColumnLefts[column] - origin.x;
                var right = left + metrics.CellWidth;
                if (column == 0)
                    left += edgeMargin;
                if (column == metrics.Columns - 1)
                    right -= edgeMargin;

                if (x < left || x >= right)
                    continue;

                var index = row * metrics.Columns + column;
                return index >= 0 && index < _viewModel.VirtualItems.Count ? index : -1;
            }

            return -1;
        }

        var point = new LVHITTESTINFO
        {
            pt = new POINT { x = x, y = y }
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<LVHITTESTINFO>());
        try
        {
            Marshal.StructureToPtr(point, ptr, false);
            var index = (int)SendMessage(ListHandle, LVM_HITTEST, 0, ptr);
            return index >= 0 && index < _viewModel.VirtualItems.Count ? index : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private DriveItemModel? HitTest(nint lParam)
    {
        var index = HitTestIndex(lParam);
        return _viewModel is not null && index >= 0 && index < _viewModel.VirtualItems.Count
            ? _viewModel.VirtualItems[index].Item
            : null;
    }

    private int GetFirstVisibleIndex()
    {
        if (_viewModel is null || _viewModel.VirtualItems.Count == 0)
            return 0;

        if (_viewModel.ViewMode == FileViewMode.Details)
        {
            return Math.Clamp((int)SendMessage(ListHandle, LVM_GETTOPINDEX, 0, 0),
                0, _viewModel.VirtualItems.Count - 1);
        }

        var visible = GetVisibleIconIndices();
        if (visible.Count > 0)
            return visible[0];

        var range = GetEstimatedVisibleIconIndexRange();
        return range.First >= 0 ? range.First : 0;
    }

    private (int First, int Last) GetVisibleIndexRange()
    {
        if (_viewModel is null || _viewModel.VirtualItems.Count == 0)
            return (-1, -1);

        if (_viewModel.ViewMode != FileViewMode.Details)
        {
            var visible = GetVisibleIconIndices();
            if (visible.Count > 0)
                return (visible[0], visible[^1]);
            return GetEstimatedVisibleIconIndexRange();
        }

        var first = Math.Clamp((int)SendMessage(ListHandle, LVM_GETTOPINDEX, 0, 0),
            0, _viewModel.VirtualItems.Count - 1);
        GetClientRect(ListHandle, out var client);
        var count = Math.Max(1, client.Height / Math.Max(1, ScaleInt(DetailsRowHeight)) + 3);
        return (first, Math.Min(_viewModel.VirtualItems.Count - 1, first + count - 1));
    }

    private List<int> GetVisibleIconIndices()
    {
        var result = new List<int>();
        if (_viewModel is null || _viewModel.VirtualItems.Count == 0 || ListHandle == 0)
            return result;

        // LVNI_VISIBLEONLY is the native Vista+ contract for asking SysListView32 which items are
        // actually visible. It is deliberately used by itself because Microsoft documents it as
        // mutually exclusive with the other LVM_GETNEXTITEM flags.
        var current = -1;
        while (true)
        {
            current = (int)SendMessage(ListHandle, LVM_GETNEXTITEM, (nint)current, (nint)LVNI_VISIBLEONLY);
            if (current < 0)
                break;
            if (current < _viewModel.VirtualItems.Count)
                result.Add(current);
        }

        result.Sort();
        return result;
    }

    private (int First, int Last) GetEstimatedVisibleIconIndexRange()
    {
        if (_viewModel is null || _viewModel.VirtualItems.Count == 0 || ListHandle == 0)
            return (-1, -1);

        GetClientRect(ListHandle, out var client);
        var metrics = CalculateNativeGridMetrics();
        var pitchY = Math.Max(1, metrics.CellHeight + metrics.Gap);
        var origin = GetNativeViewOrigin();
        var scrollY = Math.Max(0, origin.y);
        var firstRow = Math.Max(0, scrollY / pitchY);
        var lastPixel = scrollY + Math.Max(1, client.Height) - 1;
        var lastRow = Math.Max(firstRow, lastPixel / pitchY);
        var first = firstRow * metrics.Columns;
        var last = Math.Min(_viewModel.VirtualItems.Count - 1,
            ((lastRow + 1) * metrics.Columns) - 1);

        if (first >= _viewModel.VirtualItems.Count)
        {
            first = Math.Max(0, _viewModel.VirtualItems.Count - metrics.Columns);
            last = _viewModel.VirtualItems.Count - 1;
        }

        return (first, Math.Max(first, last));
    }

    private void ReportScrollPosition()
    {
        if (_viewModel is null)
            return;
        var range = GetVisibleIndexRange();
        if (range.First < 0)
            return;
        _host.RaiseScrollStateChanged(range.First, range.Last);
    }

    private void QueueVisibleThumbnails(bool allowNetwork)
    {
        if (_viewModel is null || _viewModel.VirtualItems.Count == 0)
        {
            _visibleNativeThumbnailIds.Clear();
            return;
        }

        if (_viewModel.ViewMode != FileViewMode.Details)
        {
            var indices = GetVisibleIconIndices();
            if (indices.Count == 0)
            {
                var estimated = GetEstimatedVisibleIconIndexRange();
                if (estimated.First < 0 || estimated.Last < estimated.First)
                    return;
                for (var i = estimated.First; i <= estimated.Last; i++)
                    indices.Add(i);
            }

            var visibleNativeItems = new List<DriveItemModel>(indices.Count);
            foreach (var visibleIndex in indices)
            {
                if (_viewModel.VirtualItems[visibleIndex].Item is { } visibleItem)
                    visibleNativeItems.Add(visibleItem);
            }
            UpdateVisibleNativeThumbnailPins(visibleNativeItems);

            // Prefetch one complete row after the last native-visible item. The visible set itself
            // comes from SysListView32, so thumbnail hydration can no longer drift away from the
            // actual icon viewport after a long wheel fling.
            var metrics = CalculateNativeGridMetrics();
            var lastVisible = indices[^1];
            for (var i = 1; i <= metrics.Columns; i++)
            {
                var next = lastVisible + i;
                if (next >= _viewModel.VirtualItems.Count)
                    break;
                indices.Add(next);
            }

            var distinct = indices.Distinct().OrderBy(static x => x).ToList();
            var items = new List<DriveItemModel>(distinct.Count);
            var actual = new List<int>(distinct.Count);
            foreach (var index in distinct)
            {
                if (_viewModel.VirtualItems[index].Item is not { } item)
                    continue;
                actual.Add(index);
                items.Add(item);
            }

            if (items.Count > 0)
                _viewModel.UpdateDesktopRealizedThumbnails(actual, items, allowNetwork);
            return;
        }

        var (first, last) = GetVisibleIndexRange();
        if (first < 0 || last < first)
            return;

        var detailIndices = new List<int>(last - first + 1);
        var detailItems = new List<DriveItemModel>(last - first + 1);
        for (var i = first; i <= last; i++)
        {
            if (_viewModel.VirtualItems[i].Item is not { } item)
                continue;
            detailIndices.Add(i);
            detailItems.Add(item);
        }

        UpdateVisibleNativeThumbnailPins(detailItems);
        _viewModel.UpdateDesktopRealizedThumbnails(detailIndices, detailItems, allowNetwork);
    }

    private void BeginNativeScroll()
    {
        if (_viewModel is null)
            return;

        if (!_scrolling)
        {
            _scrolling = true;
            _viewModel.SetDesktopListScrolling(true);

            if (_hotIndex >= 0)
            {
                var old = _hotIndex;
                _hotIndex = -1;
                RedrawHoverItem(old);
            }
        }

        // Wheel/trackpad input can arrive dozens of times per second. Do not walk visible items,
        // touch thumbnail state, or publish scroll history for every delta; Common Controls can
        // scroll its double buffer directly. The idle timer performs one consolidated recovery.
        SetTimer(ListHandle, (nuint)ScrollIdleTimerId, 120, 0);
    }

    private void EndNativeScroll()
    {
        KillTimer(ListHandle, (nuint)ScrollIdleTimerId);
        if (_viewModel is null)
            return;

        if (_scrolling)
        {
            _scrolling = false;
            _viewModel.SetDesktopListScrolling(false);
        }

        ReportScrollPosition();
        QueueVisibleThumbnails(allowNetwork: true);

        // Painted indices are accumulated while the native double-buffer scrolls. Flush them once
        // after motion stops instead of posting thumbnail work from every intermediate paint.
        ScheduleNativePaintedThumbnailFlush();
    }

    private void UpdateHotItem(nint lParam)
    {
        if (_viewModel?.TransparentFileItemBackground == true)
        {
            if (_hotIndex >= 0)
                ClearHotItem();
            return;
        }

        if (!_trackingMouseLeave)
        {
            var tracking = new TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                dwFlags = TME_LEAVE,
                hwndTrack = ListHandle
            };
            TrackMouseEvent(ref tracking);
            _trackingMouseLeave = true;
        }

        var next = HitTestIndex(lParam);
        if (next == _hotIndex)
            return;

        var old = _hotIndex;
        _hotIndex = next;
        if (old >= 0)
            RedrawHoverItem(old);
        if (next >= 0)
            RedrawHoverItem(next);
    }

    private void ClearHotItem()
    {
        _trackingMouseLeave = false;
        if (_hotIndex < 0)
            return;
        var old = _hotIndex;
        _hotIndex = -1;
        RedrawHoverItem(old);
    }

    private void RedrawHoverItem(int index)
    {
        if (_viewModel is null || index < 0 || index >= _viewModel.VirtualItems.Count)
            return;

        if (_viewModel.VirtualItems[index].Item is { } item && item.SupportsThumbnail)
        {
            if (_thumbnailCache.TryGetValue(item.Id, out var cached) &&
                string.Equals(cached.VersionToken, item.VersionToken, StringComparison.Ordinal))
            {
                TouchThumbnail(cached);
            }
            else if (item.ThumbnailImage is not null)
            {
                // Preserve the currently displayed pixels until the scaled native copy is ready.
                // Invalidating now would clear the old thumbnail first and briefly replace it with
                // a generic file badge, which looked like the thumbnail vanished on hover.
                QueueNativeThumbnailPreparation(item);
                return;
            }
        }

        RedrawItem(index);
    }

    private void RedrawItem(int index)
    {
        if (index < 0)
            return;
        InvalidateNativeItemRange(index, index);
    }

    private void InvalidateVisibleItems()
    {
        var (first, last) = GetVisibleIndexRange();
        if (first < 0)
            return;
        InvalidateNativeItemRange(first, last);
    }
}
