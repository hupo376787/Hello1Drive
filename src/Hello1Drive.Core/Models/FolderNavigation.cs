namespace Hello1Drive.Models;

public enum FolderNavigationReason
{
    Initial,
    EnterChild,
    Back,
    Breadcrumb,
    Root,
    Refresh
}

public sealed class FolderNavigationEventArgs : EventArgs
{
    public FolderNavigationEventArgs(FolderNavigationReason reason, string folderKey)
    {
        Reason = reason;
        FolderKey = folderKey;
    }

    public FolderNavigationReason Reason { get; }
    public string FolderKey { get; }
    public bool ShouldRestoreScroll => Reason is FolderNavigationReason.Back or FolderNavigationReason.Breadcrumb or FolderNavigationReason.Root or FolderNavigationReason.Refresh;
}
