using Hello1Drive.Services;
using UIKit;

namespace Hello1Drive.iOS.Services;

public sealed class IosPlatformConfirmationService : IPlatformConfirmationService
{
    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText = "确定",
        string cancelText = "取消")
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            var presenter = FindTopViewController();
            if (presenter is null)
            {
                completion.TrySetResult(false);
                return;
            }

            var alert = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);
            alert.AddAction(UIAlertAction.Create(
                cancelText,
                UIAlertActionStyle.Cancel,
                _ => completion.TrySetResult(false)));
            alert.AddAction(UIAlertAction.Create(
                confirmText,
                UIAlertActionStyle.Destructive,
                _ => completion.TrySetResult(true)));

            presenter.PresentViewController(alert, true, null);
        });

        return completion.Task;
    }

    private static UIViewController? FindTopViewController()
    {
        var window = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .SelectMany(static scene => scene.Windows)
            .FirstOrDefault(static candidate => candidate.IsKeyWindow)
            ?? UIApplication.SharedApplication.Windows.FirstOrDefault(static candidate => !candidate.Hidden);

        var controller = window?.RootViewController;
        while (controller?.PresentedViewController is { } presented)
            controller = presented;

        if (controller is UINavigationController navigation)
            controller = navigation.VisibleViewController ?? navigation;
        if (controller is UITabBarController tabs)
            controller = tabs.SelectedViewController ?? tabs;

        return controller;
    }
}
