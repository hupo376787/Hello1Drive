using Android.App;
using Android.Content;
using Android.Content.PM;
using Microsoft.Identity.Client;

namespace Hello1Drive.Android;

[Activity(
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    NoHistory = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryBrowsable, Intent.CategoryDefault],
    DataHost = "auth",
    DataScheme = "msal9ea6a8b7-0122-4c9a-8b14-752d60de9626")]
public class MsalActivity : BrowserTabActivity
{
}
