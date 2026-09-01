using System.Net;
using Hello1Drive.Models;

namespace Hello1Drive.Services;

/// <summary>
/// Adds transfer-specific resilience around OneDriveService without replaying arbitrary Graph
/// mutations. Downloads and resumable uploads already retry inside OneDriveService; the missing
/// case was a normal/simple PUT upload (<= 10 MiB), which could become Failed after one temporary
/// timeout when Android moved to the background. A seekable simple-upload stream is safe to rewind
/// and PUT to the same OneDrive path again, so retry only that narrow operation here.
/// </summary>
public sealed class ResilientOneDriveService : IOneDriveService
{
    private const long SimpleUploadRetryLimit = 10L * 1024 * 1024;
    private readonly IOneDriveService _inner;

    public ResilientOneDriveService(IOneDriveService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public long? UploadBytesPerSecondLimit
    {
        get => _inner.UploadBytesPerSecondLimit;
        set => _inner.UploadBytesPerSecondLimit = value;
    }

    public long? DownloadBytesPerSecondLimit
    {
        get => _inner.DownloadBytesPerSecondLimit;
        set => _inner.DownloadBytesPerSecondLimit = value;
    }

    public Task<GraphUser> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        _inner.GetCurrentUserAsync(cancellationToken);

    public Task<DriveInfoModel> GetDriveInfoAsync(CancellationToken cancellationToken = default) =>
        _inner.GetDriveInfoAsync(cancellationToken);

    public Task<byte[]?> GetProfilePhotoAsync(CancellationToken cancellationToken = default) =>
        _inner.GetProfilePhotoAsync(cancellationToken);

    public Task<IReadOnlyList<DriveItemModel>> GetChildrenAsync(string? parentItemId, CancellationToken cancellationToken = default) =>
        _inner.GetChildrenAsync(parentItemId, cancellationToken);

    public Task<IReadOnlyList<DriveItemModel>> GetChildFoldersAsync(string? parentItemId, CancellationToken cancellationToken = default) =>
        _inner.GetChildFoldersAsync(parentItemId, cancellationToken);

    public Task<DriveItemPage> GetChildrenPageAsync(
        string? parentItemId,
        string? nextLink = null,
        int pageSize = 120,
        CancellationToken cancellationToken = default,
        string? orderBy = null) =>
        _inner.GetChildrenPageAsync(parentItemId, nextLink, pageSize, cancellationToken, orderBy);

    public Task<DriveDeltaPage> GetDriveDeltaPageAsync(
        string? deltaOrNextLink = null,
        int pageSize = 200,
        CancellationToken cancellationToken = default) =>
        _inner.GetDriveDeltaPageAsync(deltaOrNextLink, pageSize, cancellationToken);

    public Task<DriveItemModel> GetItemMetadataAsync(string? itemId, CancellationToken cancellationToken = default) =>
        _inner.GetItemMetadataAsync(itemId, cancellationToken);

    public Task<byte[]?> GetThumbnailAsync(DriveItemModel item, CancellationToken cancellationToken = default) =>
        _inner.GetThumbnailAsync(item, cancellationToken);

    public Task<DriveItemModel> CreateFolderAsync(string? parentItemId, string name, CancellationToken cancellationToken = default) =>
        _inner.CreateFolderAsync(parentItemId, name, cancellationToken);

    public Task<DriveItemModel> RenameAsync(string itemId, string newName, CancellationToken cancellationToken = default) =>
        _inner.RenameAsync(itemId, newName, cancellationToken);

    public Task DeleteAsync(string itemId, CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(itemId, cancellationToken);

    public Task MoveAsync(string itemId, string targetFolderId, CancellationToken cancellationToken = default) =>
        _inner.MoveAsync(itemId, targetFolderId, cancellationToken);

    public Task CopyAsync(string itemId, string targetFolderId, CancellationToken cancellationToken = default) =>
        _inner.CopyAsync(itemId, targetFolderId, cancellationToken);

    public Task<string> CreateShareLinkAsync(string itemId, CancellationToken cancellationToken = default) =>
        _inner.CreateShareLinkAsync(itemId, cancellationToken);

    public async Task UploadFileAsync(
        string? parentItemId,
        string fileName,
        Stream source,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanRetrySimpleUpload(source))
        {
            await _inner.UploadFileAsync(parentItemId, fileName, source, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var originalPosition = source.Position;
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.Position = originalPosition;
            if (attempt > 0)
                progress?.Report(0);

            try
            {
                await _inner.UploadFileAsync(parentItemId, fileName, source, progress, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsTransientTransferFailure(ex, cancellationToken))
            {
                var exponential = Math.Min(8000d, 500d * Math.Pow(2, Math.Min(attempt, 4)));
                var delay = TimeSpan.FromMilliseconds(exponential + Random.Shared.Next(0, 250));
                attempt++;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task UpdateFileContentAsync(
        string itemId,
        Stream source,
        string contentType = "text/plain; charset=utf-8",
        CancellationToken cancellationToken = default) =>
        _inner.UpdateFileContentAsync(itemId, source, contentType, cancellationToken);

    public Task DownloadFileAsync(
        string itemId,
        Stream destination,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        _inner.DownloadFileAsync(itemId, destination, progress, cancellationToken);

    public Task<string?> GetDownloadUrlAsync(string itemId, CancellationToken cancellationToken = default) =>
        _inner.GetDownloadUrlAsync(itemId, cancellationToken);

    private static bool CanRetrySimpleUpload(Stream source)
    {
        if (!source.CanSeek)
            return false;

        try
        {
            return source.Length <= SimpleUploadRetryLimit;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTransientTransferFailure(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || ex is OperationCanceledException && ex is not TaskCanceledException)
            return false;

        if (ex is HttpRequestException http)
        {
            if (http.StatusCode is null)
                return true;

            var code = (int)http.StatusCode.Value;
            return code is 408 or 425 or 429 || code >= 500;
        }

        // HttpClient uses TaskCanceledException for its own request timeout as well as explicit
        // cancellation. The token check above distinguishes the two cases.
        if (ex is TaskCanceledException or TimeoutException)
            return true;

        return ex.InnerException is not null && IsTransientTransferFailure(ex.InnerException, cancellationToken);
    }
}
