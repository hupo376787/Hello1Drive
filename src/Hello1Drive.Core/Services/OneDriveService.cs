using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hello1Drive.Configuration;
using Hello1Drive.Models;

namespace Hello1Drive.Services;

public sealed class OneDriveService : IOneDriveService
{
    private const long SimpleUploadLimit = 250L * 1024 * 1024;
    private const long UploadSessionThreshold = 10L * 1024 * 1024;
    private const int UploadChunkSize = 5 * 1024 * 1024; // 5 MiB = 16 * 320 KiB; smoother progress while staying within Graph guidance.

    private readonly IAuthenticationService _authentication;
    private readonly HttpClient _httpClient = new();
    private string? _driveId;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public OneDriveService(IAuthenticationService authentication)
    {
        _authentication = authentication;
        // A dead Wi-Fi/TLS path should fail into our cancellation-aware retry loop instead of
        // occupying one Graph request for HttpClient's much longer default timeout.
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public long? UploadBytesPerSecondLimit { get; set; }
    public long? DownloadBytesPerSecondLimit { get; set; }

    public async Task<GraphUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{AppConfig.GraphBaseUrl}/me?$select=id,displayName,mail,userPrincipalName");
        using var response = await SendAsync(request, cancellationToken);
        return await DeserializeAsync<GraphUser>(response, cancellationToken);
    }

    public async Task<DriveInfoModel> GetDriveInfoAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{AppConfig.GraphBaseUrl}/me/drive?$select=id,driveType,quota");
        using var response = await SendAsync(request, cancellationToken);
        var info = await DeserializeAsync<DriveInfoModel>(response, cancellationToken);
        _driveId = info.Id;
        return info;
    }

    public async Task<byte[]?> GetProfilePhotoAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{AppConfig.GraphBaseUrl}/me/photo/$value");
        using var response = await SendAsync(request, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DriveItemModel>> GetChildrenAsync(string? parentItemId, CancellationToken cancellationToken = default)
    {
        var all = new List<DriveItemModel>();
        string? nextLink = null;
        do
        {
            var page = await GetChildrenPageAsync(parentItemId, nextLink, 200, cancellationToken).ConfigureAwait(false);
            all.AddRange(page.Items);
            nextLink = page.NextLink;
        } while (!string.IsNullOrWhiteSpace(nextLink));

        return all;
    }

    public async Task<IReadOnlyList<DriveItemModel>> GetChildFoldersAsync(
        string? parentItemId,
        CancellationToken cancellationToken = default)
    {
        // The move/copy destination browser only needs folders. Microsoft Graph's
        // driveItem/children endpoint does not support a server-side folder-only $filter,
        // so request only the minimal facets and filter locally. Avoiding $expand=thumbnails
        // makes deep destination navigation much cheaper than the normal file-list request.
        var folders = new List<DriveItemModel>();
        string? nextLink = null;

        do
        {
            var url = nextLink;
            if (string.IsNullOrWhiteSpace(url))
            {
                var baseUrl = parentItemId is null
                    ? $"{AppConfig.GraphBaseUrl}/me/drive/root/children"
                    : $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(parentItemId)}/children";
                url = baseUrl + "?$top=200&$select=id,name,webUrl,folder,remoteItem,specialFolder,parentReference";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            var page = await DeserializeAsync<GraphCollectionResponse<DriveItemModel>>(response, cancellationToken)
                .ConfigureAwait(false);

            // Personal Vault is intentionally excluded from the app-side move/copy picker.
            // It requires OneDrive's own extra verification flow and cannot be used as a
            // normal Graph destination by this third-party client.
            folders.AddRange(page.Value.Where(static item => item.IsFolder && !item.IsPersonalVault));
            nextLink = page.NextLink;
        }
        while (!string.IsNullOrWhiteSpace(nextLink));

        return folders;
    }

    public async Task<DriveItemPage> GetChildrenPageAsync(
        string? parentItemId,
        string? nextLink = null,
        int pageSize = 120,
        CancellationToken cancellationToken = default,
        string? orderBy = null)
    {
        pageSize = Math.Clamp(pageSize, 20, 200);
        var url = nextLink;
        if (string.IsNullOrWhiteSpace(url))
        {
            var baseUrl = parentItemId is null
                ? $"{AppConfig.GraphBaseUrl}/me/drive/root/children"
                : $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(parentItemId)}/children";
            // Request specialFolder explicitly. Some OneDrive consumer responses omit that
            // facet from the default children payload; without it Personal Vault can look like
            // a normal folder and the next /children request fails with 422.
            const string select = "id,name,size,webUrl,createdDateTime,lastModifiedDateTime,eTag,cTag,file,folder,remoteItem,specialFolder,parentReference";
            var query = $"?$top={pageSize}&$select={select}&$expand=thumbnails";
            if (!string.IsNullOrWhiteSpace(orderBy))
                query += $"&$orderby={Uri.EscapeDataString(orderBy)}";
            url = baseUrl + query;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Microsoft documents `size` as a supported OneDrive $orderby field, but some
        // consumer storage backends map it to the non-indexed internal
        // SMTotalFileStreamSize property and return 501/notSupported unless this SharePoint
        // compatibility preference is present. Apply it to the initial request and to Graph
        // nextLink pages so a large folder stays consistently ordered.
        var sizeOrderRequested = IsSizeOrderRequest(url, orderBy);
        if (sizeOrderRequested)
            request.Headers.TryAddWithoutValidation("Prefer", "HonorNonIndexedQueriesWarningMayFailRandomly");

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (sizeOrderRequested && response.StatusCode == HttpStatusCode.NotImplemented &&
                (detail.Contains("SMTotalFileStreamSize", StringComparison.OrdinalIgnoreCase) ||
                 detail.Contains("notSupported", StringComparison.OrdinalIgnoreCase)))
            {
                throw new GraphOrderByNotSupportedException("大小", detail);
            }

            if ((int)response.StatusCode == 422 &&
                (detail.Contains("getChildrenOnNonFolder", StringComparison.OrdinalIgnoreCase) ||
                 detail.Contains("Children cannot be listed from an item that is not a folder", StringComparison.OrdinalIgnoreCase)))
            {
                throw new GraphChildrenOnNonFolderException(detail);
            }

            throw new HttpRequestException(
                $"Microsoft Graph 请求失败：{(int)response.StatusCode} {response.ReasonPhrase}\n{detail}",
                null,
                response.StatusCode);
        }

        var page = await DeserializeAsync<GraphCollectionResponse<DriveItemModel>>(response, cancellationToken).ConfigureAwait(false);
        return new DriveItemPage
        {
            Items = page.Value,
            NextLink = page.NextLink
        };
    }

    public async Task<DriveDeltaPage> GetDriveDeltaPageAsync(
        string? deltaOrNextLink = null,
        int pageSize = 200,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 20, 200);
        var url = deltaOrNextLink;
        if (string.IsNullOrWhiteSpace(url))
        {
            const string select = "id,name,size,webUrl,createdDateTime,lastModifiedDateTime,eTag,cTag,file,folder,remoteItem,specialFolder,parentReference,deleted,root";
            url = $"{AppConfig.GraphBaseUrl}/me/drive/root/delta?$top={pageSize}&$select={select}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            return new DriveDeltaPage
            {
                ResyncRequired = true,
                ResyncLink = response.Headers.Location?.ToString()
            };
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var page = await DeserializeAsync<GraphDeltaCollectionResponse<DriveItemModel>>(response, cancellationToken)
            .ConfigureAwait(false);
        return new DriveDeltaPage
        {
            Items = page.Value,
            NextLink = page.NextLink,
            DeltaLink = page.DeltaLink
        };
    }

    public async Task<DriveItemModel> GetItemMetadataAsync(string? itemId, CancellationToken cancellationToken = default)
    {
        var itemPath = string.IsNullOrWhiteSpace(itemId)
            ? "root"
            : $"items/{Uri.EscapeDataString(itemId)}";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{AppConfig.GraphBaseUrl}/me/drive/{itemPath}?$select=id,name,size,webUrl,createdDateTime,lastModifiedDateTime,eTag,cTag,file,folder,remoteItem,specialFolder,parentReference,root");
        using var response = await SendAsync(request, cancellationToken);
        return await DeserializeAsync<DriveItemModel>(response, cancellationToken);
    }

    public async Task<byte[]?> GetThumbnailAsync(DriveItemModel item, CancellationToken cancellationToken = default)
    {
        if (!item.SupportsThumbnail || string.IsNullOrWhiteSpace(item.Id))
            return null;

        // `$expand=thumbnails` normally gives us a short-lived, cache-safe thumbnail URL.
        // Prefer it because it saves an extra Graph metadata request per item.
        var thumbnailUrl = item.ThumbnailUrl;
        if (!string.IsNullOrWhiteSpace(thumbnailUrl) && Uri.TryCreate(thumbnailUrl, UriKind.Absolute, out var thumbnailUri))
        {
            try
            {
                using var directResponse = await _httpClient.GetAsync(
                    thumbnailUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (directResponse.IsSuccessStatusCode)
                    return await directResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A CDN timeout/SSL/DNS problem should fall through to the authenticated Graph
                // endpoint, whose read-side transport has cancellation-aware automatic retry.
            }
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(item.Id)}/thumbnails/0/medium/content");
        using var response = await SendAsync(request, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<DriveItemModel> CreateFolderAsync(string? parentItemId, string name, CancellationToken cancellationToken = default)
    {
        var url = parentItemId is null
            ? $"{AppConfig.GraphBaseUrl}/me/drive/root/children"
            : $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(parentItemId)}/children";

        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = name,
            ["folder"] = new { },
            ["@microsoft.graph.conflictBehavior"] = "rename"
        }, _jsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await SendAsync(request, cancellationToken);
        return await DeserializeAsync<DriveItemModel>(response, cancellationToken);
    }

    public async Task<DriveItemModel> RenameAsync(string itemId, string newName, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new { name = newName }, _jsonOptions);
        using var request = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(itemId)}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await SendAsync(request, cancellationToken);
        return await DeserializeAsync<DriveItemModel>(response, cancellationToken);
    }

    public async Task DeleteAsync(string itemId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(itemId)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NoContent)
            await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task MoveAsync(string itemId, string targetFolderId, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new
        {
            parentReference = new { id = targetFolderId }
        }, _jsonOptions);

        using var request = new HttpRequestMessage(new HttpMethod("PATCH"),
            $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(itemId)}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task CopyAsync(string itemId, string targetFolderId, CancellationToken cancellationToken = default)
    {
        var driveId = _driveId;
        if (string.IsNullOrWhiteSpace(driveId))
            driveId = (await GetDriveInfoAsync(cancellationToken)).Id;

        var parentReference = string.IsNullOrWhiteSpace(driveId)
            ? new Dictionary<string, string> { ["id"] = targetFolderId }
            : new Dictionary<string, string> { ["driveId"] = driveId, ["id"] = targetFolderId };
        var json = JsonSerializer.Serialize(new { parentReference }, _jsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(itemId)}/copy")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<string> CreateShareLinkAsync(string itemId, CancellationToken cancellationToken = default)
    {
        // Do not force an anonymous/public scope. With scope omitted Microsoft Graph uses the
        // account's/default sharing-link policy, which is safer than silently widening access.
        var json = JsonSerializer.Serialize(new { type = "view" }, _jsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(itemId)}/createLink")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await SendAsync(request, cancellationToken);
        var permission = await DeserializeAsync<SharingPermissionModel>(response, cancellationToken);
        return permission.Link?.WebUrl
            ?? throw new InvalidOperationException("OneDrive 没有返回可分享链接。");
    }

    public async Task UploadFileAsync(
        string? parentItemId,
        string fileName,
        Stream source,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        long? length = source.CanSeek ? source.Length : null;

        if (length is > UploadSessionThreshold)
        {
            await UploadWithSessionAsync(parentItemId, fileName, source, length.Value, progress, cancellationToken);
            return;
        }

        if (length is > SimpleUploadLimit)
            throw new InvalidOperationException("文件超过 250 MB，需要可恢复上传会话，但当前流无法满足上传条件。");

        var escapedName = EscapePathSegment(fileName);
        var url = parentItemId is null
            ? $"{AppConfig.GraphBaseUrl}/me/drive/root:/{escapedName}:/content"
            : $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(parentItemId)}:/{escapedName}:/content";

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            // A custom HttpContent copies in small chunks and reports bytes as they are
            // actually serialized to the HTTP request. This keeps the UI progress bar
            // moving instead of jumping from 0% to 100% for normal PUT uploads.
            Content = new ProgressStreamContent(
                source,
                UploadBytesPerSecondLimit,
                progress,
                "application/octet-stream")
        };

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        progress?.Report(1.0);
    }

    public async Task UpdateFileContentAsync(
        string itemId,
        Stream source,
        string contentType = "text/plain; charset=utf-8",
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(itemId)}/content")
        {
            Content = new StreamContent(source)
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<string?> GetDownloadUrlAsync(string itemId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(itemId)}?$select=id,name,@microsoft.graph.downloadUrl");
        using var response = await SendAsync(request, cancellationToken);
        var result = await DeserializeAsync<DownloadUrlResponse>(response, cancellationToken);
        return result.DownloadUrl;
    }

    public async Task DownloadFileAsync(
        string itemId,
        Stream destination,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var retryAttempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (retryAttempt > 0)
                {
                    if (!destination.CanSeek)
                        throw new IOException("下载连接中断，目标流不可定位，无法自动续重试。");
                    destination.Position = 0;
                    destination.SetLength(0);
                    progress?.Report(0);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(itemId)}/content");

                // Transfer I/O deliberately leaves the caller's synchronization context after the
                // response headers arrive. SendAsync itself retries temporary TLS/DNS/Graph failures.
                using var response = await SendAsync(request, cancellationToken, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var limiter = new TransferRateLimiter(DownloadBytesPerSecondLimit);
                var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
                long copied = 0;
                try
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                            .ConfigureAwait(false);
                        if (read <= 0)
                            break;

                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        await limiter.ThrottleAsync(read, cancellationToken).ConfigureAwait(false);
                        copied += read;
                        if (total is > 0)
                            progress?.Report((double)copied / total.Value);
                    }

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    progress?.Report(1.0);
                    return;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception ex) when (destination.CanSeek && IsTransientTransportFailure(ex, cancellationToken))
            {
                await DelayBeforeReadRetryAsync(null, retryAttempt++, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task UploadWithSessionAsync(
        string? parentItemId,
        string fileName,
        Stream source,
        long length,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!source.CanSeek)
            throw new InvalidOperationException("大文件上传要求输入流可定位（CanSeek=true）。");

        var escapedName = EscapePathSegment(fileName);
        var sessionUrl = parentItemId is null
            ? $"{AppConfig.GraphBaseUrl}/me/drive/root:/{escapedName}:/createUploadSession"
            : $"{AppConfig.GraphBaseUrl}/me/drive/items/{Uri.EscapeDataString(parentItemId)}:/{escapedName}:/createUploadSession";

        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["item"] = new Dictionary<string, object>
            {
                ["@microsoft.graph.conflictBehavior"] = "rename",
                ["name"] = fileName
            }
        }, _jsonOptions);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, sessionUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var createResponse = await SendAsync(createRequest, cancellationToken).ConfigureAwait(false);
        var session = await DeserializeAsync<UploadSessionResponse>(createResponse, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(session.UploadUrl))
            throw new InvalidOperationException("Microsoft Graph 未返回上传会话 URL。");

        using var uploadClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var buffer = new byte[UploadChunkSize];
        long offset = 0;
        source.Position = 0;

        while (offset < length)
        {
            var wanted = (int)Math.Min(buffer.Length, length - offset);
            var readTotal = 0;
            while (readTotal < wanted)
            {
                var read = await source.ReadAsync(buffer.AsMemory(readTotal, wanted - readTotal), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                readTotal += read;
            }

            if (readTotal == 0)
                throw new EndOfStreamException("上传过程中输入流提前结束。");

            var chunkOffset = offset;
            var chunkAttempt = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var chunkStream = new MemoryStream(buffer, 0, readTotal, writable: false, publiclyVisible: true);
                var chunkProgress = new InlineProgress<double>(p =>
                {
                    var sent = chunkOffset + p * readTotal;
                    progress?.Report(Math.Clamp(sent / length, 0, 1));
                });
                using var chunk = new ProgressStreamContent(
                    chunkStream,
                    UploadBytesPerSecondLimit,
                    chunkProgress,
                    "application/octet-stream");
                chunk.Headers.ContentLength = readTotal;
                chunk.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + readTotal - 1, length);

                try
                {
                    using var response = await uploadClient.PutAsync(session.UploadUrl, chunk, cancellationToken).ConfigureAwait(false);
                    if (IsTransientReadStatus(response.StatusCode))
                    {
                        await DelayBeforeReadRetryAsync(response, chunkAttempt++, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (IsTransientTransportFailure(ex, cancellationToken))
                {
                    await DelayBeforeReadRetryAsync(null, chunkAttempt++, cancellationToken).ConfigureAwait(false);
                }
            }

            offset += readTotal;
            progress?.Report((double)offset / length);
        }

        progress?.Report(1.0);
    }

    private static bool IsSizeOrderRequest(string? url, string? orderBy)
    {
        if (!string.IsNullOrWhiteSpace(orderBy) &&
            orderBy.TrimStart().StartsWith("size", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            return Uri.UnescapeDataString(url).Contains("orderby=size", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return url.Contains("orderby=size", StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        // Browsing/preview/metadata calls are reads. A temporary Wi-Fi, DNS, TLS, proxy, 429 or
        // Graph 5xx failure should never become a red English exception banner. Keep retrying with
        // bounded exponential backoff until the caller cancels because the user navigated away.
        // Mutating requests are intentionally *not* replayed here because POST/copy/create-folder
        // and stream uploads can have side effects that are unsafe to duplicate blindly.
        var retryableRead = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
        var originalRequestAvailable = true;
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? token;
            try
            {
                // Graph calls must never start an interactive login by themselves. Interactive
                // authentication is initiated only by the explicit Login command.
                token = await _authentication.GetAccessTokenAsync(interactive: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (retryableRead && IsTransientTransportFailure(ex, cancellationToken))
            {
                await DelayBeforeReadRetryAsync(null, attempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("未登录 Microsoft 账户。");

            HttpRequestMessage attemptRequest;
            var ownsAttemptRequest = false;
            if (originalRequestAvailable)
            {
                attemptRequest = request;
                originalRequestAvailable = false;
            }
            else
            {
                attemptRequest = CloneReadRequest(request);
                ownsAttemptRequest = true;
            }

            attemptRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                var response = await _httpClient.SendAsync(attemptRequest, completionOption, cancellationToken)
                    .ConfigureAwait(false);

                if (retryableRead && IsTransientReadStatus(response.StatusCode))
                {
                    try
                    {
                        await DelayBeforeReadRetryAsync(response, attempt++, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        response.Dispose();
                    }
                    continue;
                }

                return response;
            }
            catch (Exception ex) when (retryableRead && IsTransientTransportFailure(ex, cancellationToken))
            {
                await DelayBeforeReadRetryAsync(null, attempt++, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (ownsAttemptRequest)
                    attemptRequest.Dispose();
            }
        }
    }

    private static HttpRequestMessage CloneReadRequest(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };

        foreach (var header in source.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static bool IsTransientReadStatus(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is 408 or 425 or 429 || code >= 500;
    }

    private static bool IsTransientTransportFailure(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        if (ex is HttpRequestException http)
        {
            if (http.StatusCode is null)
                return true;
            return IsTransientReadStatus(http.StatusCode.Value);
        }

        if (ex is TaskCanceledException or TimeoutException)
            return true;

        return ex.InnerException is not null && IsTransientTransportFailure(ex.InnerException, cancellationToken);
    }

    private static async Task DelayBeforeReadRetryAsync(
        HttpResponseMessage? response,
        int attempt,
        CancellationToken cancellationToken)
    {
        TimeSpan delay;
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            delay = delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }
        else
        {
            // 0.45, 0.9, 1.8, 3.6, 7.2, then 8 seconds. Small jitter avoids every parallel
            // thumbnail/metadata request retrying on the exact same frame after Wi-Fi returns.
            var seconds = Math.Min(8.0, 0.45 * Math.Pow(2, Math.Min(attempt, 5)));
            delay = TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(40, 220));
        }

        if (delay < TimeSpan.FromMilliseconds(150))
            delay = TimeSpan.FromMilliseconds(150);
        if (delay > TimeSpan.FromSeconds(30))
            delay = TimeSpan.FromSeconds(30);

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Microsoft Graph 返回了空响应。");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Microsoft Graph 请求失败：{(int)response.StatusCode} {response.ReasonPhrase}\n{detail}",
            null,
            response.StatusCode);
    }

    private static string EscapePathSegment(string name) => Uri.EscapeDataString(name);
}
