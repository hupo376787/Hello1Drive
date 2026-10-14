using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using Microsoft.Identity.Client;
using Hello1Drive.Android.Services;

namespace Hello1Drive.Android;

[Activity(
    Label = "Hello1Drive",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    public static MainActivity? Instance { get; private set; }
    internal bool IsResumed { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Instance = this;
        base.OnCreate(savedInstanceState);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(requestCode, resultCode, data);
    }

    protected override void OnResume()
    {
        base.OnResume();
        IsResumed = true;
        AndroidTransferBackgroundService.ResumeActiveTransfers();
    }

    protected override void OnPause()
    {
        IsResumed = false;
        // The transfer service/worker owns its lifetime; locking the screen must not stop it.
        base.OnPause();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        AndroidTransferBackgroundService.NotifyPermissionResult(requestCode);
        AndroidTransferBackgroundService.ResumeActiveTransfers();
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Instance, this))
            Instance = null;
        base.OnDestroy();
    }
}
