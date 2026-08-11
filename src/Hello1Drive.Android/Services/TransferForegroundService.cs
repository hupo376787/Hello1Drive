namespace Hello1Drive.Android.Services;

[global::Android.App.Service(
    Exported = false,
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

    public override global::Android.OS.IBinder? OnBind(global::Android.Content.Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureNotificationChannel();
    }

    public override global::Android.App.StartCommandResult OnStartCommand(
        global::Android.Content.Intent? intent,
        global::Android.App.StartCommandFlags flags,
        int startId)
    {
        var activeCount = intent?.GetIntExtra(ExtraActiveCount, 0) ?? 0;
        var runningCount = intent?.GetIntExtra(ExtraRunningCount, 0) ?? 0;
        if (activeCount <= 0)
        {
            StopForegroundCompat();
            StopSelf(startId);
            return global::Android.App.StartCommandResult.NotSticky;
        }

        var uploadCount = intent?.GetIntExtra(ExtraUploadCount, 0) ?? 0;
        var downloadCount = intent?.GetIntExtra(ExtraDownloadCount, 0) ?? 0;
        var cacheCount = intent?.GetIntExtra(ExtraCacheCount, 0) ?? 0;
        var notification = BuildNotification(activeCount, runningCount, uploadCount, downloadCount, cacheCount);

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

    public override void OnTimeout(int startId, global::Android.Content.PM.ForegroundService fgsType)
    {
        // Android 15+ limits dataSync FGS time. Stop promptly when the platform asks us to avoid
        // RemoteServiceException/ANR. A later foreground visit can resume persisted pending work.
        StopForegroundCompat();
        StopSelf(startId);
    }

    public override void OnDestroy()
    {
        AndroidTransferBackgroundService.NotifyServiceStopped();
        StopForegroundCompat();
        base.OnDestroy();
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
