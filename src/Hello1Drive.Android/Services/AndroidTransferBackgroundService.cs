using System.Runtime.Versioning;
using Hello1Drive.Services;

namespace Hello1Drive.Android.Services;

/// <summary>
/// Bridges the cross-platform transfer queue to an Android dataSync foreground service.
/// The transfer workers themselves remain in the existing Core queue; the foreground service
/// keeps the process eligible to continue user-visible upload/download work after the Activity
/// moves to the background.
/// </summary>
public sealed class AndroidTransferBackgroundService : ITransferBackgroundService
{
    private const int NotificationPermissionRequestCode = 4817;
    private static readonly object ServiceSync = new();
    private static bool _notificationPermissionRequestedThisProcess;
    private static bool _serviceStarted;

    public void Update(TransferBackgroundState state)
    {
        var context = global::Android.App.Application.Context;
        var intent = new global::Android.Content.Intent(context, typeof(TransferForegroundService));

        if (!state.HasActiveTransfers)
        {
            context.StopService(intent);
            lock (ServiceSync)
                _serviceStarted = false;
            return;
        }

        TryRequestNotificationPermission();

        intent.PutExtra(TransferForegroundService.ExtraActiveCount, state.ActiveCount);
        intent.PutExtra(TransferForegroundService.ExtraRunningCount, state.RunningCount);
        intent.PutExtra(TransferForegroundService.ExtraUploadCount, state.UploadCount);
        intent.PutExtra(TransferForegroundService.ExtraDownloadCount, state.DownloadCount);
        intent.PutExtra(TransferForegroundService.ExtraCacheCount, state.CacheCount);

        // Start the FGS while the user-visible transfer is first queued. Once it already exists,
        // regular StartService calls only update its notification; this avoids trying to create a
        // new foreground service from the background between queued files.
        lock (ServiceSync)
        {
            if (_serviceStarted)
            {
                context.StartService(intent);
                return;
            }

            // StartForegroundService was introduced in Android 8.0 (API 26). Older supported
            // Android versions start the service normally and the service immediately promotes
            // itself with StartForeground from OnStartCommand.
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(intent);
            else
                context.StartService(intent);

            _serviceStarted = true;
        }
    }

    internal static void NotifyServiceStopped()
    {
        lock (ServiceSync)
            _serviceStarted = false;
    }

    private static void TryRequestNotificationPermission()
    {
        // POST_NOTIFICATIONS only exists from Android 13 (API 33). Keep every reference to the
        // API-33-only manifest constant inside an API-33 platform context so CA1416 also understands
        // the deferred RunOnUiThread callback.
        if (_notificationPermissionRequestedThisProcess || !OperatingSystem.IsAndroidVersionAtLeast(33))
            return;

        var activity = global::Hello1Drive.Android.MainActivity.Instance;
        if (activity is null)
            return;

        _notificationPermissionRequestedThisProcess = true;
        RequestNotificationPermissionApi33(activity);
    }

    [SupportedOSPlatform("android33.0")]
    private static void RequestNotificationPermissionApi33(global::Hello1Drive.Android.MainActivity activity)
    {
        if (activity.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) ==
            global::Android.Content.PM.Permission.Granted)
        {
            return;
        }

        activity.RunOnUiThread(() => activity.RequestPermissions(
            [global::Android.Manifest.Permission.PostNotifications],
            NotificationPermissionRequestCode));
    }
}
