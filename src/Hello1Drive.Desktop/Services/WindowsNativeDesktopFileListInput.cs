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
            for (var i = 0; i < _viewModel.VirtualItems.Count; i++)
            {
                var item = _viewModel.VirtualItems[i].Item;
                SetItemSelected(i, item is not null && selectedIds.Contains(item.Id));
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
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private bool IsItemSelected(int index) =>
        (((long)SendMessage(ListHandle, LVM_GETITEMSTATE, (nint)index, (nint)LVIS_SELECTED)) & (long)LVIS_SELECTED) != 0;

    private void RaiseSelectionChanged()
    {
        if (_synchronizingSelection || _viewModel is null)
            return;

        var ids = new List<string>();
        var current = -1;
        while (true)
        {
            current = (int)SendMessage(ListHandle, LVM_GETNEXTITEM, (nint)current, (nint)LVNI_SELECTED);
            if (current < 0)
                break;
            if (current < _viewModel.VirtualItems.Count && _viewModel.VirtualItems[current].Item is { Id.Length: > 0 } item)
                ids.Add(item.Id);
        }
        _host.RaiseSelectionChanged(ids);
    }

    private int HitTestIndex(nint lParam)
    {
        if (_viewModel is null)
            return -1;

        var point = new LVHITTESTINFO
        {
            pt = new POINT
            {
                x = unchecked((short)((long)lParam & 0xFFFF)),
                y = unchecked((short)(((long)lParam >> 16) & 0xFFFF))
            }
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
        var scrollY = Math.Max(0, -origin.y);
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
            return;

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
        }

        if (_hotIndex >= 0)
        {
            var old = _hotIndex;
            _hotIndex = -1;
            RedrawItem(old);
        }

        SetTimer(ListHandle, (nuint)ScrollIdleTimerId, 150, 0);
        ReportScrollPosition();
        QueueVisibleThumbnails(allowNetwork: false);
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
        InvalidateVisibleItems();
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
            RedrawItem(old);
        if (next >= 0)
            RedrawItem(next);
    }

    private void ClearHotItem()
    {
        _trackingMouseLeave = false;
        if (_hotIndex < 0)
            return;
        var old = _hotIndex;
        _hotIndex = -1;
        RedrawItem(old);
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
