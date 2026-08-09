using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Hello1Drive.Models;

public enum TransferDirection
{
    Upload,
    Download,
    Cache
}

public enum TransferState
{
    Waiting,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum TransferResumeKind
{
    None,
    UploadFile,
    DownloadFile,
    DownloadToFolder,
    CacheFile
}

public sealed class TransferResumeInfo
{
    // Prevent a restored transfer from accidentally running against a different
    // Microsoft account after sign-out/sign-in between app launches.
    public string? AccountId { get; set; }
    public TransferResumeKind Kind { get; set; }
    public string? OneDriveItemId { get; set; }
    public string? TargetFolderId { get; set; }
    public string? StorageBookmark { get; set; }
    public string[] RelativeFolderSegments { get; set; } = [];
}

public partial class TransferItemModel : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string FileName { get; init; } = string.Empty;
    public TransferDirection Direction { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    // Resume metadata is persisted separately from the runtime callback. It allows a
    // pending upload/download/cache job to be reconstructed after the next launch.
    public TransferResumeInfo? ResumeInfo { get; set; }

    // Runtime-only retry callback. It intentionally isn't serialized and is owned by
    // the current application session.
    [JsonIgnore]
    public Func<Task>? RetryAction { get; set; }

    // Runtime flag used once during startup so a restored pending row is not confused
    // with a newly queued transfer from the current session.
    [JsonIgnore]
    public bool IsRestoredFromDisk { get; set; }

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private TransferState state = TransferState.Waiting;

    [ObservableProperty]
    private string message = "等待中";

    public string DirectionText => Direction switch
    {
        TransferDirection.Upload => "上传",
        TransferDirection.Download => "下载",
        TransferDirection.Cache => "缓存",
        _ => string.Empty
    };
    public bool CanRetry => State == TransferState.Failed && RetryAction is not null;
    public string StateText => State switch
    {
        TransferState.Waiting => Direction switch
        {
            TransferDirection.Upload => "待上传",
            TransferDirection.Download => "待下载",
            TransferDirection.Cache => "等待缓存",
            _ => "等待中"
        },
        TransferState.Running => Direction switch
        {
            TransferDirection.Upload => "正在上传",
            TransferDirection.Download => "正在下载",
            TransferDirection.Cache => "正在缓存",
            _ => "进行中"
        },
        TransferState.Completed => Direction switch
        {
            TransferDirection.Upload => "已上传",
            TransferDirection.Download => "已下载",
            TransferDirection.Cache => "已缓存",
            _ => "已完成"
        },
        TransferState.Failed => Direction switch
        {
            TransferDirection.Upload => "上传错误",
            TransferDirection.Download => "下载错误",
            TransferDirection.Cache => "缓存错误",
            _ => "错误"
        },
        TransferState.Cancelled => "已取消",
        _ => string.Empty
    };
    public string ProgressText => $"{Progress:P0}";
    public string StateProgressText => State == TransferState.Running
        ? $"{StateText} {ProgressText}"
        : StateText;

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(StateProgressText));
    }

    partial void OnStateChanged(TransferState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateProgressText));
        OnPropertyChanged(nameof(CanRetry));
    }
}
