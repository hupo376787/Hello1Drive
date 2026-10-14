using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;
using Hello1Drive.Views;

AppServices.Configure(new NoAuthentication());
AppBuilder.Configure<Hello1Drive.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
Run("non-seekable small file has Content-Length and preserves source ownership", () => Network(CheckSmallAsync));
Run("Android pipe with zero reported length is staged and cleaned up", () => Network(CheckAndroidPipeAsync));
Run("non-seekable large file uses aligned resumable fragments", () => Network(() => CheckSessionAsync("normal")));
Run("lost fragment response resumes from server offset", () => Network(() => CheckSessionAsync("lost")));
Run("socket closed resumes the same session", () => Network(() => CheckSessionAsync("socket")));
Run("software caused connection abort resumes the same session", () => Network(() => CheckSessionAsync("abort")));
Run("unwrapped Java socket exception resumes the same session", () => Network(() => CheckSessionAsync("java-socket")));
Run("unwrapped Java I/O abort resumes the same session", () => Network(() => CheckSessionAsync("java-io")));
Run("native timeout resumes the same session", () => Network(() => CheckSessionAsync("timeout")));
Run("progress-watchdog cancellation resumes the existing session", () => Network(() => CheckSessionAsync("watchdog")));
Run("HTTP 416 resumes from server offset", () => Network(() => CheckSessionAsync("416")));
Run("lost final response verifies completion without creating another session", () => Network(() => CheckSessionAsync("final-lost")));
Run("202 Accepted is not mistaken for completed upload", () => Network(() => CheckSessionAsync("final-202")));
Run("nonzero stream position uploads only remaining bytes", () => Network(() => CheckSessionAsync("position")));
Run("premature EOF cannot send a malformed last fragment", () => Network(CheckPrematureEndAsync));
Run("quota rejection remains a real upload failure", () => Network(CheckQuotaAsync));
Run("user cancellation is not retried and staging is cleaned up", () => Network(CheckCancellationAsync));
Run("explicit slow upload limit is not mistaken for a stalled socket", () => Network(CheckSlowLimitAsync));
Run("100% request serialization remains 99% until server confirmation", CheckUiProgress);
Run("refresh failure cannot overwrite confirmed upload", CheckRefreshFailure);
Run("Android stream-close failure cannot overwrite confirmed upload", CheckCloseFailure);
Run("native abort keeps transfer/background state active through retry", CheckBackgroundRetry);
Console.WriteLine("PASS: all upload regressions");

static void Run(string name, Action action) { action(); Console.WriteLine($"PASS: {name}"); }
static void Network(Func<Task> check) => Task.Run(check).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
{
    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
};
static object Pending(long offset) => new { nextExpectedRanges = new[] { $"{offset}-" } };
static object Completed(long size) => new { id = "uploaded-id", name = "upload.bin", size, file = new { } };
static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
    new(new ScriptedHandler(send)) { Timeout = Timeout.InfiniteTimeSpan };
static ResilientOneDriveService Service(HttpClient client) => new(new OneDriveService(new TokenAuthentication(), client, client));

static async Task CheckSmallAsync()
{
    using var input = new PickerStream(new byte[32768]);
    var before = Directory.GetFiles(Path.GetTempPath(), "hello1drive-upload-*.tmp").ToHashSet();
    using var client = Client(async (request, token) =>
    {
        Assert(request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.EndsWith("/content"), "small file uses content PUT");
        Assert(request.Content!.Headers.ContentLength == 32768, "known Content-Length before serialization");
        Assert((await request.Content.ReadAsByteArrayAsync(token)).Length == 32768, "complete small body");
        return Json(HttpStatusCode.Created, Completed(32768));
    });
    await Service(client).UploadFileAsync(null, "中文.bin", input);
    Assert(!input.IsDisposed, "caller still owns source");
    Assert(Directory.GetFiles(Path.GetTempPath(), "hello1drive-upload-*.tmp").All(before.Contains), "staged file removed");
}

static async Task CheckAndroidPipeAsync()
{
    using var input = new PickerStream(new byte[8192], advertisesSeek: true, reportsZeroLength: true);
    var type = typeof(ResilientOneDriveService).Assembly.GetType("Hello1Drive.Services.PreparedUploadSource")!;
    var task = (Task)type.GetMethod("PrepareAsync")!.Invoke(null, [input, true, CancellationToken.None])!;
    await task;
    var prepared = task.GetType().GetProperty("Result")!.GetValue(task)!;
    var stream = (Stream)type.GetProperty("Stream")!.GetValue(prepared)!;
    var path = ((FileStream)stream).Name;
    Assert(stream.CanSeek && stream.Length == 8192 && stream.Position == 0, "Android pipe becomes a complete seekable file");
    await ((IAsyncDisposable)prepared).DisposeAsync();
    Assert(!File.Exists(path) && !input.IsDisposed, "only staging file disposed/deleted");
}

static async Task CheckSessionAsync(string scenario)
{
    const int chunkSize = 5 * 1024 * 1024;
    const int size = 12 * 1024 * 1024;
    var bytes = new byte[size];
    for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
    using Stream input = scenario == "position"
        ? new MemoryStream(new byte[128].Concat(bytes).ToArray()) : new PickerStream(bytes);
    if (scenario == "position") input.Position = 128;
    var sessionCreates = 0;
    var offsets = new List<long>();
    var recovered = false;
    var statusReads = 0;
    var acknowledged = false;
    using var client = Client(async (request, token) =>
    {
        if (request.Method == HttpMethod.Post)
        {
            sessionCreates++;
            return Json(HttpStatusCode.OK, new { uploadUrl = "https://upload.example/session" });
        }
        if (request.RequestUri!.Host != "upload.example")
        {
            Assert(scenario == "final-lost" && request.Method == HttpMethod.Get, "only final-loss verification uses Graph listing");
            Assert(request.RequestUri.AbsolutePath.EndsWith("/children"), "verification stops on newest page without fetching all continuation pages");
            Assert(Uri.UnescapeDataString(request.RequestUri.Query).Contains("lastModifiedDateTime desc"), "completion verification requests newest files first");
            return Json(HttpStatusCode.OK, new Dictionary<string, object> {
                ["value"] = new[] { new { id = "uploaded-id", name = "upload.bin", size, file = new { }, lastModifiedDateTime = DateTimeOffset.UtcNow } },
                ["@odata.nextLink"] = "https://graph.microsoft.com/should-not-follow"
            });
        }
        Assert(request.Headers.Authorization is null, "preauthenticated upload URL must not carry Graph bearer token");
        if (request.Method == HttpMethod.Get)
        {
            statusReads++;
            if (scenario == "final-lost") return Json(HttpStatusCode.NotFound, new { error = "session finished" });
            recovered = true;
            return Json(HttpStatusCode.OK, Pending(2L * chunkSize));
        }
        var range = request.Content!.Headers.ContentRange!;
        Assert(range.Length == size, "range uses remaining length, not full underlying stream length");
        var start = range.From!.Value;
        var body = await request.Content.ReadAsByteArrayAsync(token);
        offsets.Add(start);
        Assert(body.Length == Math.Min(chunkSize, size - start), "exact chunk length");
        Assert(body.AsSpan().SequenceEqual(bytes.AsSpan((int)start, body.Length)), "correct source bytes on every seek/retry");
        Assert(range.To == start + body.Length - 1 && request.Content.Headers.ContentLength == body.Length, "range/framing matches body");
        if (start == chunkSize && !recovered && scenario is "lost" or "416" or "socket" or "abort" or "java-socket" or "java-io" or "timeout" or "watchdog")
        {
            if (scenario == "lost") throw new HttpRequestException("simulated missing acknowledgement");
            if (scenario == "socket") throw new IOException("Socket closed");
            if (scenario == "abort") throw new IOException("Software caused connection abort");
            if (scenario == "java-socket") throw new Java.Net.SocketException("Socket closed");
            if (scenario == "java-io") throw new Java.IO.IOException("Software caused connection abort");
            if (scenario == "timeout") throw new OperationCanceledException("simulated native timeout");
            if (scenario == "watchdog") await Task.Delay(TimeSpan.FromSeconds(5), token);
            return Json(HttpStatusCode.RequestedRangeNotSatisfiable, new { error = "already received" });
        }
        if (start + body.Length == size)
        {
            if (scenario == "final-lost") throw new HttpRequestException("simulated missing final acknowledgement");
            if (scenario == "final-202" && !recovered)
            {
                recovered = true;
                return Json(HttpStatusCode.Accepted, Pending(0));
            }
            acknowledged = true;
            return Json(HttpStatusCode.Created, Completed(size));
        }
        return Json(HttpStatusCode.Accepted, Pending(start + body.Length));
    });
    var service = scenario == "watchdog"
        ? new ResilientOneDriveService(new OneDriveService(new TokenAuthentication(), client, client), TimeSpan.FromMilliseconds(300))
        : Service(client);
    await service.UploadFileAsync(null, "upload.bin", input);
    Assert(sessionCreates == 1, "no duplicate full-upload session");
    Assert(acknowledged || scenario == "final-lost", "requires final acknowledgement or verified remote item");
    if (scenario is "lost" or "416" or "socket" or "abort" or "java-socket" or "java-io" or "timeout" or "watchdog")
        Assert(statusReads == 1 && offsets.Count == 3, "accepted fragment was not blindly replayed");
    if (scenario == "final-202") Assert(offsets.Count == 6, "missing ranges uploaded before declaring success");
}

static async Task CheckPrematureEndAsync()
{
    using var input = new DeclaredLengthStream(new byte[11 * 1024 * 1024], 12 * 1024 * 1024);
    var fragments = 0;
    using var client = Client(async (request, token) =>
    {
        if (request.Method == HttpMethod.Post) return Json(HttpStatusCode.OK, new { uploadUrl = "https://upload.example/session" });
        var bytes = await request.Content!.ReadAsByteArrayAsync(token);
        Assert(bytes.Length == 5 * 1024 * 1024, "only complete aligned fragments sent");
        fragments++;
        return Json(HttpStatusCode.Accepted, Pending(fragments * 5L * 1024 * 1024));
    });
    try { await Service(client).UploadFileAsync(null, "upload.bin", input); throw new InvalidOperationException("expected EOF error"); }
    catch (EndOfStreamException) { }
    Assert(fragments == 2, "partial invalid fragment was never sent");
}

static async Task CheckQuotaAsync()
{
    var requests = 0;
    using var input = new MemoryStream(new byte[16]);
    using var client = Client(async (request, token) =>
    {
        requests++;
        await request.Content!.ReadAsByteArrayAsync(token);
        return Json(HttpStatusCode.InsufficientStorage, new { error = new { code = "quotaLimitReached" } });
    });
    try { await Service(client).UploadFileAsync(null, "upload.bin", input); throw new InvalidOperationException("expected quota error"); }
    catch (HttpRequestException ex) { Assert(ex.StatusCode == HttpStatusCode.InsufficientStorage, "preserves permanent error status"); }
    Assert(requests == 1, "permanent quota rejection not retried/masked");
}

static async Task CheckCancellationAsync()
{
    var before = Directory.GetFiles(Path.GetTempPath(), "hello1drive-upload-*.tmp").ToHashSet();
    using var input = new PickerStream(new byte[4096]);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    using var client = Client((_, _) => throw new InvalidOperationException("cancelled transfer must not send HTTP"));
    try { await Service(client).UploadFileAsync(null, "upload.bin", input, cancellationToken: cancellation.Token); throw new InvalidOperationException("expected cancellation"); }
    catch (OperationCanceledException) { }
    Assert(!input.IsDisposed && Directory.GetFiles(Path.GetTempPath(), "hello1drive-upload-*.tmp").All(before.Contains), "cancellation deletes staging only");
}

static async Task CheckSlowLimitAsync()
{
    var inner = DispatchProxy.Create<IOneDriveService, UploadServiceStub>();
    var attempts = 0;
    ((UploadServiceStub)(object)inner).UploadCancellable = async (progress, token) =>
    {
        attempts++;
        progress!.Report(0.1);
        await Task.Delay(200, token);
        progress.Report(1);
    };
    var service = new ResilientOneDriveService(inner, TimeSpan.FromMilliseconds(50)) { UploadBytesPerSecondLimit = 1024 };
    using var source = new MemoryStream(new byte[1024 * 1024]);
    await service.UploadFileAsync(null, "slow.bin", source);
    Assert(attempts == 1, "rate-aware watchdog must allow the deliberate gap between progress reports");
}

static (MainViewModel Vm, UploadServiceStub Stub) ViewModel()
{
    var service = DispatchProxy.Create<IOneDriveService, UploadServiceStub>();
    var vm = new MainViewModel(service, AppServices.Authentication, AppServices.Settings, AppServices.FileCache,
        AppServices.ThumbnailCache, AppServices.TransferPersistence, AppServices.StartupSnapshot, AppServices.LocalDriveIndex);
    return (vm, (UploadServiceStub)(object)service);
}

static void CheckUiProgress()
{
    var (vm, stub) = ViewModel();
    var transfer = vm.RegisterTransfer("upload.bin", TransferDirection.Upload);
    stub.Upload = progress =>
    {
        progress!.Report(0.99);
        Dispatcher.UIThread.RunJobs();
        progress.Report(1);
        Dispatcher.UIThread.RunJobs();
        Assert(transfer.State == TransferState.Running && transfer.Progress == 0.99, "last-byte progress still awaiting server");
        Assert(transfer.Message == "正在确认上传结果", "confirmation phase visible even if rounded percent didn't change");
        return Task.CompletedTask;
    };
    using var source = new MemoryStream();
    vm.UploadFileAsync(null, "upload.bin", source, false, transfer).GetAwaiter().GetResult();
    Assert(transfer.State == TransferState.Completed && transfer.Progress == 1, "acknowledged success reaches 100%");
}

static void CheckRefreshFailure()
{
    var (vm, _) = ViewModel();
    var transfer = vm.RegisterTransfer("upload.bin", TransferDirection.Upload);
    vm.FolderNavigating += (_, _) => throw new IOException("simulated refresh failure");
    using var source = new MemoryStream();
    vm.UploadFileAsync(null, "upload.bin", source, true, transfer).GetAwaiter().GetResult();
    Assert(transfer.State == TransferState.Completed && transfer.Message == "已上传", "follow-up failure preserves uploaded row");
    Assert(vm.ErrorMessage!.StartsWith("文件已上传"), "refresh error distinguished from upload failure");
}

static void CheckCloseFailure()
{
    var (vm, _) = ViewModel();
    var transfer = vm.RegisterTransfer("upload.bin", TransferDirection.Upload);
    transfer.RetryAction = () => Task.CompletedTask;
    var file = DispatchProxy.Create<IStorageFile, CloseFailureFile>();
    var method = typeof(MainView).GetMethod("UploadStorageFileAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
    ((Task)method.Invoke(null, [vm, null, file, transfer, false])!).GetAwaiter().GetResult();
    Assert(transfer.State == TransferState.Completed && transfer.Progress == 1 && transfer.RetryAction is null, "stream close error cannot enable duplicate upload retry");
    Assert(vm.ErrorMessage!.StartsWith("文件已上传"), "local cleanup error distinguished from remote upload failure");
}

static void CheckBackgroundRetry()
{
    var recorder = new BackgroundRecorder();
    var inner = DispatchProxy.Create<IOneDriveService, UploadServiceStub>();
    var attempts = 0;
    ((UploadServiceStub)(object)inner).Upload = progress =>
    {
        Assert(recorder.States.Last().HasActiveTransfers, "keep-alive retained while retrying");
        progress!.Report(0.5);
        if (++attempts == 1) throw new Java.Net.SocketException("Software caused connection abort");
        return Task.CompletedTask;
    };
    var vm = new MainViewModel(new ResilientOneDriveService(inner), AppServices.Authentication, AppServices.Settings,
        AppServices.FileCache, AppServices.ThumbnailCache, AppServices.TransferPersistence, AppServices.StartupSnapshot,
        AppServices.LocalDriveIndex, transferBackgroundService: recorder);
    var otherActive = vm.ActiveTransferCount;
    var transfer = vm.RegisterTransfer("upload.bin", TransferDirection.Upload);
    var failed = false;
    transfer.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TransferItemModel.State) && transfer.State == TransferState.Failed) failed = true; };
    using var input = new MemoryStream(new byte[32]);
    var task = vm.UploadFileAsync(null, "upload.bin", input, false, transfer);
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    Assert(task.IsCompleted, "background retry finishes");
    task.GetAwaiter().GetResult();
    Assert(attempts == 2 && !failed && transfer.State == TransferState.Completed, "native abort never marks row Failed");
    Assert(recorder.States.Last().ActiveCount == otherActive, "completed job removed from keep-alive without stopping other pending jobs");
}

public sealed class BackgroundRecorder : ITransferBackgroundService
{
    public List<TransferBackgroundState> States { get; } = [];
    public void Update(TransferBackgroundState state) => States.Add(state);
}

public sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}

public class UploadServiceStub : DispatchProxy
{
    public Func<IProgress<double>?, Task> Upload { get; set; } = _ => Task.CompletedTask;
    public Func<IProgress<double>?, CancellationToken, Task>? UploadCancellable { get; set; }
    private long? _uploadLimit;
    private object? SetUploadLimit(object? value) { _uploadLimit = (long?)value; return null; }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "UploadFileAsync" => UploadCancellable?.Invoke((IProgress<double>?)args![3], (CancellationToken)args[4]!) ?? Upload((IProgress<double>?)args![3]),
        "set_UploadBytesPerSecondLimit" => SetUploadLimit(args![0]),
        "set_DownloadBytesPerSecondLimit" => null,
        "get_UploadBytesPerSecondLimit" => _uploadLimit,
        "get_DownloadBytesPerSecondLimit" => null,
        _ => throw new NotSupportedException(method.Name)
    };
}

public sealed class PickerStream(byte[] bytes, bool advertisesSeek = false, bool reportsZeroLength = false) : Stream
{
    private readonly MemoryStream _inner = new(bytes);
    public bool IsDisposed { get; private set; }
    public override bool CanRead => !IsDisposed;
    public override bool CanSeek => advertisesSeek;
    public override bool CanWrite => false;
    public override long Length => reportsZeroLength ? 0 : throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { IsDisposed = true; _inner.Dispose(); } base.Dispose(disposing); }
}

public sealed class DeclaredLengthStream(byte[] bytes, long length) : MemoryStream(bytes)
{
    public override long Length => length;
}

public sealed class CloseFailureStream : MemoryStream
{
    public override ValueTask DisposeAsync() { Dispose(); return ValueTask.FromException(new IOException("simulated Android close failure")); }
}

public class CloseFailureFile : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_Name" => "upload.bin",
        "get_Path" => new Uri("content://test/upload.bin"),
        "get_CanBookmark" => false,
        "OpenReadAsync" => Task.FromResult<Stream>(new CloseFailureStream()),
        "SaveBookmarkAsync" => Task.FromResult<string?>(null),
        "Dispose" => null,
        _ => throw new NotSupportedException(method.Name)
    };
}

public sealed class TokenAuthentication : IAuthenticationService
{
    public Task<string?> GetAccessTokenAsync(bool interactive, CancellationToken cancellationToken = default) => Task.FromResult<string?>("test-token");
    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public bool TryHandleProtocolActivation(Uri uri) => false;
}

public sealed class NoAuthentication : IAuthenticationService
{
    public Task<string?> GetAccessTokenAsync(bool interactive, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public bool TryHandleProtocolActivation(Uri uri) => false;
}
