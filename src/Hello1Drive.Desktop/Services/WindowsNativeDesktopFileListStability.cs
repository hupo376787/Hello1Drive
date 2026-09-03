using System.Runtime.InteropServices;
using Avalonia.Threading;
using Hello1Drive.Models;

namespace Hello1Drive.Desktop.Services;

internal sealed partial class WindowsNativeDesktopFileListController
{
    private const int LVM_SCROLL_NATIVE = LVM_FIRST + 20;
    private const int SB_HORZ_NATIVE = 0;
    private const int GWL_STYLE_NATIVE = -16;
    private const long WS_HSCROLL_NATIVE = 0x00100000L;

    private bool _nativeRedrawFlushScheduled;
    private int _pendingNativeRedrawFirst = int.MaxValue;
    private int _pendingNativeRedrawLast = -1;

    /// <summary>
    /// Thumbnail hydration can update many slots within a few milliseconds. Redrawing immediately
    /// for every notification made our PREPAINT owner-draw path repaint the viewport over and over.
    /// Collapse those notifications into one invalidation per dispatcher turn.
    /// </summary>
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

        var visible = GetVisibleIndexRange();
        if (visible.First < 0 || visible.Last < visible.First)
            return;

        first = Math.Max(first, visible.First);
        last = Math.Min(last, visible.Last);
        if (last >= first)
            InvalidateNativeItemRange(first, last);
    }

    /// <summary>
    /// LVM_REDRAWITEMS invalidates the ListView's own icon/label bounds, not Hello1Drive's larger
    /// custom card. That left stale hover/thumbnail pixels behind. Invalidate the exact custom card
    /// rectangle instead, preserving the existing background because PREPAINT redraws it itself.
    /// </summary>
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

    /// <summary>
    /// SysListView32 can re-add a standard WS_HSCROLL bar after it recalculates icon extents. Merely
    /// calling ShowScrollBar(FALSE) is temporary. Remove the window style as well, and keep the icon
    /// view origin locked to X=0. This leaves the native vertical scrolling path untouched.
    /// </summary>
    private void ResetNativeHorizontalScroll()
    {
        if (ListHandle == 0)
            return;

        if (_viewModel?.ViewMode != FileViewMode.Details)
        {
            var origin = GetNativeViewOrigin();
            if (origin.x != 0)
                SendMessage(ListHandle, LVM_SCROLL_NATIVE, (nint)origin.x, 0);
        }

        var style = GetWindowLongPtrCompat(ListHandle, GWL_STYLE_NATIVE);
        var cleaned = (nint)((long)style & ~WS_HSCROLL_NATIVE);
        if (cleaned != style)
        {
            SetWindowLongPtr(ListHandle, GWL_STYLE_NATIVE, cleaned);
            SetWindowPos(ListHandle, 0, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        ShowScrollBar(ListHandle, SB_HORZ_NATIVE, false);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowScrollBar(nint hwnd, int bar, [MarshalAs(UnmanagedType.Bool)] bool show);
}
