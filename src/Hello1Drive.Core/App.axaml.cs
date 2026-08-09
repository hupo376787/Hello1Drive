using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;
using Hello1Drive.Views;

namespace Hello1Drive;

public partial class App : Application
{
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
            desktop.MainWindow = new MainWindow
            {
                DataContext = CreateMainViewModel()
            };
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

    private static MainViewModel CreateMainViewModel() =>
        new(AppServices.OneDrive, AppServices.Authentication, AppServices.Settings, AppServices.FileCache, AppServices.ThumbnailCache, AppServices.TransferPersistence);
}
