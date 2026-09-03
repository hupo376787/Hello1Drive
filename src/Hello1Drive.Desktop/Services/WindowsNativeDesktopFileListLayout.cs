using System.Runtime.InteropServices;
using Hello1Drive.Models;

namespace Hello1Drive.Desktop.Services;

internal sealed partial class WindowsNativeDesktopFileListController
{
    private const int LVM_GETORIGIN_NATIVE = LVM_FIRST + 41;

    private int _lastIconLayoutCellWidth = -1;
    private int _lastIconLayoutCellHeight = -1;
    private int _lastIconLayoutGap = -1;

    // Kept under the old method name because the controller constructor calls it before the
    // first presentation. The implementation intentionally disables the old chroma-key mode.
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
        var clientWidthDip = Math.Max(1d, client.Width / scale);
        var preferred = extra ? ExtraWidth : LargeWidth;
        var minWidth = extra ? 190d : 136d;
        var maxWidth = extra ? 276d : 184d;
        var heightDip = extra ? ExtraHeight : LargeHeight;
        var usable = Math.Max(minWidth, clientWidthDip);
        var columns = Math.Max(1, (int)Math.Floor((usable + GridSpacing) / (preferred + GridSpacing)));
        var cellWidthDip = Math.Clamp(
            (usable - GridSpacing * (columns - 1)) / columns,
            minWidth,
            maxWidth);

        var cellWidth = Math.Max(1, (int)Math.Round(cellWidthDip * scale));
        var cellHeight = Math.Max(1, (int)Math.Round(heightDip * scale));
        var gap = Math.Max(1, (int)Math.Round(GridSpacing * scale));
        return new NativeGridMetrics(columns, cellWidth, cellHeight, gap);
    }

    private void LayoutNativeIconItems(bool force, bool redrawAlreadySuspended = false)
    {
        if (_viewModel is null || _viewModel.ViewMode == FileViewMode.Details || ListHandle == 0)
        {
            ResetNativeHorizontalScroll();
            return;
        }

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
            ResetNativeHorizontalScroll();
            return;
        }

        var ownsRedraw = !redrawAlreadySuspended;
        if (ownsRedraw)
            SendMessage(ListHandle, WM_SETREDRAW, 0, 0);

        var pointPtr = Marshal.AllocHGlobal(Marshal.SizeOf<POINT>());
        try
        {
            var pitchX = metrics.CellWidth + metrics.Gap;
            var pitchY = metrics.CellHeight + metrics.Gap;

            // Keep the native item's actual hit-test/scroll cell identical to the custom-drawn
            // Hello1Drive card. The old fixed spacing disagreed with the dynamically stretched
            // desktop grid and produced visibly uneven columns.
            SendMessage(ListHandle, LVM_SETICONSPACING, 0, MakeLParam(pitchX, pitchY));

            for (var index = 0; index < itemCount; index++)
            {
                var row = index / metrics.Columns;
                var column = index % metrics.Columns;
                var point = new POINT
                {
                    x = column * pitchX,
                    y = row * pitchY
                };
                Marshal.StructureToPtr(point, pointPtr, false);
                SendMessage(ListHandle, LVM_SETITEMPOSITION32, (nint)index, pointPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointPtr);
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
        var row = index / metrics.Columns;
        var column = index % metrics.Columns;
        var origin = GetNativeViewOrigin();
        var left = origin.x + column * (metrics.CellWidth + metrics.Gap);
        var top = origin.y + row * (metrics.CellHeight + metrics.Gap);
        rect = new RECT(left, top, left + metrics.CellWidth, top + metrics.CellHeight);
        return true;
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

    private readonly record struct NativeGridMetrics(int Columns, int CellWidth, int CellHeight, int Gap);
}
