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
        _lastSignature = string.Empty;
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
            _lastSignature = string.Empty;
            SyncPresentation(force: false);
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.SelectedThemeText)
            or nameof(MainViewModel.TransparentFileItemBackground)
            or nameof(MainViewModel.SelectedBackgroundModeText)
            or nameof(MainViewModel.BackgroundColorText))
        {
            ApplyPaletteToNativeWindow();
            InvalidateRect(ListHandle, 0, true);
        }
    }

    private void VirtualItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var value in e.OldItems)
                if (value is VirtualDriveItemSlot slot)
                    DetachSlot(slot);
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
            DetachAllSlots();

        if (e.NewItems is not null)
        {
            foreach (var value in e.NewItems)
                if (value is VirtualDriveItemSlot slot)
                    AttachSlot(slot);
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

        if (e.PropertyName is nameof(VirtualDriveItemSlot.Item) or nameof(VirtualDriveItemSlot.Name))
            SetNativeItemText(index, slot.Item?.Name ?? string.Empty);

        if (e.PropertyName == nameof(VirtualDriveItemSlot.ThumbnailImage) && slot.Item is { } thumbnailItem &&
            thumbnailItem.ThumbnailImage is null)
        {
            RemoveThumbnail(thumbnailItem.Id);
        }

        if (e.PropertyName is nameof(VirtualDriveItemSlot.Item)
            or nameof(VirtualDriveItemSlot.ThumbnailImage)
            or nameof(VirtualDriveItemSlot.IsMobileSelected)
            or nameof(VirtualDriveItemSlot.IsPlaceholder)
            or nameof(VirtualDriveItemSlot.Name)
            or nameof(VirtualDriveItemSlot.SizeDisplay))
        {
            RedrawItem(index);
        }
    }

    private void SyncPresentation(bool force)
    {
        if (_disposed || _viewModel is null)
            return;

        var slots = _viewModel.VirtualItems;
        var mode = _viewModel.ViewMode;
        var signature = BuildSignature(slots, mode);
        if (!force && string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            ApplyPaletteToNativeWindow();
            QueueVisibleThumbnails(allowNetwork: !_scrolling);
            InvalidateRect(ListHandle, 0, false);
            return;
        }

        var firstVisible = GetFirstVisibleIndex();
        var anchorId = firstVisible >= 0 && firstVisible < slots.Count
            ? slots[firstVisible].Item?.Id
            : null;

        _lastSignature = signature;
        SendMessage(ListHandle, WM_SETREDRAW, 0, 0);
        try
        {
            ApplyNativeView(mode);
            SendMessage(ListHandle, LVM_DELETEALLITEMS, 0, 0);

            for (var index = 0; index < slots.Count; index++)
                InsertItem(index, slots[index].Item);

            RestoreSelection();
            var restoreIndex = FindItemIndex(anchorId, firstVisible);
            if (restoreIndex > 0)
                SendMessage(ListHandle, LVM_ENSUREVISIBLE, (nint)restoreIndex, 0);
        }
        finally
        {
            SendMessage(ListHandle, WM_SETREDRAW, 1, 0);
            InvalidateRect(ListHandle, 0, true);
        }

        UpdateColumnWidth();
        ReportScrollPosition();
        QueueVisibleThumbnails(allowNetwork: true);
    }

    private static string BuildSignature(IReadOnlyList<VirtualDriveItemSlot> slots, FileViewMode mode)
    {
        var hash = new HashCode();
        hash.Add((int)mode);
        hash.Add(slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            var item = slots[i].Item;
            if (item is null)
            {
                hash.Add(i);
                continue;
            }

            hash.Add(item.Id, StringComparer.Ordinal);
            hash.Add(item.Name, StringComparer.Ordinal);
            hash.Add(item.Size);
            hash.Add(item.LastModifiedDateTime);
        }
        return hash.ToHashCode().ToString("X8");
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
        SendMessage(ListHandle, LVM_ARRANGE, LVA_DEFAULT, 0);
    }

    private void InsertItem(int index, DriveItemModel? item)
    {
        var namePtr = Marshal.StringToHGlobalUni(item?.Name ?? string.Empty);
        try
        {
            var lvItem = new LVITEM
            {
                mask = LVIF_TEXT | LVIF_IMAGE,
                iItem = index,
                iSubItem = 0,
                pszText = namePtr,
                iImage = 0
            };
            var itemPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LVITEM>());
            try
            {
                Marshal.StructureToPtr(lvItem, itemPtr, false);
                SendMessage(ListHandle, LVM_INSERTITEMW, 0, itemPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(itemPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    private void SetNativeItemText(int index, string text)
    {
        var textPtr = Marshal.StringToHGlobalUni(text ?? string.Empty);
        var lvItem = new LVITEM { iSubItem = 0, pszText = textPtr };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<LVITEM>());
        try
        {
            Marshal.StructureToPtr(lvItem, ptr, false);
            SendMessage(ListHandle, LVM_SETITEMTEXTW, (nint)index, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
            Marshal.FreeHGlobal(textPtr);
        }
    }

}
