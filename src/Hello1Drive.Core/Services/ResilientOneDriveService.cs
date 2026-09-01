using System.Net;
using Hello1Drive.Models;

namespace Hello1Drive.Services;

/// <summary>
/// Adds transfer-specific resilience around OneDriveService without replaying arbitrary Graph
/// mutations. Mobile operating systems can temporarily park a socket while an app is moving to
/// the background. That often surfaces as OperationCanceledException/HttpClient timeout even though
/// the user never cancelled the transfer. A transfer therefore gets a progress watchdog and a
/// retry loop which is independent from the UI/View lifetime.
/// </summary>
public sealed class ResilientOneDriveService : IOneDriveService
{
    // A healthy Graph transfer reports progress much more frequently than this. The watchdog is
    // intentionally longer than OneDriveService's normal 30-second request timeout, but shorter
    // than platform handlers which can otherwise sit on a dead background socket for several
    // minutes (the observed failure was around 300 seconds).
    private static readonly TimeSpan TransferStallTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(15);

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
        // A non-seekable picker stream cannot be replayed safely. It still benefits from the
        // inner service's resumable-upload retry logic, but we must not restart it from here.
        if (!source.CanSeek)
        {
            await _inner.UploadFileAsync(parentItemId, fileName, source, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var originalPosition = source.Position;
        long? remainingLength = null;
        try
        {
            remainingLength = Math.Max(0, source.Length - originalPosition);
        }
        catch
        {
            // Seekable streams normally expose Length, but retrying still works without it.
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.Position = originalPosition;
            if (attempt > 0)
                progress?.Report(0);

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(TransferStallTimeout);
            var heartbeat = new TransferHeartbeatProgress(progress, attemptCts, TransferStallTimeout);

            try
            {
                await _inner.UploadFileAsync(parentItemId, fileName, source, heartbeat, attemptCts.Token)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ShouldRetryAttempt(ex, cancellationToken, attemptCts))
            {
                // If the socket disappeared after the final request body was sent, the server may
                // already have committed the item even though the client never saw the response.
                // Verify before replaying the whole upload so a background timeout cannot create a
                // second "file (1).ext" copy.
                if (heartbeat.LastValue >= 0.995 && remainingLength is long expectedLength &&
                    await TryVerifyCompletedUploadAsync(
                        parentItemId,
                        fileName,
                        expectedLength,
                        startedUtc,
                        cancellationToken).ConfigureAwait(false))
                {
                    progress?.Report(1.0);
                    return;
                }

                await DelayBeforeTransferRetryAsync(attempt++, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task UpdateFileContentAsync(
        string itemId,
        Stream source,
        string contentType = "text/plain; charset=utf-8",
        CancellationToken cancellationToken = default) =>
        _inner.UpdateFileContentAsync(itemId, source, contentType, cancellationToken);

    public async Task DownloadFileAsync(
        string itemId,
        Stream destination,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!destination.CanSeek)
        {
            await _inner.DownloadFileAsync(itemId, destination, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Position = 0;
            destination.SetLength(0);
            if (attempt > 0)
                progress?.Report(0);

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(TransferStallTimeout);
            var heartbeat = new TransferHeartbeatProgress(progress, attemptCts, TransferStallTimeout);

            try
            {
                await _inner.DownloadFileAsync(itemId, destination, heartbeat, attemptCts.Token)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ShouldRetryAttempt(ex, cancellationToken, attemptCts))
            {
                await DelayBeforeTransferRetryAsync(attempt++, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task<string?> GetDownloadUrlAsync(string itemId, CancellationToken cancellationToken = default) =>
        _inner.GetDownloadUrlAsync(itemId, cancellationToken);

    private async Task<bool> TryVerifyCompletedUploadAsync(
        string? parentItemId,
        string requestedName,
        long expectedLength,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            using var verifyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            verifyCts.CancelAfter(VerificationTimeout);
            var children = await _inner.GetChildrenAsync(parentItemId, verifyCts.Token).ConfigureAwait(false);

            return children.Any(item =>
                item.IsFile &&
                item.Size == expectedLength &&
                LooksLikeRequestedUploadName(requestedName, item.Name) &&
                item.LastModifiedDateTime is { } modified &&
                modified >= startedUtc.AddMinutes(-1));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeRequestedUploadName(string requestedName, string candidateName)
    {
        if (string.Equals(requestedName, candidateName, StringComparison.OrdinalIgnoreCase))
            return true;

        var extension = Path.GetExtension(requestedName);
        var stem = Path.GetFileNameWithoutExtension(requestedName);
        var candidateExtension = Path.GetExtension(candidateName);
        var candidateStem = Path.GetFileNameWithoutExtension(candidateName);

        return string.Equals(extension, candidateExtension, StringComparison.OrdinalIgnoreCase) &&
               candidateStem.StartsWith(stem + " (", StringComparison.OrdinalIgnoreCase) &&
               candidateStem.EndsWith(')');
    }

    private static bool ShouldRetryAttempt(
        Exception ex,
        CancellationToken userCancellationToken,
        CancellationTokenSource attemptCts)
    {
        if (userCancellationToken.IsCancellationRequested)
            return false;

        // Our progress watchdog deliberately cancels only the current socket attempt. Android and
        // iOS handlers can surface that as either TaskCanceledException or plain
        // OperationCanceledException, so both are retryable when the user token is still live.
        if (attemptCts.IsCancellationRequested && ex is OperationCanceledException)
            return true;

        return IsTransientTransferFailure(ex, userCancellationToken);
    }

    private static bool IsTransientTransferFailure(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        // A platform HttpMessageHandler may use plain OperationCanceledException for its own
        // timeout. If our caller token is not cancelled, this is a transport timeout, not a user
        // cancellation, and must not turn the transfer row into Failed.
        if (ex is OperationCanceledException)
            return true;

        if (ex is HttpRequestException http)
        {
            if (http.StatusCode is null)
                return true;

            var code = (int)http.StatusCode.Value;
            return code is 408 or 425 or 429 || code >= 500;
        }

        if (ex is TimeoutException)
            return true;

        return ex.InnerException is not null && IsTransientTransferFailure(ex.InnerException, cancellationToken);
    }

    private static async Task DelayBeforeTransferRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var exponential = Math.Min(10000d, 500d * Math.Pow(2, Math.Min(attempt, 5)));
        var delay = TimeSpan.FromMilliseconds(exponential + Random.Shared.Next(0, 350));
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private sealed class TransferHeartbeatProgress : IProgress<double>
    {
        private readonly IProgress<double>? _innerProgress;
        private readonly CancellationTokenSource _attemptCts;
        private readonly TimeSpan _stallTimeout;
        private double _lastValue;

        public TransferHeartbeatProgress(
            IProgress<double>? innerProgress,
            CancellationTokenSource attemptCts,
            TimeSpan stallTimeout)
        {
            _innerProgress = innerProgress;
            _attemptCts = attemptCts;
            _stallTimeout = stallTimeout;
        }

        public double LastValue => Volatile.Read(ref _lastValue);

        public void Report(double value)
        {
            value = Math.Clamp(value, 0, 1);
            Volatile.Write(ref _lastValue, value);

            try
            {
                if (!_attemptCts.IsCancellationRequested)
                    _attemptCts.CancelAfter(_stallTimeout);
            }
            catch (ObjectDisposedException)
            {
                // A late progress callback after the attempt completed is harmless.
            }

            _innerProgress?.Report(value);
        }
    }
}
