using System.Runtime.InteropServices;
using Hello1Drive.Models;

namespace Hello1Drive.Desktop.Services;

internal sealed partial class WindowsNativeDesktopFileListController
{
    private const uint CDDS_ITEMPREPAINT_NATIVE = 0x00010001;
    private const int CDRF_DODEFAULT_NATIVE = 0x00000000;
    private const int CDRF_NOTIFYITEMDRAW_NATIVE = 0x00000020;

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

        if (msg == WM_ERASEBKGND)
            return 1;

        if (msg == WM_PRINTCLIENT && wParam != 0)
        {
            GetClientRect(hwnd, out var client);
            PaintNativeBackdrop(wParam, client);
            return 1;
        }

        if (msg == WM_PAINT)
        {
            var hdc = BeginPaint(hwnd, out var paint);
            if (hdc != 0)
            {
                GetClientRect(hwnd, out var client);
                PaintNativeBackdrop(hdc, client);
            }
            EndPaint(hwnd, ref paint);
            return 0;
        }

        var result = CallWindowProcW(_oldParentWndProc, hwnd, msg, wParam, lParam);
        if (msg == WM_SIZE)
        {
            ResizeListToHost();
            LayoutNativeIconItems(force: false);
            ResetNativeHorizontalScroll();
            InvalidateRect(hwnd, 0, false);
            InvalidateRect(ListHandle, 0, false);
            QueueVisibleThumbnails(allowNetwork: !_scrolling);
        }
        return result;
    }

    private nint ListWindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_ERASEBKGND)
            return 1;

        if (msg == WM_PRINTCLIENT && wParam != 0)
        {
            SyncBackdrop(force: false);
            GetClientRect(hwnd, out var client);
            PaintNativeBackdrop(wParam, client);
            DrawVisibleItems(wParam);
            return 1;
        }

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
                InvalidateVisibleItems();
                break;
            case WM_KEYUP:
                RaiseSelectionChanged();
                ReportScrollPosition();
                QueueVisibleThumbnails(allowNetwork: true);
                InvalidateVisibleItems();
                break;
            case WM_LBUTTONDBLCLK:
                if (HitTest(lParam) is { } doubleItem)
                    _host.RaiseItemDoubleTapped(doubleItem);
                break;
            case WM_RBUTTONUP:
                if (HitTest(lParam) is { } contextItem)
                    _host.RaiseItemContextRequested(contextItem);
                RaiseSelectionChanged();
                InvalidateVisibleItems();
                break;
            case WM_MOUSEWHEEL:
            case WM_VSCROLL:
                BeginNativeScroll();
                // Never mutate window/frame styles here. The old ResetNativeHorizontalScroll path
                // performed SWP_FRAMECHANGED during scrolling/painting and made the vertical bar
                // repeatedly disappear/reappear.
                InvalidateRect(ListHandle, 0, false);
                break;
            case WM_TIMER:
                if ((nuint)wParam == (nuint)ScrollIdleTimerId)
                {
                    EndNativeScroll();
                    // Force one clean item draw after idle. CDDS_ITEMPREPAINT then tells us exactly
                    // which native items are on screen and queues their network thumbnails.
                    InvalidateRect(ListHandle, 0, false);
                }
                break;
        }
        return result;
    }

    private nint HandleCustomDraw(nint lParam)
    {
        var custom = Marshal.PtrToStructure<NMLVCUSTOMDRAW>(lParam);

        if (custom.nmcd.dwDrawStage == CDDS_PREPAINT)
        {
            // Microsoft documents CDRF_SKIPDEFAULT for CDDS_ITEMPREPAINT, not the control-level
            // PREPAINT notification. Ask for item notifications and let ListView manage the viewport,
            // clipping and native scrollbars normally.
            SyncBackdrop(force: false);
            return (nint)CDRF_NOTIFYITEMDRAW_NATIVE;
        }

        if (custom.nmcd.dwDrawStage == CDDS_ITEMPREPAINT_NATIVE)
        {
            if (_viewModel is null || custom.nmcd.dwItemSpec > int.MaxValue)
                return (nint)CDRF_DODEFAULT_NATIVE;

            var index = (int)custom.nmcd.dwItemSpec;
            if (index < 0 || index >= _viewModel.VirtualItems.Count)
                return (nint)CDRF_DODEFAULT_NATIVE;

            RECT rect;
            var hasRect = _viewModel.ViewMode == FileViewMode.Details
                ? TryGetNativeItemRect(index, out rect)
                : TryGetNativeGridCellRect(index, out rect);
            if (!hasRect)
                rect = custom.nmcd.rc;

            DrawItem(custom.nmcd.hdc, rect, index, _viewModel.VirtualItems[index]);
            ObserveNativePaintedItem(index);

            // We drew this item completely. Suppress Explorer's own hot/selection/text rendering so
            // it cannot leave the gray native hover rectangle over our transparent card.
            return (nint)CDRF_SKIPDEFAULT;
        }

        return (nint)CDRF_DODEFAULT_NATIVE;
    }

    private void DrawVisibleItems(nint hdc)
    {
        if (_viewModel is null || _viewModel.VirtualItems.Count == 0)
            return;

        var (first, last) = GetVisibleIndexRange();
        if (first < 0 || last < first)
            return;

        if (_viewModel.ViewMode != FileViewMode.Details)
        {
            var metrics = CalculateNativeGridMetrics();
            first = Math.Max(0, (first / metrics.Columns) * metrics.Columns);
            last = Math.Min(_viewModel.VirtualItems.Count - 1,
                (((last / metrics.Columns) + 1) * metrics.Columns) - 1);
        }

        for (var index = first; index <= last; index++)
        {
            RECT rect;
            if (_viewModel.ViewMode == FileViewMode.Details)
            {
                if (!TryGetNativeItemRect(index, out rect))
                    continue;
            }
            else if (!TryGetNativeGridCellRect(index, out rect))
            {
                continue;
            }

            DrawItem(hdc, rect, index, _viewModel.VirtualItems[index]);
        }
    }

    private bool TryGetNativeItemRect(int index, out RECT rect)
    {
        rect = new RECT(LVIR_BOUNDS, 0, 0, 0);
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.StructureToPtr(rect, ptr, false);
            if (SendMessage(ListHandle, LVM_GETITEMRECT, (nint)index, ptr) == 0)
                return false;

            rect = Marshal.PtrToStructure<RECT>(ptr);
            return rect.Width > 0 && rect.Height > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void DrawItem(nint hdc, RECT nativeRect, int index, VirtualDriveItemSlot slot)
    {
        var palette = _palette;
        RECT drawRect;
        if (_viewModel?.ViewMode == FileViewMode.Details)
        {
            GetClientRect(ListHandle, out var client);
            drawRect = new RECT(0, nativeRect.top, client.right, nativeRect.bottom);
        }
        else
        {
            drawRect = nativeRect;
        }

        var savedDc = SaveDC(hdc);
        try
        {
            IntersectClipRect(hdc, drawRect.left, drawRect.top, drawRect.right, drawRect.bottom);
            SetBkMode(hdc, TRANSPARENT);

            if (_viewModel?.ViewMode == FileViewMode.Details)
                DrawDetailsItem(hdc, drawRect, index, slot, palette);
            else
                DrawGridItem(hdc, drawRect, index, slot, palette);
        }
        finally
        {
            if (savedDc != 0)
                RestoreDC(hdc, savedDc);
        }
    }

    private void DrawDetailsItem(nint hdc, RECT rect, int index, VirtualDriveItemSlot slot, Palette palette)
    {
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
        var maximumArtwork = ScaleInt(extra ? ExtraArtwork : LargeArtwork);
        var artworkBottom = rect.bottom - padding - captionHeight - sizeHeight - ScaleInt(extra ? 12 : 9);
        var artworkSize = Math.Max(
            ScaleInt(32),
            Math.Min(maximumArtwork, Math.Min(
                Math.Max(ScaleInt(32), rect.Width - padding * 2),
                Math.Max(ScaleInt(32), artworkBottom - rect.top - padding))));
        var artworkX = rect.left + (rect.Width - artworkSize) / 2;
        var artworkY = Math.Max(rect.top + padding,
            rect.top + (artworkBottom - rect.top - artworkSize + padding) / 2);
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
