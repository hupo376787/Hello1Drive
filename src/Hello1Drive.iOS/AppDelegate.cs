using Avalonia;
using Avalonia.iOS;
using Foundation;
using Hello1Drive.iOS.Services;
using Hello1Drive.Services;
using UIKit;

namespace Hello1Drive.iOS;

[Register("AppDelegate")]
public class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        var authentication = new IosAuthenticationService();
        var normalOneDrive = new ResilientOneDriveService(new OneDriveService(authentication));
        var backgroundOneDrive = new IosBackgroundOneDriveService(authentication, normalOneDrive);

        AppServices.Configure(
            authentication,
            platformShareService: new IosPlatformShareService(),
            platformConfirmationService: new IosPlatformConfirmationService(),
            nativeMobileFileListFactory: new IosNativeMobileFileListFactory(),
            oneDriveService: backgroundOneDrive);
        return base.CustomizeAppBuilder(builder).LogToTrace();
    }

    public override void HandleEventsForBackgroundUrl(
        UIApplication application,
        string sessionIdentifier,
        Action completionHandler)
    {
        // iOS calls this when a background NSURLSession finishes work while the app was suspended
        // or relaunched. Keep the completion handler until the service has delivered every native
        // callback (and, for Graph upload sessions, scheduled the next sequential chunk).
        IosBackgroundOneDriveService.HandleEventsForBackgroundUrl(sessionIdentifier, completionHandler);
    }
}
