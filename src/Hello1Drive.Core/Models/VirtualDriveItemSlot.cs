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
    private bool _showModifiedDateInIconView;

    public VirtualDriveItemSlot(int index, DriveItemModel? item = null, bool showModifiedDateInIconView = false)
    {
        Index = index;
        _showModifiedDateInIconView = showModifiedDateInIconView;
        _item = item;
        if (_item is not null)
        {
            _item.AttachPresentation();
            _item.PropertyChanged += Item_PropertyChanged;
        }
    }

    public int Index { get; }

    public DriveItemModel? Item => _item;
    public bool IsLoaded => _item is not null;
    public bool IsPlaceholder => _item is null;

    public string Id => _item?.Id ?? string.Empty;
    public string Name => _item?.Name ?? string.Empty;
    public string SizeDisplay => _item?.SizeDisplay ?? string.Empty;
    public string ModifiedDisplay => _item?.ModifiedDisplay ?? string.Empty;
    public string IconSecondaryDisplay
    {
        get
        {
            var size = SizeDisplay;
            if (!_showModifiedDateInIconView || string.IsNullOrWhiteSpace(ModifiedDisplay))
                return size;
            if (string.IsNullOrWhiteSpace(size))
                return ModifiedDisplay;
            return $"{ModifiedDisplay} · {size}";
        }
    }
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

    public void SetIconDateVisibility(bool show)
    {
        if (_showModifiedDateInIconView == show)
            return;
        _showModifiedDateInIconView = show;
        OnPropertyChanged(nameof(IconSecondaryDisplay));
    }

    public void SetItem(DriveItemModel? item, bool compactNotification = false)
    {
        if (ReferenceEquals(_item, item))
            return;

        if (_item is not null)
        {
            _item.PropertyChanged -= Item_PropertyChanged;
            _item.DetachPresentation();
        }

        _item = item;

        if (_item is not null)
        {
            // Attach before exposing the new item to bindings/rendering. If the cache layer just
            // replaced an equivalent model, AttachPresentation can adopt the decoded bitmap handed
            // off by the old model, so the slot never paints an intermediate IMG/VID placeholder.
            _item.AttachPresentation();
            _item.PropertyChanged += Item_PropertyChanged;
        }

        // Desktop uses one retained self-drawn surface and reads the DriveItemModel directly.
        // A 200-item Graph page therefore needs only one notification per slot instead
        // of the 15+ forwarded binding notifications required by the legacy/mobile XAML surface.
        // This keeps a background page hydration short enough for pointer/wheel input to preempt.
        if (compactNotification)
        {
            OnPropertyChanged(nameof(Item));
            return;
        }

        RaiseAllForwardedProperties();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Forward model changes under the same property name. The slot data templates
        // therefore stay as light as the original DriveItem templates while the slot itself
        // remains stable and never has to be replaced in the collection.
        if (!string.IsNullOrWhiteSpace(e.PropertyName))
            OnPropertyChanged(e.PropertyName);

        if (e.PropertyName is nameof(DriveItemModel.ModifiedDisplay) or nameof(DriveItemModel.SizeDisplay) or
            nameof(DriveItemModel.LastModifiedDateTime) or nameof(DriveItemModel.Size) or nameof(DriveItemModel.ChildCount))
        {
            OnPropertyChanged(nameof(ModifiedDisplay));
            OnPropertyChanged(nameof(IconSecondaryDisplay));
        }
    }

    public void Dispose()
    {
        if (_item is not null)
        {
            _item.PropertyChanged -= Item_PropertyChanged;
            _item.DetachPresentation();
        }
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
        OnPropertyChanged(nameof(ModifiedDisplay));
        OnPropertyChanged(nameof(IconSecondaryDisplay));
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
