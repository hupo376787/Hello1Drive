using Avalonia.VisualTree;

namespace Hello1Drive.Views;

public partial class MainView
{
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InitializeDesktopExperienceEnhancements();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DisposeDesktopExperienceEnhancements();
        base.OnDetachedFromVisualTree(e);
    }
}
