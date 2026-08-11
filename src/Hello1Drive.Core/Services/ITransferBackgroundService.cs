namespace Hello1Drive.Services;

/// <summary>
/// Lets a platform head keep user-initiated file transfers alive while the UI is backgrounded.
/// Desktop/iOS/Browser may leave this service unconfigured; Android uses a dataSync foreground service.
/// </summary>
public interface ITransferBackgroundService
{
    void Update(TransferBackgroundState state);
}

public readonly record struct TransferBackgroundState(
    int ActiveCount,
    int RunningCount,
    int UploadCount,
    int DownloadCount,
    int CacheCount)
{
    public bool HasActiveTransfers => ActiveCount > 0;
}
