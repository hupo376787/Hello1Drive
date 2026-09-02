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
    private void ApplyNativeTransparency()
    {
        if (Handle != 0)
            SetLayeredWindowAttributes(Handle, NativeTransparentKey, 255, LWA_COLORKEY);
        if (ListHandle != 0)
            SetLayeredWindowAttributes(ListHandle, NativeTransparentKey, 255, LWA_COLORKEY);
    }

    private void PaintNativeTransparentBackground(nint hwnd, nint hdc)
    {
        if (hwnd == 0 || hdc == 0)
            return;
        GetClientRect(hwnd, out var client);
        FillRectColor(hdc, client, NativeTransparentKey);
    }

    private void ResetNativeIconLayout()
    {
        _lastIconLayoutColumns = -1;
        _lastIconLayoutItemCount = -1;
        _lastIconLayoutMode = -1;
    }

    private void LayoutNativeIconItems(bool force, bool redrawAlreadySuspended = false)
    {
        if (_viewModel is null || _viewModel.ViewMode == FileViewMode.Details || ListHandle == 0)
            return;

        GetClientRect(ListHandle, out var client);
        if (client.Width <= 1)
            return;

        var extra = _viewModel.ViewMode == FileViewMode.ExtraLargeIcons;
        var cellWidth = ScaleInt(extra ? ExtraWidth : LargeWidth);
        var cellHeight = ScaleInt(extra ? ExtraHeight : LargeHeight);
        var gap = ScaleInt(GridSpacing);
        var pitchX = Math.Max(1, cellWidth + gap);
        var pitchY = Math.Max(1, cellHeight + gap);
        var columns = Math.Max(1, (client.Width + gap) / pitchX);
        var itemCount = _viewModel.VirtualItems.Count;
        var mode = (int)_viewModel.ViewMode;

        if (!force && columns == _lastIconLayoutColumns && itemCount == _lastIconLayoutItemCount && mode == _lastIconLayoutMode)
            return;

        var ownsRedraw = !redrawAlreadySuspended;
        if (ownsRedraw)
            SendMessage(ListHandle, WM_SETREDRAW, 0, 0);

        var pointPtr = Marshal.AllocHGlobal(Marshal.SizeOf<POINT>());
        try
        {
            var edge = Math.Max(0, gap / 2);
            for (var index = 0; index < itemCount; index++)
            {
                var row = index / columns;
                var column = index % columns;
                var point = new POINT
                {
                    x = edge + column * pitchX,
                    y = edge + row * pitchY
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
                InvalidateRect(ListHandle, 0, true);
            }
        }

        _lastIconLayoutColumns = columns;
        _lastIconLayoutItemCount = itemCount;
        _lastIconLayoutMode = mode;
    }
}
