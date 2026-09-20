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
        _lastIconLayoutMode = -1;
        _lastIconLayoutCellWidth = -1;
        _lastIconLayoutCellHeight = -1;
        _lastIconLayoutGap = -1;
        InvalidateNativeIconGeometry();
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
        // Keep the proven column-count calculation unchanged. GridOuterMargin is a visual inset,
        // not part of native wrapping; otherwise a narrow resize can incorrectly lose a column.
        var edgePadding = Math.Max(gap, (int)Math.Round(6d * scale));
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

        return new NativeGridMetrics(columns, cellWidth, cellHeight, gap);
    }

    private void LayoutNativeIconItems(bool force, bool redrawAlreadySuspended = false)
    {
        if (_viewModel is null || _viewModel.ViewMode == FileViewMode.Details || ListHandle == 0)
            return;

        var metrics = CalculateNativeGridMetrics();
        var mode = (int)_viewModel.ViewMode;

        if (!force &&
            metrics.Columns == _lastIconLayoutColumns &&
            metrics.CellWidth == _lastIconLayoutCellWidth &&
            metrics.CellHeight == _lastIconLayoutCellHeight &&
            metrics.Gap == _lastIconLayoutGap &&
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
            RefreshNativeIconGeometry(metrics);
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
        _lastIconLayoutMode = mode;
        ResetNativeHorizontalScroll();
    }

    private void InvalidateNativeIconGeometry()
    {
        _cachedIconColumnLefts = [];
        _cachedIconFirstBaseLeft = 0;
        _cachedIconFirstTop = 0;
        _cachedIconRowPitch = 0;
        _cachedIconGeometryValid = false;
    }

    private void RefreshNativeIconGeometry(NativeGridMetrics metrics)
    {
        InvalidateNativeIconGeometry();
        if (_viewModel is null || _viewModel.VirtualItems.Count == 0 || metrics.Columns <= 0)
            return;

        var iconWidth = ScaleInt(
            _viewModel.ViewMode == FileViewMode.ExtraLargeIcons ? ExtraArtwork : LargeArtwork);
        var horizontalInset = Math.Max(0, (metrics.CellWidth - iconWidth) / 2);

        if (!TryGetNativeItemViewPosition(0, out var firstPosition))
            return;

        _cachedIconFirstBaseLeft = firstPosition.x - horizontalInset;
        _cachedIconFirstTop = firstPosition.y;
        _cachedIconRowPitch = Math.Max(1, metrics.CellHeight + metrics.Gap);
        _cachedIconColumnLefts = Enumerable.Repeat(int.MinValue, metrics.Columns).ToArray();

        var columnsWithItems = Math.Min(metrics.Columns, _viewModel.VirtualItems.Count);
        for (var column = 0; column < columnsWithItems; column++)
        {
            if (!TryGetNativeItemViewPosition(column, out var position))
                continue;
            _cachedIconColumnLefts[column] =
                (position.x - horizontalInset) - _cachedIconFirstBaseLeft;
        }

        if (_viewModel.VirtualItems.Count > metrics.Columns &&
            TryGetNativeItemViewPosition(metrics.Columns, out var nextRow))
        {
            var measuredPitch = nextRow.y - firstPosition.y;
            if (measuredPitch > 0)
                _cachedIconRowPitch = measuredPitch;
        }

        _cachedIconGeometryValid = true;
    }

    private bool TryGetNativeGridCellRect(int index, out RECT rect) =>
        TryGetNativeGridCellRect(index, GetNativeViewOrigin(), out rect);

    private bool TryGetNativeGridCellRect(int index, POINT origin, out RECT rect)
    {
        rect = default;
        if (_viewModel is null || _viewModel.ViewMode == FileViewMode.Details ||
            index < 0 || index >= _viewModel.VirtualItems.Count || ListHandle == 0)
        {
            return false;
        }

        var metrics = CalculateNativeGridMetrics();
        if (!_cachedIconGeometryValid || _cachedIconColumnLefts.Length != metrics.Columns)
            RefreshNativeIconGeometry(metrics);
        if (!_cachedIconGeometryValid)
            return false;

        var column = index % metrics.Columns;
        var row = index / metrics.Columns;

        if (_cachedIconColumnLefts[column] == int.MinValue)
        {
            var iconWidth = ScaleInt(
                _viewModel.ViewMode == FileViewMode.ExtraLargeIcons ? ExtraArtwork : LargeArtwork);
            var horizontalInset = Math.Max(0, (metrics.CellWidth - iconWidth) / 2);
            if (!TryGetNativeItemViewPosition(column, out var position))
                return false;
            _cachedIconColumnLefts[column] =
                (position.x - horizontalInset) - _cachedIconFirstBaseLeft;
        }

        var left = _cachedIconColumnLefts[column] - origin.x;
        var right = left + metrics.CellWidth;
        var top = _cachedIconFirstTop + row * _cachedIconRowPitch - origin.y;

        // Keep the correct native column distribution but add symmetric visual breathing room to
        // the outer cards only. Internal column pitch remains owned by SysListView32.
        var edgeMargin = ScaleInt(GridOuterMargin);
        if (column == 0)
            left += edgeMargin;
        if (column == metrics.Columns - 1)
            right -= edgeMargin;

        rect = new RECT(left, top, Math.Max(left + 1, right), top + metrics.CellHeight);
        return true;
    }

    private bool TryGetNativeItemViewPosition(int index, out POINT position)
    {
        position = default;
        if (ListHandle == 0 || index < 0)
            return false;

        return SendMessagePoint(
            ListHandle,
            LVM_GETITEMPOSITION_NATIVE,
            (nint)index,
            ref position) != 0;
    }

    private POINT GetNativeViewOrigin()
    {
        var origin = new POINT();
        if (ListHandle != 0)
            SendMessagePoint(ListHandle, LVM_GETORIGIN_NATIVE, 0, ref origin);
        return origin;
    }

    private readonly record struct NativeGridMetrics(
        int Columns,
        int CellWidth,
        int CellHeight,
        int Gap);
}
