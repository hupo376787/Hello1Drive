using Avalonia;
using Hello1Drive.Desktop.Services;
using Hello1Drive.Desktop.Media;
using Hello1Drive.Services;

namespace Hello1Drive.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppServices.Configure(
            new DesktopAuthenticationService(),
            new DesktopEmbeddedMediaPlayerFactory(),
            startupRegistrationService: new WindowsStartupRegistrationService(),
            desktopInputSettingsService: new DesktopInputSettingsService());
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
