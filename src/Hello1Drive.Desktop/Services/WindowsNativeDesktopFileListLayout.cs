using System.Runtime.InteropServices;
using Hello1Drive.Models;

namespace Hello1Drive.Desktop.Services;

internal sealed partial class WindowsNativeDesktopFileListController
{
    private const int LVM_GETITEMPOSITION_NATIVE = LVM_FIRST + 16;
    private const int LVM_GETORIGIN_NATIVE = LVM_FIRST + 41;

    private int _lastIconLayoutCellWidth = -1;
    private int _lastIconLayoutCellHeight = -1;
    private int _lastIconLayoutGap = -1;

    private void ApplyNativeTransparency() => DisableLegacyColorKeyLayering();

    private void PaintNativeTransparentBackground(nint hwnd, nint hdc)
    {
        if (hwnd == 0 || hdc == 0)
            return;
        GetClientRect(hwnd, out var client);
        PaintNativeBackdrop(hdc, client);
    }

    private void ResetNativeIconLayout()
    {
        _lastIconLayoutColumns = -1;
        _lastIconLayoutItemCount = -1;
        _lastIconLayoutMode = -1;
        _lastIconLayoutCellWidth = -1;
        _lastIconLayoutCellHeight = -1;
        _lastIconLayoutGap = -1;
    }

    private NativeGridMetrics CalculateNativeGridMetrics()
    {
        GetClientRect(ListHandle, out var client);
        var extra = _viewModel?.ViewMode == FileViewMode.ExtraLargeIcons;
        var scale = Math.Max(0.01, _dpi / 96d);

        // Do all column fitting in physical pixels. Converting the viewport to DIPs and then
        // rounding every cell back to pixels accumulates enough error at 125/150% DPI to make
        // SysListView32 drop a column that visibly still fits.
        var preferredWidth = ScaleInt(extra ? ExtraWidth : LargeWidth);
        var minWidth = ScaleInt(extra ? 190d : 136d);
        var maxWidth = ScaleInt(extra ? 276d : 184d);
        var cellHeight = ScaleInt(extra ? ExtraHeight : LargeHeight);
        var gap = ScaleInt(GridSpacing);
        var edgePadding = Math.Max(gap, ScaleInt(GridOuterMargin));
        var usableWidth = Math.Max(1, client.Width - edgePadding * 2);

        var columns = Math.Max(1, (usableWidth + gap) / Math.Max(1, preferredWidth + gap));

        // If exactly one additional column still leaves every card at or above the designed
        // minimum width, use it. This avoids the large dead strip visible on medium-width windows
        // without making the grid suddenly jump several density levels.
        var nextColumns = columns + 1;
        var nextWidth = (usableWidth - gap * (nextColumns - 1)) / Math.Max(1, nextColumns);
        if (nextWidth >= minWidth)
            columns = nextColumns;

        var rawCellWidth = (usableWidth - gap * (columns - 1)) / Math.Max(1, columns);
        var cellWidth = columns == 1 && rawCellWidth < minWidth
            ? Math.Max(1, Math.Min(maxWidth, rawCellWidth))
            : Math.Clamp(rawCellWidth, minWidth, maxWidth);

        var gridWidth = columns * cellWidth + Math.Max(0, columns - 1) * gap;
        var leftMargin = Math.Max(edgePadding, (client.Width - gridWidth) / 2);

        return new NativeGridMetrics(columns, cellWidth, cellHeight, gap, leftMargin);
    }

    private void LayoutNativeIconItems(bool force, bool redrawAlreadySuspended = false)
    {
        if (_viewModel is null || _viewModel.ViewMode == FileViewMode.Details || ListHandle == 0)
            return;

        var metrics = CalculateNativeGridMetrics();
        var itemCount = _viewModel.VirtualItems.Count;
        var mode = (int)_viewModel.ViewMode;

        if (!force &&
            metrics.Columns == _lastIconLayoutColumns &&
            metrics.CellWidth == _lastIconLayoutCellWidth &&
            metrics.CellHeight == _lastIconLayoutCellHeight &&
            metrics.Gap == _lastIconLayoutGap &&
            itemCount == _lastIconLayoutItemCount &&
            mode == _lastIconLayoutMode)
        {
            return;
        }

        var ownsRedraw = !redrawAlreadySuspended;
        if (ownsRedraw)
            SendMessage(ListHandle, WM_SETREDRAW, 0, 0);

        try
        {
            var pitchX = metrics.CellWidth + metrics.Gap;
            var pitchY = metrics.CellHeight + metrics.Gap;

            // LVS_OWNERDATA virtual lists default to auto-arrange. Set the native icon grid once
            // and let SysListView32 calculate positions internally instead of issuing one
            // LVM_SETITEMPOSITION32 call per file.
            SendMessage(ListHandle, LVM_SETICONSPACING, 0, MakeLParam(pitchX, pitchY));
            SendMessage(ListHandle, LVM_ARRANGE, 0, 0);
        }
        finally
        {
            if (ownsRedraw)
            {
                SendMessage(ListHandle, WM_SETREDRAW, 1, 0);
                InvalidateRect(ListHandle, 0, false);
            }
        }

        _lastIconLayoutColumns = metrics.Columns;
        _lastIconLayoutCellWidth = metrics.CellWidth;
        _lastIconLayoutCellHeight = metrics.CellHeight;
        _lastIconLayoutGap = metrics.Gap;
        _lastIconLayoutItemCount = itemCount;
        _lastIconLayoutMode = mode;
        ResetNativeHorizontalScroll();
        ClampNativeIconScrollToContent();
    }

    private bool TryGetNativeGridCellRect(int index, out RECT rect)
    {
        rect = default;
        if (_viewModel is null || _viewModel.ViewMode == FileViewMode.Details ||
            index < 0 || index >= _viewModel.VirtualItems.Count || ListHandle == 0)
        {
            return false;
        }

        var metrics = CalculateNativeGridMetrics();
        if (!TryGetNativeItemViewPosition(index, out var position))
            return false;

        // LVM_GETITEMPOSITION returns view coordinates. LVM_GETORIGIN is the current positive view
        // scroll origin exposed by the control, so client coordinates are view - origin. Adding the
        // origin makes owner-drawn cards move downward while the native control scrolls downward,
        // which is exactly the reversed-wheel/blank-space symptom.
        var origin = GetNativeViewOrigin();
        var iconWidth = ScaleInt(_viewModel.ViewMode == FileViewMode.ExtraLargeIcons ? ExtraArtwork : LargeArtwork);

        // In auto-arranged icon view the native item position is the icon's upper-left corner.
        // Hello1Drive's painted card is wider than that layout image, so recover the grid-cell
        // origin by removing the native centering offset before converting view -> client coords.
        var horizontalInset = Math.Max(0, (metrics.CellWidth - iconWidth) / 2);

        // Normalize against item 0 before applying our centered outer margin. Common Controls may
        // give the first icon a theme/image-list dependent x offset; carrying that offset into the
        // custom card is what made the first column appear glued to (or slightly outside) the edge.
        var firstBaseLeft = 0;
        if (TryGetNativeItemViewPosition(0, out var firstPosition))
            firstBaseLeft = firstPosition.x - horizontalInset;

        var relativeLeft = (position.x - horizontalInset) - firstBaseLeft;
        var left = metrics.LeftMargin + relativeLeft - origin.x;
        var top = position.y - origin.y;
        rect = new RECT(left, top, left + metrics.CellWidth, top + metrics.CellHeight);
        return true;
    }

    private bool TryGetNativeItemViewPosition(int index, out POINT position)
    {
        position = default;
        if (ListHandle == 0 || index < 0)
            return false;

        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<POINT>());
        try
        {
            Marshal.StructureToPtr(position, ptr, false);
            if (SendMessage(ListHandle, LVM_GETITEMPOSITION_NATIVE, (nint)index, ptr) == 0)
                return false;
            position = Marshal.PtrToStructure<POINT>(ptr);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private POINT GetNativeViewOrigin()
    {
        var origin = new POINT();
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<POINT>());
        try
        {
            Marshal.StructureToPtr(origin, ptr, false);
            if (SendMessage(ListHandle, LVM_GETORIGIN_NATIVE, 0, ptr) != 0)
                origin = Marshal.PtrToStructure<POINT>(ptr);
            return origin;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void ClampNativeIconScrollToContent()
    {
        if (_clampingNativeIconScroll ||
            _viewModel is null ||
            _viewModel.ViewMode == FileViewMode.Details ||
            _viewModel.VirtualItems.Count == 0 ||
            ListHandle == 0)
        {
            return;
        }

        var lastIndex = _viewModel.VirtualItems.Count - 1;
        GetClientRect(ListHandle, out var client);
        var metrics = CalculateNativeGridMetrics();
        var bottomMargin = ScaleInt(GridBottomMargin);

        int contentBottom;
        if (TryGetNativeItemViewPosition(lastIndex, out var lastPosition))
        {
            contentBottom = lastPosition.y + metrics.CellHeight + bottomMargin;
        }
        else
        {
            // Defensive fallback for transient Common Controls layout states immediately after a
            // count/view switch. The desired row geometry is already known from our spacing.
            var rows = Math.Max(1,
                (_viewModel.VirtualItems.Count + metrics.Columns - 1) / metrics.Columns);
            contentBottom =
                rows * metrics.CellHeight +
                Math.Max(0, rows - 1) * metrics.Gap +
                bottomMargin;
        }

        var maxOriginY = Math.Max(0, contentBottom - Math.Max(1, client.Height));

        var origin = GetNativeViewOrigin();
        if (origin.y <= maxOriginY)
            return;

        _clampingNativeIconScroll = true;
        try
        {
            // LVM_SCROLL uses a delta. Clamp any stale native icon-view extent back to the
            // bottom of the final real card instead of allowing a viewport of empty background.
            SendMessage(ListHandle, LVM_SCROLL_NATIVE, 0, (nint)(maxOriginY - origin.y));
        }
        finally
        {
            _clampingNativeIconScroll = false;
        }
    }

    private readonly record struct NativeGridMetrics(
        int Columns,
        int CellWidth,
        int CellHeight,
        int Gap,
        int LeftMargin);
}
