using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Markup.Xaml;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;
using Hello1Drive.Views;

namespace Hello1Drive;

public partial class App : Application
{
    private TrayIcon? _startupTrayIcon;
    private MainWindow? _desktopMainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Avalonia 12 iOS uses a scene-based lifecycle. Subscribing here also
        // catches cold-start URI activations before any asynchronous work runs.
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatableLifetime)
        {
            activatableLifetime.Activated += (_, args) =>
            {
                if (args is ProtocolActivatedEventArgs protocolArgs &&
                    protocolArgs.Kind == ActivationKind.OpenUri)
                {
                    AppServices.Authentication.TryHandleProtocolActivation(protocolArgs.Uri);
                }
            };
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var startInTray = OperatingSystem.IsWindows() &&
                Environment.GetCommandLineArgs().Any(static x =>
                    string.Equals(x, "--tray", StringComparison.OrdinalIgnoreCase));

            var mainWindow = new MainWindow
            {
                DataContext = CreateMainViewModel()
            };
            _desktopMainWindow = mainWindow;

            if (startInTray)
            {
                // The classic desktop lifetime will show MainWindow once. Hide it immediately
                // on Opened and remove it from the taskbar so startup is tray-only.
                mainWindow.ShowInTaskbar = false;
                mainWindow.Opacity = 0;
                mainWindow.Opened += (_, _) =>
                {
                    mainWindow.HideToTray();
                    // Restore normal opacity before a later tray-menu open. Setting this only
                    // after Hide prevents a one-frame startup-window flash.
                    mainWindow.Opacity = 1;
                };
                CreateStartupTrayIcon(desktop, mainWindow);
            }

            desktop.MainWindow = mainWindow;
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            activityLifetime.MainViewFactory = () => new MainView
            {
                DataContext = CreateMainViewModel()
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView
            {
                DataContext = CreateMainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateStartupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow mainWindow)
    {
        var showItem = new NativeMenuItem("打开 Hello1Drive");
        showItem.Click += (_, _) => mainWindow.ShowFromTray();

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            _startupTrayIcon?.Dispose();
            _startupTrayIcon = null;
            desktop.Shutdown();
        };

        var menu = new NativeMenu();
        menu.Add(showItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exitItem);

        using var iconStream = AssetLoader.Open(new Uri("avares://Hello1Drive.Core/Assets/app-icon.ico"));
        var trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "Hello1Drive",
            Menu = menu,
            IsVisible = true
        };
        trayIcon.Clicked += (_, _) => mainWindow.ShowFromTray();
        _startupTrayIcon = trayIcon;

        TrayIcon.SetIcons(this, new TrayIcons { trayIcon });
    }

    private static MainViewModel CreateMainViewModel() =>
        new(AppServices.OneDrive, AppServices.Authentication, AppServices.Settings, AppServices.FileCache, AppServices.ThumbnailCache, AppServices.TransferPersistence, AppServices.StartupSnapshot, AppServices.LocalDriveIndex, AppServices.StartupRegistrationService, AppServices.TransferBackgroundService);
}
