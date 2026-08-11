using Foundation;
using Hello1Drive.Services;
using UIKit;

namespace Hello1Drive.iOS.Services;

public sealed class IosPlatformShareService : IPlatformShareService
{
    public Task ShareTextAsync(string title, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var root = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .SelectMany(static scene => scene.Windows)
            .FirstOrDefault(static window => window.IsKeyWindow)
            ?.RootViewController
            ?? throw new InvalidOperationException("iOS 当前没有可用于显示分享面板的窗口。");

        while (root.PresentedViewController is { } presented)
            root = presented;

        var controller = new UIActivityViewController([new NSString(text)], null);
        if (controller.PopoverPresentationController is { } popover && root.View is { } sourceView)
        {
            popover.SourceView = sourceView;
            popover.SourceRect = sourceView.Bounds;
        }

        root.PresentViewController(controller, true, null);
        return Task.CompletedTask;
    }
}
