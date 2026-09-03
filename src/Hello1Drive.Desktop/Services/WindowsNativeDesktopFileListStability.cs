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

    /// <summary>
    /// Thumbnail hydration can update many slots within a few milliseconds. Redrawing immediately
    /// for every notification made our PREPAINT owner-draw path repaint the whole viewport over and
    /// over, which looked like continuous flashing. Collapse those notifications into one native
    /// redraw per Avalonia dispatcher turn and limit it to the currently visible range.
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
            SendMessage(ListHandle, LVM_REDRAWITEMS, (nint)first, (nint)last);
    }

    /// <summary>
    /// SysListView32 icon view computes an internal horizontal extent from icon/label bounds even
    /// though Hello1Drive lays every card inside the client width. Long labels can therefore expose
    /// a horizontal scrollbar. Keep the native X origin at zero and hide only the horizontal bar;
    /// vertical scrolling remains fully native.
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

        ShowScrollBar(ListHandle, SB_HORZ_NATIVE, false);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowScrollBar(nint hwnd, int bar, [MarshalAs(UnmanagedType.Bool)] bool show);
}
