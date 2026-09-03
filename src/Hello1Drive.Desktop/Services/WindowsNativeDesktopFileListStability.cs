using System.Runtime.InteropServices;
using Avalonia.Threading;
using Hello1Drive.Models;

namespace Hello1Drive.Desktop.Services;

internal sealed partial class WindowsNativeDesktopFileListController
{
    private const int LVM_SCROLL_NATIVE = LVM_FIRST + 20;
    private const int SB_HORZ_NATIVE = 0;

    private bool _nativeRedrawFlushScheduled;
    private int _pendingNativeRedrawFirst = int.MaxValue;
    private int _pendingNativeRedrawLast = -1;
    private readonly HashSet<int> _nativePaintedIndices = [];
    private bool _nativePaintedFlushScheduled;

    private void QueueNativeItemRedraw(int index)
    {
        if (_disposed || index < 0)
            return;

        _pendingNativeRedrawFirst = Math.Min(_pendingNativeRedrawFirst, index);
        _pendingNativeRedrawLast = Math.Max(_pendingNativeRedrawLast, index);
        if (_nativeRedrawFlushScheduled)
            return;

        _nativeRedrawFlushScheduled = true;
        Dispatcher.UIThread.Post(FlushQueuedNativeItemRedraws, DispatcherPriority.Background);
    }

    private void FlushQueuedNativeItemRedraws()
    {
        _nativeRedrawFlushScheduled = false;
        var first = _pendingNativeRedrawFirst;
        var last = _pendingNativeRedrawLast;
        _pendingNativeRedrawFirst = int.MaxValue;
        _pendingNativeRedrawLast = -1;

        if (_disposed || ListHandle == 0 || _viewModel is null || last < first)
            return;

        // Do not filter this through our estimated viewport. SysListView32 clips the dirty rectangle
        // itself; filtering here used to drop thumbnail redraws when icon-view origin math lagged a frame.
        InvalidateNativeItemRange(first, last);
    }

    private void ObserveNativePaintedItem(int index)
    {
        if (_disposed || _viewModel is null || index < 0 || index >= _viewModel.VirtualItems.Count)
            return;
        if (_viewModel.VirtualItems[index].Item is null)
            return;

        _nativePaintedIndices.Add(index);
        if (_nativePaintedFlushScheduled)
            return;

        _nativePaintedFlushScheduled = true;
        Dispatcher.UIThread.Post(FlushNativePaintedThumbnails, DispatcherPriority.Background);
    }

    private void FlushNativePaintedThumbnails()
    {
        _nativePaintedFlushScheduled = false;
        if (_disposed || _viewModel is null || _nativePaintedIndices.Count == 0)
        {
            _nativePaintedIndices.Clear();
            return;
        }

        var indices = _nativePaintedIndices
            .Where(index => index >= 0 && index < _viewModel.VirtualItems.Count && _viewModel.VirtualItems[index].Item is not null)
            .OrderBy(static index => index)
            .ToList();
        _nativePaintedIndices.Clear();
        if (indices.Count == 0)
            return;

        // After scrolling stops, prefetch one row beyond what Windows has just painted. During an
        // active fling only touch already-painted cards so network/decode work cannot fight scrolling.
        if (!_scrolling && _viewModel.ViewMode != FileViewMode.Details)
        {
            var metrics = CalculateNativeGridMetrics();
            var last = indices[^1];
            for (var i = 1; i <= metrics.Columns; i++)
            {
                var next = last + i;
                if (next >= _viewModel.VirtualItems.Count)
                    break;
                if (_viewModel.VirtualItems[next].Item is not null)
                    indices.Add(next);
            }
        }

        var items = new List<DriveItemModel>(indices.Count);
        var actualIndices = new List<int>(indices.Count);
        foreach (var index in indices.Distinct())
        {
            if (_viewModel.VirtualItems[index].Item is not { } item)
                continue;
            actualIndices.Add(index);
            items.Add(item);
        }

        if (items.Count == 0)
            return;

        _viewModel.UpdateDesktopRealizedThumbnails(actualIndices, items, allowNetwork: !_scrolling);
        _host.RaiseScrollStateChanged(actualIndices[0], actualIndices[^1]);
    }

    private void InvalidateNativeItemRange(int first, int last)
    {
        if (_viewModel is null || ListHandle == 0 || _viewModel.VirtualItems.Count == 0)
            return;

        first = Math.Clamp(first, 0, _viewModel.VirtualItems.Count - 1);
        last = Math.Clamp(last, first, _viewModel.VirtualItems.Count - 1);

        var hasDirty = false;
        var dirty = default(RECT);
        for (var index = first; index <= last; index++)
        {
            RECT rect;
            var ok = _viewModel.ViewMode == FileViewMode.Details
                ? TryGetNativeItemRect(index, out rect)
                : TryGetNativeGridCellRect(index, out rect);
            if (!ok)
                continue;

            if (!hasDirty)
            {
                dirty = rect;
                hasDirty = true;
            }
            else
            {
                dirty.left = Math.Min(dirty.left, rect.left);
                dirty.top = Math.Min(dirty.top, rect.top);
                dirty.right = Math.Max(dirty.right, rect.right);
                dirty.bottom = Math.Max(dirty.bottom, rect.bottom);
            }
        }

        if (!hasDirty)
            return;

        GetClientRect(ListHandle, out var client);
        dirty.left = Math.Max(dirty.left, client.left);
        dirty.top = Math.Max(dirty.top, client.top);
        dirty.right = Math.Min(dirty.right, client.right);
        dirty.bottom = Math.Min(dirty.bottom, client.bottom);
        if (dirty.Width <= 0 || dirty.Height <= 0)
            return;

        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.StructureToPtr(dirty, ptr, false);
            InvalidateRect(ListHandle, ptr, false);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void ResetNativeHorizontalScroll()
    {
        if (ListHandle == 0)
            return;

        if (_viewModel?.ViewMode != FileViewMode.Details)
        {
            var origin = GetNativeViewOrigin();
            if (origin.x != 0)
            {
                // LVM_SCROLL takes a delta. To move the current origin back to zero the delta is
                // -origin.X, not origin.X. The old sign doubled the horizontal displacement.
                SendMessage(ListHandle, LVM_SCROLL_NATIVE, (nint)(-origin.x), 0);
            }
        }

        // Do not mutate WS_HSCROLL or issue SWP_FRAMECHANGED from WM_PAINT/scroll callbacks. Those
        // frame changes force both scrollbars to be recalculated and were the source of the flashing
        // vertical bar. With empty native labels and a grid constrained to the client width, hiding
        // the horizontal bar here is now only a final presentation guard.
        ShowScrollBar(ListHandle, SB_HORZ_NATIVE, false);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowScrollBar(nint hwnd, int bar, [MarshalAs(UnmanagedType.Bool)] bool show);
}
