using System.Runtime.InteropServices.JavaScript;
using Avalonia;
using Avalonia.Browser;
using Hello1Drive.Browser.Services;
using Hello1Drive.Services;

namespace Hello1Drive.Browser;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        await JSHost.ImportAsync("Hello1DriveAuth", "../js/auth.js");
        AppServices.Configure(new BrowserAuthenticationService());
        await BuildAvaloniaApp().StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>();
}
