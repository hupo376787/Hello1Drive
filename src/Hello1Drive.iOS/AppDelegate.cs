using Avalonia;
using Avalonia.iOS;
using Foundation;
using Hello1Drive.iOS.Services;
using Hello1Drive.Services;

namespace Hello1Drive.iOS;

[Register("AppDelegate")]
public class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        AppServices.Configure(new IosAuthenticationService());
        return base.CustomizeAppBuilder(builder).LogToTrace();
    }
}
