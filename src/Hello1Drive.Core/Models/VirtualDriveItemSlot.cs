using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Hello1Drive.Models;

/// <summary>
/// Extremely small stable slot used by the virtualized file surfaces on mobile and desktop.
/// The slot count can match folder.childCount immediately, while the real
/// DriveItemModel is attached later as background metadata pages arrive.
/// </summary>
public sealed class VirtualDriveItemSlot : ObservableObject, IDisposable
{
    private DriveItemModel? _item;

    public VirtualDriveItemSlot(int index, DriveItemModel? item = null)
    {
        Index = index;
        _item = item;
        if (_item is not null)
            _item.PropertyChanged += Item_PropertyChanged;
    }

    public int Index { get; }

    public DriveItemModel? Item => _item;
    public bool IsLoaded => _item is not null;
    public bool IsPlaceholder => _item is null;

    public string Id => _item?.Id ?? string.Empty;
    public string Name => _item?.Name ?? string.Empty;
    public string SizeDisplay => _item?.SizeDisplay ?? string.Empty;
    public bool IsFolder => _item?.IsFolder == true;
    public bool IsImage => _item?.IsImage == true;
    public bool ShowMobileFileBadge => _item?.ShowMobileFileBadge == true;
    public string FileBadgeText => _item?.FileBadgeText ?? string.Empty;
    public bool HasThumbnailImage => _item?.HasThumbnailImage == true;
    public bool HasNoThumbnailImage => _item?.HasNoThumbnailImage == true;
    public Bitmap? ThumbnailImage => _item?.ThumbnailImage;
    public bool ShowVideoThumbnailBadge => _item?.ShowVideoThumbnailBadge == true;
    public bool IsMobileSelected => _item?.IsMobileSelected == true;
    public bool IsMobileSelectionMode => _item?.IsMobileSelectionMode == true;

    public void SetItem(DriveItemModel? item)
    {
        if (ReferenceEquals(_item, item))
            return;

        if (_item is not null)
            _item.PropertyChanged -= Item_PropertyChanged;

        _item = item;

        if (_item is not null)
            _item.PropertyChanged += Item_PropertyChanged;

        RaiseAllForwardedProperties();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Forward model changes under the same property name. The slot data templates
        // therefore stay as light as the original DriveItem templates while the slot itself
        // remains stable and never has to be replaced in the collection.
        if (!string.IsNullOrWhiteSpace(e.PropertyName))
            OnPropertyChanged(e.PropertyName);
    }

    public void Dispose()
    {
        if (_item is not null)
            _item.PropertyChanged -= Item_PropertyChanged;
        _item = null;
    }

    private void RaiseAllForwardedProperties()
    {
        OnPropertyChanged(nameof(Item));
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsPlaceholder));
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(IsFolder));
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(ShowMobileFileBadge));
        OnPropertyChanged(nameof(FileBadgeText));
        OnPropertyChanged(nameof(HasThumbnailImage));
        OnPropertyChanged(nameof(HasNoThumbnailImage));
        OnPropertyChanged(nameof(ThumbnailImage));
        OnPropertyChanged(nameof(ShowVideoThumbnailBadge));
        OnPropertyChanged(nameof(IsMobileSelected));
        OnPropertyChanged(nameof(IsMobileSelectionMode));
    }
}
