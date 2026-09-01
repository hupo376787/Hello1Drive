using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Hello1Drive.Controls;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;
using Microsoft.Win32;

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

        // LVM_GETTOPINDEX is documented for list/report views only. LVNI_VISIBLEONLY also works
        // for icon views, so large/extra-large folders queue thumbnails for the actual native
        // viewport instead of repeatedly treating the first row as visible.
        var visible = (int)SendMessage(ListHandle, LVM_GETNEXTITEM, (nint)(-1), (nint)LVNI_VISIBLEONLY);
        if (visible >= 0)
            return Math.Clamp(visible, 0, _viewModel.VirtualItems.Count - 1);

        return Math.Clamp((int)SendMessage(ListHandle, LVM_GETTOPINDEX, 0, 0), 0, _viewModel.VirtualItems.Count - 1);
    }

    private (int First, int Last) GetVisibleIndexRange()
    {
        if (_viewModel is null || _viewModel.VirtualItems.Count == 0)
            return (-1, -1);

        var first = GetFirstVisibleIndex();
        GetClientRect(ListHandle, out var client);
        var mode = _viewModel.ViewMode;
        int count;
        if (mode == FileViewMode.Details)
        {
            count = Math.Max(1, client.Height / Math.Max(1, ScaleInt(DetailsRowHeight)) + 3);
        }
        else
        {
            var extra = mode == FileViewMode.ExtraLargeIcons;
            var pitchX = Math.Max(1, ScaleInt((extra ? ExtraWidth : LargeWidth) + GridSpacing));
            var pitchY = Math.Max(1, ScaleInt((extra ? ExtraHeight : LargeHeight) + GridSpacing));
            var columns = Math.Max(1, client.Width / pitchX);
            var rows = Math.Max(1, client.Height / pitchY + 2);
            count = columns * rows + columns;
        }

        return (first, Math.Min(_viewModel.VirtualItems.Count - 1, first + count - 1));
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

        var (first, last) = GetVisibleIndexRange();
        if (first < 0 || last < first)
            return;

        var indices = new List<int>(last - first + 1);
        var items = new List<DriveItemModel>(last - first + 1);
        for (var i = first; i <= last; i++)
        {
            if (_viewModel.VirtualItems[i].Item is not { } item)
                continue;
            indices.Add(i);
            items.Add(item);
        }

        _viewModel.UpdateDesktopRealizedThumbnails(indices, items, allowNetwork);
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
        SendMessage(ListHandle, LVM_REDRAWITEMS, (nint)index, (nint)index);
    }

    private void InvalidateVisibleItems()
    {
        var (first, last) = GetVisibleIndexRange();
        if (first < 0)
            return;
        SendMessage(ListHandle, LVM_REDRAWITEMS, (nint)first, (nint)last);
    }

}
