using Android.Runtime;
using Avalonia.Android;
using Hello1Drive.Android.Services;
using Hello1Drive.Services;

namespace Hello1Drive.Android;

[Android.App.Application]
public sealed class AndroidApp : AvaloniaAndroidApplication<App>
{
    protected AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        AppServices.Configure(new AndroidAuthenticationService());
        base.OnCreate();
    }
}
