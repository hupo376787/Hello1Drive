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
    private nint ParentWindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (_disposed)
            return CallWindowProcW(_oldParentWndProc, hwnd, msg, wParam, lParam);

        if (msg == WM_NOTIFY && lParam != 0)
        {
            var hdr = Marshal.PtrToStructure<NMHDR>(lParam);
            if (hdr.hwndFrom == ListHandle && hdr.code == NM_CUSTOMDRAW)
                return HandleCustomDraw(lParam);
        }

        if (msg == WM_ERASEBKGND && wParam != 0)
        {
            GetClientRect(hwnd, out var client);
            FillRectColor(wParam, client, _palette.Background);
            return 1;
        }

        var result = CallWindowProcW(_oldParentWndProc, hwnd, msg, wParam, lParam);
        if (msg == WM_SIZE)
        {
            ResizeListToHost();
            if (_viewModel?.ViewMode != FileViewMode.Details)
                SendMessage(ListHandle, LVM_ARRANGE, LVA_DEFAULT, 0);
            QueueVisibleThumbnails(allowNetwork: !_scrolling);
        }
        return result;
    }

    private nint ListWindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var result = CallWindowProcW(_oldListWndProc, hwnd, msg, wParam, lParam);
        if (_disposed)
            return result;

        switch (msg)
        {
            case WM_MOUSEMOVE:
                if (!_scrolling)
                    UpdateHotItem(lParam);
                break;
            case WM_MOUSELEAVE:
                ClearHotItem();
                break;
            case WM_LBUTTONUP:
                RaiseSelectionChanged();
                ReportScrollPosition();
                QueueVisibleThumbnails(allowNetwork: true);
                break;
            case WM_KEYUP:
                RaiseSelectionChanged();
                ReportScrollPosition();
                QueueVisibleThumbnails(allowNetwork: true);
                break;
            case WM_LBUTTONDBLCLK:
                if (HitTest(lParam) is { } doubleItem)
                    _host.RaiseItemDoubleTapped(doubleItem);
                break;
            case WM_RBUTTONUP:
                if (HitTest(lParam) is { } contextItem)
                    _host.RaiseItemContextRequested(contextItem);
                RaiseSelectionChanged();
                break;
            case WM_MOUSEWHEEL:
            case WM_VSCROLL:
                BeginNativeScroll();
                break;
            case WM_TIMER:
                if ((nuint)wParam == (nuint)ScrollIdleTimerId)
                    EndNativeScroll();
                break;
        }
        return result;
    }

    private nint HandleCustomDraw(nint lParam)
    {
        var custom = Marshal.PtrToStructure<NMLVCUSTOMDRAW>(lParam);
        if (custom.nmcd.dwDrawStage == CDDS_PREPAINT)
            return (nint)CDRF_NOTIFYITEMDRAW;

        if (custom.nmcd.dwDrawStage != CDDS_ITEMPREPAINT || _viewModel is null)
            return 0;

        var index = checked((int)custom.nmcd.dwItemSpec);
        if (index < 0 || index >= _viewModel.VirtualItems.Count)
            return (nint)CDRF_SKIPDEFAULT;

        DrawItem(custom.nmcd.hdc, custom.nmcd.rc, index, _viewModel.VirtualItems[index]);
        return (nint)CDRF_SKIPDEFAULT;
    }

    private void DrawItem(nint hdc, RECT nativeRect, int index, VirtualDriveItemSlot slot)
    {
        var palette = _palette;
        SetBkMode(hdc, TRANSPARENT);

        if (_viewModel?.ViewMode == FileViewMode.Details)
        {
            GetClientRect(ListHandle, out var client);
            var row = new RECT(0, nativeRect.top, client.right, nativeRect.bottom);
            DrawDetailsItem(hdc, row, index, slot, palette);
            return;
        }

        var cell = NormalizeGridCell(nativeRect, _viewModel?.ViewMode == FileViewMode.ExtraLargeIcons);
        DrawGridItem(hdc, cell, index, slot, palette);
    }

    private void DrawDetailsItem(nint hdc, RECT rect, int index, VirtualDriveItemSlot slot, Palette palette)
    {
        FillRectColor(hdc, rect, palette.Background);
        var selected = IsItemSelected(index) || slot.Item?.IsMobileSelected == true;
        var hover = _hotIndex == index && !selected;
        var surface = Inset(rect, ScaleInt(2), ScaleInt(2));
        if (selected)
            FillRoundRect(hdc, surface, ScaleInt(5), palette.Selection);
        else if (hover)
            FillRoundRect(hdc, surface, ScaleInt(5), palette.Hover);
        else if (_viewModel?.TransparentFileItemBackground != true)
            FillRoundRect(hdc, surface, ScaleInt(5), palette.Surface);

        if (slot.Item is not { } item)
        {
            var placeholder = Inset(rect, ScaleInt(7), ScaleInt(7));
            FillRoundRect(hdc, placeholder, ScaleInt(4), palette.Placeholder);
            return;
        }

        var art = new RECT(
            rect.left + ScaleInt(5),
            rect.top + ScaleInt(7),
            rect.left + ScaleInt(37),
            rect.top + ScaleInt(39));
        DrawArtwork(hdc, item, art, ScaleInt(5), palette);

        var width = rect.Width;
        var gap = ScaleInt(6);
        var iconColumn = ScaleInt(42);
        var typeWidth = ScaleInt(150);
        var sizeWidth = ScaleInt(130);
        var modifiedWidth = ScaleInt(190);
        var fixedRight = typeWidth + sizeWidth + modifiedWidth + gap * 3;
        var nameWidth = Math.Max(ScaleInt(24), width - iconColumn - gap - fixedRight);
        var nameX = rect.left + iconColumn + gap;
        var typeX = nameX + nameWidth + gap;
        var sizeX = typeX + typeWidth + gap;
        var modifiedX = sizeX + sizeWidth + gap;

        DrawTextLine(hdc, item.Name,
            new RECT(nameX, rect.top, nameX + nameWidth, rect.bottom),
            _normalFont, palette.Text, center: false);
        DrawTextLine(hdc, item.TypeDisplay,
            new RECT(typeX, rect.top, typeX + typeWidth, rect.bottom),
            _smallFont, palette.MutedText, center: false);
        DrawTextLine(hdc, item.SizeDisplay,
            new RECT(sizeX, rect.top, sizeX + sizeWidth, rect.bottom),
            _smallFont, palette.MutedText, center: false);
        DrawTextLine(hdc, item.ModifiedDisplay,
            new RECT(modifiedX, rect.top, Math.Max(modifiedX + 1, rect.right - ScaleInt(4)), rect.bottom),
            _smallFont, palette.MutedText, center: false);
    }

    private void DrawGridItem(nint hdc, RECT rect, int index, VirtualDriveItemSlot slot, Palette palette)
    {
        FillRectColor(hdc, rect, palette.Background);
        var selected = IsItemSelected(index) || slot.Item?.IsMobileSelected == true;
        var hover = _hotIndex == index && !selected;
        var extra = _viewModel?.ViewMode == FileViewMode.ExtraLargeIcons;
        var radius = ScaleInt(extra ? 12 : 10);
        var surface = Inset(rect, ScaleInt(2), ScaleInt(2));
        if (selected)
            FillRoundRect(hdc, surface, radius, palette.Selection);
        else if (hover)
            FillRoundRect(hdc, surface, radius, palette.Hover);
        else if (_viewModel?.TransparentFileItemBackground != true)
            FillRoundRect(hdc, surface, radius, palette.Surface);

        if (slot.Item is not { } item)
        {
            FillRoundRect(hdc, Inset(rect, ScaleInt(7), ScaleInt(7)), Math.Max(ScaleInt(4), radius - ScaleInt(2)), palette.Placeholder);
            return;
        }

        var padding = ScaleInt(extra ? 12 : 10);
        var captionHeight = ScaleInt(extra ? 23 : 21);
        var sizeHeight = ScaleInt(extra ? 20 : 18);
        var artworkSize = ScaleInt(extra ? ExtraArtwork : LargeArtwork);
        var artworkBottom = rect.bottom - padding - captionHeight - sizeHeight - ScaleInt(extra ? 12 : 9);
        var artworkX = rect.left + (rect.Width - artworkSize) / 2;
        var availableArtworkHeight = Math.Max(artworkSize, artworkBottom - rect.top - padding);
        var artworkY = Math.Max(rect.top + padding,
            rect.top + (availableArtworkHeight - artworkSize + padding) / 2);
        var art = new RECT(artworkX, artworkY, artworkX + artworkSize, artworkY + artworkSize);
        DrawArtwork(hdc, item, art, ScaleInt(extra ? 11 : 9), palette);

        var nameY = rect.bottom - padding - sizeHeight - captionHeight - ScaleInt(2);
        DrawTextLine(hdc, item.Name,
            new RECT(rect.left + padding, nameY, rect.right - padding, nameY + captionHeight),
            _mediumFont, palette.Text, center: true);
        DrawTextLine(hdc, item.SizeDisplay,
            new RECT(rect.left + padding, rect.bottom - padding - sizeHeight, rect.right - padding, rect.bottom - padding),
            _smallFont, palette.MutedText, center: true);
    }

    private RECT NormalizeGridCell(RECT nativeRect, bool extra)
    {
        var width = ScaleInt(extra ? ExtraWidth : LargeWidth);
        var height = ScaleInt(extra ? ExtraHeight : LargeHeight);
        var centerX = nativeRect.left + nativeRect.Width / 2;
        var centerY = nativeRect.top + nativeRect.Height / 2;
        return new RECT(
            centerX - width / 2,
            centerY - height / 2,
            centerX - width / 2 + width,
            centerY - height / 2 + height);
    }

    private void DrawArtwork(nint hdc, DriveItemModel item, RECT dest, int radius, Palette palette)
    {
        if (item.ThumbnailImage is not null && TryDrawThumbnail(hdc, item, dest, radius))
        {
            if (item.IsVideo)
                DrawVideoBadge(hdc, dest, palette);
            return;
        }

        if (item.IsFolder)
        {
            DrawFolder(hdc, dest);
            return;
        }

        DrawFileBadge(hdc, item, dest, radius, palette);
    }

}
