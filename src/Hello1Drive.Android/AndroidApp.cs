using Android.Runtime;
using Avalonia.Android;
using Hello1Drive.Android.Media;
using Hello1Drive.Android.Services;
using Hello1Drive.Services;

namespace Hello1Drive.Android;

[global::Android.App.Application]
public class AndroidApp : AvaloniaAndroidApplication<global::Hello1Drive.App>
{
    protected AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        AppServices.Configure(new AndroidAuthenticationService(), new AndroidEmbeddedMediaPlayerFactory(), new AndroidPlatformShareService(), new AndroidPlatformAppLifecycleService(), transferBackgroundService: new AndroidTransferBackgroundService());
        base.OnCreate();
    }
}
