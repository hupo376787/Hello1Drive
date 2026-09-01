using Hello1Drive.Services;

namespace Hello1Drive.Android.Services;

[global::Android.App.Service(
    Exported = false,
    StopWithTask = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class TransferForegroundService : global::Android.App.Service
{
    internal const string ExtraActiveCount = "activeCount";
    internal const string ExtraRunningCount = "runningCount";
    internal const string ExtraUploadCount = "uploadCount";
    internal const string ExtraDownloadCount = "downloadCount";
    internal const string ExtraCacheCount = "cacheCount";

    private const string ChannelId = "hello1drive_transfers";
    private const int NotificationId = 14101;
    private static readonly object InstanceSync = new();
    private static TransferForegroundService? _instance;

    private global::Android.OS.PowerManager.WakeLock? _transferWakeLock;

    public override global::Android.OS.IBinder? OnBind(global::Android.Content.Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureNotificationChannel();
        lock (InstanceSync)
            _instance = this;
    }

    /// <summary>
    /// Updates an already-running foreground service without calling Context.StartService again.
    /// Android restricts background service starts; once the dataSync FGS exists, updating its
    /// notification directly is both cheaper and safe while the Activity is backgrounded.
    /// </summary>
    internal static bool TryUpdate(TransferBackgroundState state)
    {
        TransferForegroundService? instance;
        lock (InstanceSync)
            instance = _instance;

        if (instance is null)
            return false;

        try
        {
            instance.ApplyState(state);
            return true;
        }
        catch
        {
            // The service may be in the middle of OnDestroy. The bridge will keep the latest
            // queue state and can start a new FGS on the next foreground-visible transfer event.
            return false;
        }
    }

    public override global::Android.App.StartCommandResult OnStartCommand(
        global::Android.Content.Intent? intent,
        global::Android.App.StartCommandFlags flags,
        int startId)
    {
        var state = new TransferBackgroundState(
            ActiveCount: intent?.GetIntExtra(ExtraActiveCount, 0) ?? 0,
            RunningCount: intent?.GetIntExtra(ExtraRunningCount, 0) ?? 0,
            UploadCount: intent?.GetIntExtra(ExtraUploadCount, 0) ?? 0,
            DownloadCount: intent?.GetIntExtra(ExtraDownloadCount, 0) ?? 0,
            CacheCount: intent?.GetIntExtra(ExtraCacheCount, 0) ?? 0);

        // Waiting -> Running can happen immediately after StartForegroundService returns, before
        // Android has constructed this Service. Consume the newest state so the first foreground
        // notification and wake-lock decision never get stuck on the stale "waiting" snapshot.
        state = AndroidTransferBackgroundService.GetLatestState(state);

        if (!state.HasActiveTransfers)
        {
            ReleaseTransferWakeLock();
            StopForegroundCompat();
            StopSelf(startId);
            return global::Android.App.StartCommandResult.NotSticky;
        }

        EnsureTransferWakeLock();
        var notification = BuildNotification(state.ActiveCount, state.RunningCount, state.UploadCount, state.DownloadCount, state.CacheCount);

        // The overload that declares a foreground-service type was introduced in Android 10
        // (API 29). Android 9 and earlier use the original two-argument overload.
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(
                NotificationId,
                notification,
                global::Android.Content.PM.ForegroundService.TypeDataSync);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }

        // Do not synthesize a transfer after process death. The existing transfer persistence
        // layer will safely reconstruct pending work the next time the app UI starts.
        return global::Android.App.StartCommandResult.NotSticky;
    }

    private void ApplyState(TransferBackgroundState state)
    {
        if (!state.HasActiveTransfers)
        {
            ReleaseTransferWakeLock();
            StopForegroundCompat();
            StopSelf();
            return;
        }

        EnsureTransferWakeLock();
        var manager = GetSystemService(global::Android.Content.Context.NotificationService) as global::Android.App.NotificationManager;
        manager?.Notify(
            NotificationId,
            BuildNotification(state.ActiveCount, state.RunningCount, state.UploadCount, state.DownloadCount, state.CacheCount));
    }

    public override void OnTimeout(int startId, global::Android.Content.PM.ForegroundService fgsType)
    {
        // Android 15+ limits dataSync FGS time. Stop promptly when the platform asks us to avoid
        // RemoteServiceException/ANR. Pending transfer metadata remains persisted for a later retry.
        ReleaseTransferWakeLock();
        StopForegroundCompat();
        StopSelf(startId);
    }

    public override void OnDestroy()
    {
        lock (InstanceSync)
        {
            if (ReferenceEquals(_instance, this))
                _instance = null;
        }

        AndroidTransferBackgroundService.NotifyServiceStopped();
        ReleaseTransferWakeLock();
        StopForegroundCompat();
        base.OnDestroy();
    }

    private void EnsureTransferWakeLock()
    {
        if (_transferWakeLock?.IsHeld == true)
            return;

        var powerManager = GetSystemService(global::Android.Content.Context.PowerService) as global::Android.OS.PowerManager;
        if (powerManager is null)
            return;

        _transferWakeLock ??= powerManager.NewWakeLock(
            global::Android.OS.WakeLockFlags.Partial,
            $"{PackageName}:Hello1DriveTransfer");
        _transferWakeLock.SetReferenceCounted(false);
        if (!_transferWakeLock.IsHeld)
            _transferWakeLock.Acquire();
    }

    private void ReleaseTransferWakeLock()
    {
        var wakeLock = _transferWakeLock;
        _transferWakeLock = null;
        if (wakeLock is null)
            return;

        try
        {
            if (wakeLock.IsHeld)
                wakeLock.Release();
        }
        catch
        {
            // The OS may have already released it while tearing down the service.
        }
        finally
        {
            wakeLock.Dispose();
        }
    }

    private void StopForegroundCompat()
    {
        // StopForeground(StopForegroundFlags) is available from Android 7.0 (API 24).
        // Android 6.0 (API 23), which Hello1Drive still supports, needs the legacy bool overload.
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
            StopForeground(global::Android.App.StopForegroundFlags.Remove);
        else
            StopForeground(true);
    }

    private void EnsureNotificationChannel()
    {
        // Notification channels were introduced in Android 8.0 (API 26).
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return;

        var manager = GetSystemService(global::Android.Content.Context.NotificationService) as global::Android.App.NotificationManager;
        if (manager is null)
            return;

        var channel = new global::Android.App.NotificationChannel(
            ChannelId,
            "文件传输",
            global::Android.App.NotificationImportance.Low);
        channel.SetSound(null, null);
        channel.EnableVibration(false);
        manager.CreateNotificationChannel(channel);
    }

    private global::Android.App.Notification BuildNotification(
        int activeCount,
        int runningCount,
        int uploadCount,
        int downloadCount,
        int cacheCount)
    {
        var launchIntent = new global::Android.Content.Intent(this, typeof(global::Hello1Drive.Android.MainActivity));
        launchIntent.SetFlags(global::Android.Content.ActivityFlags.SingleTop | global::Android.Content.ActivityFlags.ClearTop);
        var pendingIntent = global::Android.App.PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            global::Android.App.PendingIntentFlags.UpdateCurrent | global::Android.App.PendingIntentFlags.Immutable);

        global::Android.App.Notification.Builder builder;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            builder = new global::Android.App.Notification.Builder(this, ChannelId);
        else
            builder = new global::Android.App.Notification.Builder(this);

        builder
            .SetSmallIcon(global::Hello1Drive.Android.Resource.Mipmap.icon)
            .SetContentTitle("Hello1Drive 正在传输")
            .SetContentText(BuildSummary(activeCount, runningCount, uploadCount, downloadCount, cacheCount))
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetCategory(global::Android.App.Notification.CategoryProgress)
            .SetProgress(0, 0, true);

        return builder.Build();
    }

    private static string BuildSummary(int activeCount, int runningCount, int uploadCount, int downloadCount, int cacheCount)
    {
        var parts = new List<string>(3);
        if (uploadCount > 0)
            parts.Add($"上传 {uploadCount}");
        if (downloadCount > 0)
            parts.Add($"下载 {downloadCount}");
        if (cacheCount > 0)
            parts.Add($"缓存 {cacheCount}");

        if (runningCount == 0)
            return $"等待传输 · 共 {activeCount} 个任务";

        return parts.Count == 0
            ? $"正在处理 {runningCount} / {activeCount} 个任务"
            : $"{string.Join(" · ", parts)} · {runningCount}/{activeCount} 进行中";
    }
}
