using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foundation;
using Hello1Drive.Configuration;
using Hello1Drive.Models;
using Hello1Drive.Services;
using UIKit;

namespace Hello1Drive.iOS.Services;

/// <summary>
/// iOS transport wrapper for user initiated file transfers. Metadata and short Graph operations
/// continue to use the shared OneDrive service, while upload/download bytes are handed to a
/// background NSURLSession so iOS, rather than the Avalonia UI process, owns the network socket
/// while the app is locked or suspended.
/// </summary>
public sealed class IosBackgroundOneDriveService : IOneDriveService, IDisposable
{
    private const string SessionIdentifier = "com.xiaowei.hello1drive.background-transfer";
    private const long SimpleUploadLimit = 250L * 1024 * 1024;
    private const int UploadChunkSize = 5 * 1024 * 1024; // 16 * 320 KiB, valid for Graph upload sessions.

    private static readonly object StaticSync = new();
    private static IosBackgroundOneDriveService? _current;
    private static Action? _pendingBackgroundCompletionHandler;
    private static string? _pendingBackgroundSessionIdentifier;

    private readonly IAuthenticationService _authentication;
    private readonly IOneDriveService _inner;
    private readonly BackgroundSessionDelegate _delegate;
    private readonly NSUrlSession _session;
    private readonly HttpClient _controlClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentDictionary<UIntPtr, NativeTaskOperation> _operations = new();
    private readonly string _workDirectory;
    private readonly object _backgroundEventsSync = new();

    private Action? _backgroundEventsCompletionHandler;
    private bool _backgroundEventsFinished;
    private int _activeManagedOperations;
    private bool _disposed;

    public IosBackgroundOneDriveService(IAuthenticationService authentication, IOneDriveService inner)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        var localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localRoot))
            localRoot = Path.GetTempPath();
        _workDirectory = Path.Combine(localRoot, "Hello1Drive", "ios-background-transfers");
        Directory.CreateDirectory(_workDirectory);

        _delegate = new BackgroundSessionDelegate(this);
        using var configuration = NSUrlSessionConfiguration.CreateBackgroundSessionConfiguration(SessionIdentifier);
        configuration.SessionSendsLaunchEvents = true;
        configuration.WaitsForConnectivity = true;
        configuration.Discretionary = false;
        configuration.AllowsCellularAccess = true;
        configuration.HttpMaximumConnectionsPerHost = 2;
        // A background session must not impose a short app-side resource deadline. Connectivity
        // scheduling/retry belongs to iOS and the Graph service once the native task is resumed.
        configuration.TimeoutIntervalForRequest = 60;
        configuration.TimeoutIntervalForResource = 7 * 24 * 60 * 60;
        _session = NSUrlSession.FromConfiguration(configuration, _delegate, null);

        lock (StaticSync)
        {
            _current = this;
            if (string.Equals(_pendingBackgroundSessionIdentifier, SessionIdentifier, StringComparison.Ordinal))
            {
                SetBackgroundEventsCompletionHandler(_pendingBackgroundCompletionHandler);
                _pendingBackgroundCompletionHandler = null;
                _pendingBackgroundSessionIdentifier = null;
            }
        }
    }

    /// <summary>
    /// Called by AppDelegate when iOS relaunches/wakes the app to deliver background NSURLSession
    /// callbacks. The completion handler is retained until every managed chained upload operation
    /// has finished scheduling its next chunk.
    /// </summary>
    public static void HandleEventsForBackgroundUrl(string sessionIdentifier, Action completionHandler)
    {
        lock (StaticSync)
        {
            if (_current is { } current &&
                string.Equals(sessionIdentifier, SessionIdentifier, StringComparison.Ordinal))
            {
                current.SetBackgroundEventsCompletionHandler(completionHandler);
                return;
            }

            _pendingBackgroundSessionIdentifier = sessionIdentifier;
            _pendingBackgroundCompletionHandler = completionHandler;
        }
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
        ThrowIfDisposed();
        BeginManagedOperation();
        string? stagedPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markerPath = GetUploadSuccessMarkerPath(parentItemId, fileName, TryGetRemainingLength(source));
            if (File.Exists(markerPath))
            {
                TryDeleteFile(markerPath);
                progress?.Report(1);
                return;
            }

            // NSURLSession background uploads must be file-backed. Give this short local staging
            // step iOS's ordinary background-task grace period; once Resume() is called the OS owns
            // the network work and this lease is no longer needed.
            stagedPath = CreateWorkPath("upload", Path.GetExtension(fileName));
            await WithShortBackgroundLeaseAsync(
                "Prepare OneDrive upload",
                async () => await StageStreamAsync(source, stagedPath, cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);

            var length = new FileInfo(stagedPath).Length;
            if (length <= SimpleUploadLimit)
            {
                await UploadSimpleInBackgroundAsync(
                    parentItemId,
                    fileName,
                    stagedPath,
                    markerPath,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await UploadLargeInBackgroundAsync(
                    parentItemId,
                    fileName,
                    stagedPath,
                    length,
                    markerPath,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            TryDeleteFile(markerPath);
            progress?.Report(1);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(stagedPath))
                TryDeleteFile(stagedPath);
            EndManagedOperation();
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
        ThrowIfDisposed();
        BeginManagedOperation();
        var resultPath = GetDownloadResultPath(itemId);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // If iOS finished this NSURLSession task after process suspension/relaunch, its delegate
            // already moved the ephemeral download to this deterministic staging path. Reuse it
            // instead of downloading the same file a second time when transfer persistence resumes.
            if (!File.Exists(resultPath))
            {
                var token = await _authentication.GetAccessTokenAsync(false, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(token))
                    throw new InvalidOperationException("未登录 Microsoft 账户。");

                using var request = CreateAuthorizedRequest(
                    "GET",
                    $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(itemId)}/content",
                    token);
                var descriptor = new NativeTaskDescriptor
                {
                    Kind = NativeTaskKind.Download,
                    DownloadResultPath = resultPath
                };

                var task = _session.CreateDownloadTask(request);
                task.TaskDescription = JsonSerializer.Serialize(descriptor);
                await AwaitNativeTaskAsync(task, progress, descriptor, cancellationToken).ConfigureAwait(false);

                if (!File.Exists(resultPath))
                    throw new IOException("iOS 后台下载完成，但临时文件未能保存。请重新下载。");
            }

            await WithShortBackgroundLeaseAsync(
                "Finish OneDrive download",
                async () =>
                {
                    await using var source = new FileStream(
                        resultPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        256 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (destination.CanSeek)
                    {
                        destination.Position = 0;
                        destination.SetLength(0);
                    }
                    await source.CopyToAsync(destination, 256 * 1024, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);

            progress?.Report(1);
        }
        finally
        {
            TryDeleteFile(resultPath);
            EndManagedOperation();
        }
    }

    public Task<string?> GetDownloadUrlAsync(string itemId, CancellationToken cancellationToken = default) =>
        _inner.GetDownloadUrlAsync(itemId, cancellationToken);

    private async Task UploadSimpleInBackgroundAsync(
        string? parentItemId,
        string fileName,
        string stagedPath,
        string markerPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var token = await _authentication.GetAccessTokenAsync(false, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("未登录 Microsoft 账户。");

        var escapedName = Uri.EscapeDataString(fileName);
        var url = parentItemId is null
            ? $"{AppConfig.GraphBaseUrl}/me/drive/root:/{escapedName}:/content"
            : $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(parentItemId)}:/{escapedName}:/content";

        using var request = CreateAuthorizedRequest("PUT", url, token);
        request["Content-Type"] = "application/octet-stream";
        var descriptor = new NativeTaskDescriptor
        {
            Kind = NativeTaskKind.Upload,
            SuccessMarkerPath = markerPath,
            IsFinalUploadPart = true
        };

        await RunBackgroundUploadWithRetryAsync(
            request,
            stagedPath,
            progress,
            descriptor,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task UploadLargeInBackgroundAsync(
        string? parentItemId,
        string fileName,
        string stagedPath,
        long length,
        string markerPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var uploadUrl = await WithShortBackgroundLeaseAsync(
            "Create OneDrive upload session",
            async () => await CreateGraphUploadSessionAsync(parentItemId, fileName, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        long offset = 0;
        while (offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(UploadChunkSize, length - offset);
            var chunkPath = CreateWorkPath("chunk", ".bin");
            try
            {
                await WriteChunkAsync(stagedPath, chunkPath, offset, count, cancellationToken).ConfigureAwait(false);

                using var request = new NSMutableUrlRequest(new NSUrl(uploadUrl))
                {
                    HttpMethod = "PUT",
                    TimeoutInterval = 60
                };
                request["Content-Type"] = "application/octet-stream";
                request["Content-Length"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                request["Content-Range"] = $"bytes {offset}-{offset + count - 1}/{length}";

                var baseOffset = offset;
                var descriptor = new NativeTaskDescriptor
                {
                    Kind = NativeTaskKind.Upload,
                    SuccessMarkerPath = markerPath,
                    IsFinalUploadPart = offset + count >= length
                };
                var chunkProgress = new InlineProgress(value =>
                    progress?.Report(Math.Clamp((baseOffset + value * count) / length, 0, 1)));

                await RunBackgroundUploadWithRetryAsync(
                    request,
                    chunkPath,
                    chunkProgress,
                    descriptor,
                    cancellationToken).ConfigureAwait(false);

                offset += count;
                progress?.Report((double)offset / length);
            }
            finally
            {
                TryDeleteFile(chunkPath);
            }
        }
    }

    private async Task RunBackgroundUploadWithRetryAsync(
        NSUrlRequest request,
        string filePath,
        IProgress<double>? progress,
        NativeTaskDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var fileUrl = NSUrl.FromFilename(filePath);
            var task = _session.CreateUploadTask(request, fileUrl);
            task.TaskDescription = JsonSerializer.Serialize(descriptor);

            var result = await AwaitNativeTaskAsync(task, progress, descriptor, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
                return;

            if (!result.IsTransient || attempt >= 8)
                throw result.ToException("iOS 后台上传失败");

            var delay = TimeSpan.FromMilliseconds(Math.Min(10000, 500 * Math.Pow(2, attempt)) + Random.Shared.Next(0, 350));
            attempt++;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> CreateGraphUploadSessionAsync(
        string? parentItemId,
        string fileName,
        CancellationToken cancellationToken)
    {
        var token = await _authentication.GetAccessTokenAsync(false, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("未登录 Microsoft 账户。");

        var escapedName = Uri.EscapeDataString(fileName);
        var url = parentItemId is null
            ? $"{AppConfig.GraphBaseUrl}/me/drive/root:/{escapedName}:/createUploadSession"
            : $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(parentItemId)}:/{escapedName}:/createUploadSession";

        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["item"] = new Dictionary<string, object>
            {
                ["@microsoft.graph.conflictBehavior"] = "rename",
                ["name"] = fileName
            }
        });

        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            try
            {
                using var response = await _controlClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode is 408 or 425 or 429 || (int)response.StatusCode >= 500)
                {
                    if (attempt++ < 8)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(8000, 500 * Math.Pow(2, attempt))), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"创建 OneDrive 上传会话失败：{(int)response.StatusCode} {body}");

                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("uploadUrl", out var uploadUrl) &&
                    !string.IsNullOrWhiteSpace(uploadUrl.GetString()))
                {
                    return uploadUrl.GetString()!;
                }

                throw new InvalidOperationException("Microsoft Graph 未返回上传会话 URL。");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException &&
                                       !cancellationToken.IsCancellationRequested && attempt++ < 8)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(8000, 500 * Math.Pow(2, attempt))), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<NativeTaskResult> AwaitNativeTaskAsync(
        NSUrlSessionTask task,
        IProgress<double>? progress,
        NativeTaskDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var operation = new NativeTaskOperation(progress, descriptor);
        if (!_operations.TryAdd(task.TaskIdentifier, operation))
            throw new InvalidOperationException("无法注册 iOS 后台传输任务。");

        using var registration = cancellationToken.Register(static state =>
        {
            try { ((NSUrlSessionTask)state!).Cancel(); } catch { }
        }, task);

        try
        {
            task.Resume();
            var result = await operation.Completion.Task.ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            return result;
        }
        finally
        {
            _operations.TryRemove(task.TaskIdentifier, out _);
            task.Dispose();
        }
    }

    private void OnDownloadFinished(NSUrlSessionDownloadTask task, NSUrl location)
    {
        NativeTaskDescriptor? descriptor = null;
        if (_operations.TryGetValue(task.TaskIdentifier, out var operation))
            descriptor = operation.Descriptor;
        descriptor ??= TryParseDescriptor(task.TaskDescription);

        var resultPath = descriptor?.DownloadResultPath;
        if (string.IsNullOrWhiteSpace(resultPath) || string.IsNullOrWhiteSpace(location.Path))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.Copy(location.Path, resultPath, true);
            if (operation is not null)
                operation.DownloadResultPath = resultPath;
        }
        catch (Exception ex)
        {
            operation?.SetLocalFileError(ex);
        }
    }

    private void OnUploadProgress(NSUrlSessionTask task, long sent, long expected)
    {
        if (expected <= 0 || !_operations.TryGetValue(task.TaskIdentifier, out var operation))
            return;
        operation.Progress?.Report(Math.Clamp((double)sent / expected, 0, 1));
    }

    private void OnDownloadProgress(NSUrlSessionDownloadTask task, long written, long expected)
    {
        if (expected <= 0 || !_operations.TryGetValue(task.TaskIdentifier, out var operation))
            return;
        operation.Progress?.Report(Math.Clamp((double)written / expected, 0, 1));
    }

    private void OnTaskCompleted(NSUrlSessionTask task, NSError? error)
    {
        _operations.TryGetValue(task.TaskIdentifier, out var operation);
        var descriptor = operation?.Descriptor ?? TryParseDescriptor(task.TaskDescription);
        var statusCode = task.Response is NSHttpUrlResponse http ? (int)http.StatusCode : 0;
        var successfulHttp = statusCode is >= 200 and < 300;

        if (error is null && successfulHttp && descriptor is { IsFinalUploadPart: true } &&
            !string.IsNullOrWhiteSpace(descriptor.SuccessMarkerPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(descriptor.SuccessMarkerPath)!);
                File.WriteAllText(descriptor.SuccessMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
            }
            catch
            {
                // Marker recovery is an optimization. The active managed task can still complete.
            }
        }

        if (operation is null)
            return;

        if (operation.LocalFileError is { } localError)
        {
            operation.Completion.TrySetResult(NativeTaskResult.LocalFailure(localError));
            return;
        }

        if (error is not null)
        {
            operation.Completion.TrySetResult(NativeTaskResult.NativeFailure(error));
            return;
        }

        if (!successfulHttp)
        {
            operation.Completion.TrySetResult(NativeTaskResult.HttpFailure(statusCode));
            return;
        }

        operation.Progress?.Report(1);
        operation.Completion.TrySetResult(NativeTaskResult.Success(statusCode));
    }

    private void SetBackgroundEventsCompletionHandler(Action? completionHandler)
    {
        if (completionHandler is null)
            return;

        Action? callNow = null;
        lock (_backgroundEventsSync)
        {
            _backgroundEventsCompletionHandler = completionHandler;
            _backgroundEventsFinished = false;
            if (Volatile.Read(ref _activeManagedOperations) == 0)
            {
                // Wait for NSURLSessionDelegate.DidFinishEventsForBackgroundSession before
                // releasing iOS's wake; simply having no managed operation is not sufficient.
            }
        }
        callNow?.Invoke();
    }

    private void OnBackgroundEventsFinished()
    {
        Action? completion = null;
        lock (_backgroundEventsSync)
        {
            _backgroundEventsFinished = true;
            if (Volatile.Read(ref _activeManagedOperations) == 0)
            {
                completion = _backgroundEventsCompletionHandler;
                _backgroundEventsCompletionHandler = null;
                _backgroundEventsFinished = false;
            }
        }
        completion?.Invoke();
    }

    private void BeginManagedOperation() => Interlocked.Increment(ref _activeManagedOperations);

    private void EndManagedOperation()
    {
        if (Interlocked.Decrement(ref _activeManagedOperations) != 0)
            return;

        Action? completion = null;
        lock (_backgroundEventsSync)
        {
            if (_backgroundEventsFinished)
            {
                completion = _backgroundEventsCompletionHandler;
                _backgroundEventsCompletionHandler = null;
                _backgroundEventsFinished = false;
            }
        }
        completion?.Invoke();
    }

    private static NSMutableUrlRequest CreateAuthorizedRequest(string method, string url, string token)
    {
        var request = new NSMutableUrlRequest(new NSUrl(url))
        {
            HttpMethod = method,
            TimeoutInterval = 60
        };
        request["Authorization"] = $"Bearer {token}";
        return request;
    }

    private async Task WithShortBackgroundLeaseAsync(string name, Func<Task> action)
    {
        var app = UIApplication.SharedApplication;
        var backgroundTask = app.BeginBackgroundTask(name, null);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            if (backgroundTask != UIApplication.BackgroundTaskInvalid)
                app.EndBackgroundTask(backgroundTask);
        }
    }

    private async Task<T> WithShortBackgroundLeaseAsync<T>(string name, Func<Task<T>> action)
    {
        var app = UIApplication.SharedApplication;
        var backgroundTask = app.BeginBackgroundTask(name, null);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            if (backgroundTask != UIApplication.BackgroundTaskInvalid)
                app.EndBackgroundTask(backgroundTask);
        }
    }

    private static async Task StageStreamAsync(Stream source, string path, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(output, 256 * 1024, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteChunkAsync(
        string sourcePath,
        string chunkPath,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        source.Position = offset;

        await using var output = new FileStream(
            chunkPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[256 * 1024];
        var remaining = count;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
                throw new EndOfStreamException("生成 iOS 后台上传分片时源文件提前结束。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private string GetDownloadResultPath(string itemId) =>
        Path.Combine(_workDirectory, $"download-{StableHash(itemId)}.bin");

    private string GetUploadSuccessMarkerPath(string? parentItemId, string fileName, long? length) =>
        Path.Combine(
            _workDirectory,
            $"upload-{StableHash($"{parentItemId ?? "__ROOT__"}|{fileName}|{length?.ToString() ?? "?"}")}.done");

    private string CreateWorkPath(string prefix, string? extension)
    {
        var safeExtension = string.IsNullOrWhiteSpace(extension) || extension.Length > 16 ? ".tmp" : extension;
        return Path.Combine(_workDirectory, $"{prefix}-{Guid.NewGuid():N}{safeExtension}");
    }

    private static long? TryGetRemainingLength(Stream source)
    {
        try
        {
            return source.CanSeek ? Math.Max(0, source.Length - source.Position) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string StableHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static NativeTaskDescriptor? TryParseDescriptor(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try { return JsonSerializer.Deserialize<NativeTaskDescriptor>(json); }
        catch { return null; }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // iOS can still have the URLSession file open for a short moment; stale work files are
            // harmless and use GUID/hash names, so cleanup failure must never fail a transfer.
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(IosBackgroundOneDriveService));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock (StaticSync)
        {
            if (ReferenceEquals(_current, this))
                _current = null;
        }

        _controlClient.Dispose();
        _session.FinishTasksAndInvalidate();
        _delegate.Dispose();
    }

    private sealed class BackgroundSessionDelegate(IosBackgroundOneDriveService owner) : NSUrlSessionDownloadDelegate
    {
        public override void DidSendBodyData(
            NSUrlSession session,
            NSUrlSessionTask task,
            long bytesSent,
            long totalBytesSent,
            long totalBytesExpectedToSend) =>
            owner.OnUploadProgress(task, totalBytesSent, totalBytesExpectedToSend);

        public override void DidWriteData(
            NSUrlSession session,
            NSUrlSessionDownloadTask downloadTask,
            long bytesWritten,
            long totalBytesWritten,
            long totalBytesExpectedToWrite) =>
            owner.OnDownloadProgress(downloadTask, totalBytesWritten, totalBytesExpectedToWrite);

        public override void DidFinishDownloading(
            NSUrlSession session,
            NSUrlSessionDownloadTask downloadTask,
            NSUrl location) =>
            owner.OnDownloadFinished(downloadTask, location);

        public override void DidCompleteWithError(NSUrlSession session, NSUrlSessionTask task, NSError? error) =>
            owner.OnTaskCompleted(task, error);

        public override void DidFinishEventsForBackgroundSession(NSUrlSession session) =>
            owner.OnBackgroundEventsFinished();
    }

    private sealed class NativeTaskOperation(IProgress<double>? progress, NativeTaskDescriptor descriptor)
    {
        public IProgress<double>? Progress { get; } = progress;
        public NativeTaskDescriptor Descriptor { get; } = descriptor;
        public TaskCompletionSource<NativeTaskResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? DownloadResultPath { get; set; }
        public Exception? LocalFileError { get; private set; }
        public void SetLocalFileError(Exception error) => LocalFileError ??= error;
    }

    private enum NativeTaskKind
    {
        Upload,
        Download
    }

    private sealed class NativeTaskDescriptor
    {
        public NativeTaskKind Kind { get; set; }
        public string? DownloadResultPath { get; set; }
        public string? SuccessMarkerPath { get; set; }
        public bool IsFinalUploadPart { get; set; }
    }

    private readonly record struct NativeTaskResult(
        bool IsSuccess,
        bool IsTransient,
        int StatusCode,
        string? ErrorMessage,
        Exception? Exception)
    {
        public static NativeTaskResult Success(int statusCode) =>
            new(true, false, statusCode, null, null);

        public static NativeTaskResult HttpFailure(int statusCode) =>
            new(false, statusCode is 408 or 425 or 429 || statusCode >= 500, statusCode,
                statusCode == 0 ? "未收到 HTTP 响应" : $"HTTP {statusCode}", null);

        public static NativeTaskResult NativeFailure(NSError error)
        {
            // NSURLSession background tasks already perform connectivity scheduling. Most native
            // transport errors remain safe to retry; authentication/HTTP errors are reported via
            // the response status rather than NSError.
            return new(false, true, 0, error.LocalizedDescription, null);
        }

        public static NativeTaskResult LocalFailure(Exception error) =>
            new(false, false, 0, error.Message, error);

        public Exception ToException(string prefix) =>
            Exception ?? new IOException($"{prefix}：{ErrorMessage ?? "未知错误"}");
    }

    private sealed class InlineProgress(Action<double> handler) : IProgress<double>
    {
        public void Report(double value) => handler(value);
    }
}
