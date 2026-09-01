namespace Hello1Drive.Services;

/// <summary>
/// Presents a small platform-native confirmation dialog. Mobile file lists are hosted in native
/// RecyclerView/UICollectionView layers, so destructive confirmations must also be native instead
/// of replacing the whole Avalonia page or hiding the file list behind an opaque overlay.
/// </summary>
public interface IPlatformConfirmationService
{
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText = "确定",
        string cancelText = "取消");
}
