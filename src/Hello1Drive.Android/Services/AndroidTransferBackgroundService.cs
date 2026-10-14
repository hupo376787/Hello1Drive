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
    private static bool _notificationPermissionPending;
    private static bool _batteryPermissionRequestedThisProcess;
    private static bool _serviceStarted;
    private static bool _hasLatestState;
    private static TransferBackgroundState _latestState;

    public void Update(TransferBackgroundState state)
    {
        lock (ServiceSync)
        {
            _latestState = state;
            _hasLatestState = true;
        }

        var context = global::Android.App.Application.Context;
        var intent = new global::Android.Content.Intent(context, typeof(TransferForegroundService));

        if (!state.HasActiveTransfers)
        {
            // If the service instance is alive, let it release the partial wake lock before
            // StopService tears it down. This also updates state synchronously when the final
            // transfer completes while the Activity is already backgrounded.
            TransferForegroundService.TryUpdate(state);
            context.StopService(intent);
            lock (ServiceSync)
                _serviceStarted = false;
            return;
        }

        if (!TryRequestNotificationPermission())
            TryRequestBatteryOptimizationExemption();

        // Once the foreground service is running, never call Context.StartService just to refresh
        // counts. Android applies background-service start restrictions even when an FGS already
        // exists; update the notification/wake-lock state through the live service instance instead.
        if (TransferForegroundService.TryUpdate(state))
        {
            lock (ServiceSync)
                _serviceStarted = true;
            return;
        }

        intent.PutExtra(TransferForegroundService.ExtraActiveCount, state.ActiveCount);
        intent.PutExtra(TransferForegroundService.ExtraRunningCount, state.RunningCount);
        intent.PutExtra(TransferForegroundService.ExtraUploadCount, state.UploadCount);
        intent.PutExtra(TransferForegroundService.ExtraDownloadCount, state.DownloadCount);
        intent.PutExtra(TransferForegroundService.ExtraCacheCount, state.CacheCount);

        // Start the FGS while the user-visible transfer is first queued. If a start is already in
        // flight but OnCreate has not published the service instance yet, do not issue a second
        // background start; OnStartCommand reads the latest queue state captured above.
        lock (ServiceSync)
        {
            if (_serviceStarted)
                return;

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

    internal static TransferBackgroundState GetLatestState(TransferBackgroundState fallback)
    {
        lock (ServiceSync)
            return _hasLatestState ? _latestState : fallback;
    }

    internal static void NotifyServiceStopped()
    {
        lock (ServiceSync)
            _serviceStarted = false;
    }

    internal static void ResumeActiveTransfers()
    {
        TransferBackgroundState state;
        lock (ServiceSync)
            state = _latestState;
        if (state.HasActiveTransfers)
        {
            try { new AndroidTransferBackgroundService().Update(state); }
            catch { /* Service starts may be restricted until the Activity is fully resumed. */ }
        }
    }

    internal static void NotifyPermissionResult(int requestCode)
    {
        if (requestCode == NotificationPermissionRequestCode)
            _notificationPermissionPending = false;
    }

    private static void TryRequestBatteryOptimizationExemption()
    {
        if (_batteryPermissionRequestedThisProcess)
            return;
        var activity = global::Hello1Drive.Android.MainActivity.Instance;
        if (activity is null || !activity.IsResumed)
            return;
        var manager = activity.GetSystemService(global::Android.Content.Context.PowerService) as global::Android.OS.PowerManager;
        if (manager is null || manager.IsIgnoringBatteryOptimizations(activity.PackageName))
            return;

        _batteryPermissionRequestedThisProcess = true;
        activity.RunOnUiThread(() =>
        {
            if (!activity.IsResumed)
                return;
            try
            {
                // The system asks the user; the app never silently changes battery policy.
                using var intent = new global::Android.Content.Intent(
                    global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                    global::Android.Net.Uri.Parse($"package:{activity.PackageName}"));
                activity.StartActivity(intent);
            }
            catch
            {
                // Some OEM builds omit this intent. The FGS and retry loop still remain active.
            }
        });
    }

    private static bool TryRequestNotificationPermission()
    {
        // POST_NOTIFICATIONS only exists from Android 13 (API 33). Keep every reference to the
        // API-33-only manifest constant inside an API-33 platform context so CA1416 also understands
        // the deferred RunOnUiThread callback.
        // Waiting -> Running updates must not open the battery dialog over an active permission
        // dialog. Continue permission setup only after Android delivers its result callback.
        if (_notificationPermissionPending)
            return true;
        if (_notificationPermissionRequestedThisProcess || !OperatingSystem.IsAndroidVersionAtLeast(33))
            return false;

        var activity = global::Hello1Drive.Android.MainActivity.Instance;
        if (activity is null || !activity.IsResumed)
            return false;

        _notificationPermissionRequestedThisProcess = true;
        _notificationPermissionPending = true;
        var requested = RequestNotificationPermissionApi33(activity);
        if (!requested)
            _notificationPermissionPending = false;
        return requested;
    }

    [SupportedOSPlatform("android33.0")]
    private static bool RequestNotificationPermissionApi33(global::Hello1Drive.Android.MainActivity activity)
    {
        if (activity.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) ==
            global::Android.Content.PM.Permission.Granted)
        {
            return false;
        }

        activity.RunOnUiThread(() => activity.RequestPermissions(
            [global::Android.Manifest.Permission.PostNotifications],
            NotificationPermissionRequestCode));
        return true;
    }
}
