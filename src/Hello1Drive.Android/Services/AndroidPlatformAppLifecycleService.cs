using Hello1Drive.Services;

namespace Hello1Drive.Android.Services;

public sealed class AndroidPlatformAppLifecycleService : IPlatformAppLifecycleService
{
    public void ExitApp()
    {
        var activity = global::Hello1Drive.Android.MainActivity.Instance;
        if (activity is null)
            return;

        activity.RunOnUiThread(activity.FinishAffinity);
    }
}
