using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Hello1Drive.Controls;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;
using Microsoft.Win32;

namespace Hello1Drive.Desktop.Services;

internal sealed partial class WindowsNativeDesktopFileListController
{
    private void AttachViewModel(MainViewModel? vm)
    {
        if (ReferenceEquals(_viewModel, vm))
            return;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.VirtualItems.CollectionChanged -= VirtualItems_CollectionChanged;
        }
        DetachAllSlots();

        _viewModel = vm;
        unchecked { _collectionVersion++; }
        _lastSyncedCollectionVersion = -1;
        _lastSyncedViewMode = -1;
        _lastNativeItemCount = -1;
        ResetNativeIconLayout();
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModel.VirtualItems.CollectionChanged += VirtualItems_CollectionChanged;
            foreach (var slot in _viewModel.VirtualItems)
                AttachSlot(slot);
        }

        ApplyPaletteToNativeWindow();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || _viewModel is null)
            return;

        if (e.PropertyName == nameof(MainViewModel.ViewMode))
        {
            _lastSyncedViewMode = -1;
            ResetNativeIconLayout();
            SyncPresentation(force: false);
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.SelectedThemeText)
            or nameof(MainViewModel.TransparentFileItemBackground)
            or nameof(MainViewModel.SelectedBackgroundModeText)
            or nameof(MainViewModel.BackgroundColorText))
        {
            ApplyPaletteToNativeWindow();
        }
    }

    private void VirtualItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        unchecked { _collectionVersion++; }

        if (e.OldItems is not null)
        {
            foreach (var value in e.OldItems)
                if (value is VirtualDriveItemSlot slot)
                    DetachSlot(slot);
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            DetachAllSlots();
            if (_viewModel is not null)
            {
                foreach (var slot in _viewModel.VirtualItems)
                    AttachSlot(slot);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var value in e.NewItems)
                if (value is VirtualDriveItemSlot slot)
                    AttachSlot(slot);
        }

        // Owner-data ListView does not observe Avalonia collection changes by itself. Coalesce
        // bursty AddRange/RemoveAt notifications into one UI pass; otherwise shrinking a previously
        // preallocated placeholder tail could trigger hundreds of native relayouts.
        if (!_collectionSyncScheduled)
        {
            _collectionSyncScheduled = true;
            Dispatcher.UIThread.Post(() =>
            {
                _collectionSyncScheduled = false;
                if (!_disposed)
                    SyncPresentation(force: false);
            }, DispatcherPriority.Background);
        }
    }

    private void AttachSlot(VirtualDriveItemSlot slot)
    {
        if (_subscribedSlots.Add(slot))
            slot.PropertyChanged += Slot_PropertyChanged;
    }

    private void DetachSlot(VirtualDriveItemSlot slot)
    {
        if (_subscribedSlots.Remove(slot))
            slot.PropertyChanged -= Slot_PropertyChanged;
    }

    private void DetachAllSlots()
    {
        foreach (var slot in _subscribedSlots)
            slot.PropertyChanged -= Slot_PropertyChanged;
        _subscribedSlots.Clear();
    }

    private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || sender is not VirtualDriveItemSlot slot || ListHandle == 0)
            return;

        var index = slot.Index;
        if (_viewModel is null || index < 0 || index >= _viewModel.VirtualItems.Count)
            return;

        // Native labels are deliberately kept empty. Hello1Drive draws the visible filename itself.
        // Feeding real names to SysListView32 makes icon view include label widths in its native
        // extent calculation, which is what kept recreating the horizontal scrollbar.

        if (e.PropertyName == nameof(VirtualDriveItemSlot.ThumbnailImage) && slot.Item is { } thumbnailItem)
        {
            if (thumbnailItem.ThumbnailImage is null)
                RemoveThumbnail(thumbnailItem.Id);
            else
                QueueNativeThumbnailPreparation(thumbnailItem);
        }

        if (e.PropertyName is nameof(VirtualDriveItemSlot.Item)
            or nameof(VirtualDriveItemSlot.ThumbnailImage)
            or nameof(VirtualDriveItemSlot.IsMobileSelected)
            or nameof(VirtualDriveItemSlot.IsPlaceholder)
            or nameof(VirtualDriveItemSlot.Name)
            or nameof(VirtualDriveItemSlot.SizeDisplay))
        {
            QueueNativeItemRedraw(index);
        }
    }

    private void SyncPresentation(bool force)
    {
        if (_disposed || _viewModel is null)
            return;

        var slots = _viewModel.VirtualItems;
        var mode = _viewModel.ViewMode;
        var modeValue = (int)mode;
        var structureChanged =
            force ||
            _lastSyncedCollectionVersion != _collectionVersion ||
            _lastSyncedViewMode != modeValue;
        if (!structureChanged)
        {
            SyncBackdrop(force: false);
            LayoutNativeIconItems(force: false);
            QueueVisibleThumbnails(allowNetwork: !_scrolling);
            return;
        }

        var firstVisible = GetFirstVisibleIndex();
        var anchorId = firstVisible >= 0 && firstVisible < slots.Count
            ? slots[firstVisible].Item?.Id
            : null;

        _lastSyncedCollectionVersion = _collectionVersion;
        _lastSyncedViewMode = modeValue;
        SendMessage(ListHandle, WM_SETREDRAW, 0, 0);
        try
        {
            ApplyNativeView(mode);

            // LVS_OWNERDATA keeps only selection/focus state inside SysListView32. When an
            // icon-view collection shrinks, clear the virtual count once before applying the new
            // count. This forces Common Controls to discard its old icon work extent/scroll range;
            // selection is restored immediately below while redraw is suspended.
            if (mode != FileViewMode.Details &&
                _lastNativeItemCount >= 0 &&
                slots.Count < _lastNativeItemCount)
            {
                SendMessage(ListHandle, LVM_SETITEMCOUNT, 0, 0);
            }

            SendMessage(ListHandle, LVM_SETITEMCOUNT, (nint)slots.Count, 0);
            _lastNativeItemCount = slots.Count;

            if (mode != FileViewMode.Details)
                LayoutNativeIconItems(force: true, redrawAlreadySuspended: true);

            RestoreSelection();
            var restoreIndex = FindItemIndex(anchorId, firstVisible);
            if (restoreIndex > 0)
                SendMessage(ListHandle, LVM_ENSUREVISIBLE, (nint)restoreIndex, 0);
        }
        finally
        {
            SendMessage(ListHandle, WM_SETREDRAW, 1, 0);
            InvalidateRect(ListHandle, 0, false);
        }

        UpdateColumnWidth();
        ResetNativeHorizontalScroll();
        ClampNativeIconScrollToContent();
        ReportScrollPosition();
        QueueVisibleThumbnails(allowNetwork: true);
    }

    private nint HandleVirtualGetDispInfo(nint lParam)
    {
        if (lParam == 0)
            return 0;

        var info = Marshal.PtrToStructure<NMLVDISPINFO>(lParam);

        // The native control owns layout/selection only; Hello1Drive paints labels and artwork.
        // Supplying one shared image slot plus an empty label avoids per-item native allocations.
        if ((info.item.mask & LVIF_IMAGE) != 0)
            info.item.iImage = 0;

        if ((info.item.mask & LVIF_TEXT) != 0 &&
            info.item.pszText != 0 &&
            info.item.cchTextMax > 0)
        {
            Marshal.WriteInt16(info.item.pszText, 0);
        }

        Marshal.StructureToPtr(info, lParam, false);
        return 0;
    }

    private void HandleVirtualCacheHint(nint lParam)
    {
        if (_viewModel is null || lParam == 0 || _viewModel.VirtualItems.Count == 0)
            return;

        var hint = Marshal.PtrToStructure<NMLVCACHEHINT>(lParam);
        var count = _viewModel.VirtualItems.Count;
        var first = Math.Clamp(Math.Min(hint.iFrom, hint.iTo), 0, count - 1);
        var last = Math.Clamp(Math.Max(hint.iFrom, hint.iTo), first, count - 1);

        // Expand the native hint slightly so fast wheel/trackpad scrolling usually lands on
        // already-hydrated thumbnails. Keep network fetches paused while the user is flinging.
        var padding = _viewModel.ViewMode == FileViewMode.Details
            ? 12
            : Math.Max(4, CalculateNativeGridMetrics().Columns * 2);
        first = Math.Max(0, first - padding);
        last = Math.Min(count - 1, last + padding);

        var indices = new List<int>(last - first + 1);
        var items = new List<DriveItemModel>(last - first + 1);
        for (var index = first; index <= last; index++)
        {
            if (_viewModel.VirtualItems[index].Item is not { } item)
                continue;
            indices.Add(index);
            items.Add(item);
        }

        if (items.Count > 0)
            _viewModel.UpdateDesktopRealizedThumbnails(indices, items, allowNetwork: !_scrolling);
    }

    private int FindItemIndex(string? itemId, int fallback)
    {
        if (_viewModel is null || string.IsNullOrWhiteSpace(itemId))
            return Math.Clamp(fallback, 0, Math.Max(0, (_viewModel?.VirtualItems.Count ?? 1) - 1));

        for (var i = 0; i < _viewModel.VirtualItems.Count; i++)
        {
            if (string.Equals(_viewModel.VirtualItems[i].Item?.Id, itemId, StringComparison.Ordinal))
                return i;
        }

        return Math.Clamp(fallback, 0, Math.Max(0, _viewModel.VirtualItems.Count - 1));
    }

    private void ApplyNativeView(FileViewMode mode)
    {
        var nativeView = mode == FileViewMode.Details ? LV_VIEW_DETAILS : LV_VIEW_ICON;
        SendMessage(ListHandle, LVM_SETVIEW, (nint)nativeView, 0);
        ResetNativeIconLayout();

        if (mode == FileViewMode.Details)
        {
            SendMessage(ListHandle, LVM_SETIMAGELIST, LVSIL_SMALL, _detailsImageList);
            SendMessage(ListHandle, WM_SETFONT, _normalFont, 1);
            UpdateColumnWidth();
            return;
        }

        var extra = mode == FileViewMode.ExtraLargeIcons;
        SendMessage(ListHandle, LVM_SETIMAGELIST, LVSIL_NORMAL, extra ? _extraImageList : _largeImageList);
        SendMessage(ListHandle, WM_SETFONT, _normalFont, 1);
        var spacingWidth = ScaleInt((extra ? ExtraWidth : LargeWidth) + GridSpacing);
        var spacingHeight = ScaleInt((extra ? ExtraHeight : LargeHeight) + GridSpacing);
        SendMessage(ListHandle, LVM_SETICONSPACING, 0, MakeLParam(spacingWidth, spacingHeight));
    }

}
