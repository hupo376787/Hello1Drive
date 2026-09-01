using System.Collections;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Hello1Drive.Views;

public partial class MainView
{
    private bool _desktopViewModeMenuLayoutNormalized;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Android/iOS render their FAB on the native list layer. Desktop keeps the Avalonia button;
        // replace its old generic arrow/tray with the same cloud-upload language used by Android.
        // Doing this here avoids adding another visual layer and leaves the existing drag surface,
        // hit testing, accent color and 48-DIP geometry untouched.
        FloatingUploadButton.Content = new Avalonia.Controls.Shapes.Path
        {
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            Stroke = new SolidColorBrush(Color.FromRgb(255, 247, 248)),
            StrokeThickness = 1.55,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            Data = Geometry.Parse(
                "M4.2,13.1 C2.2,13.1 0.9,11.8 0.9,10 C0.9,8.4 2,7 3.5,6.6 " +
                "C4,4.2 6,2.5 8.5,2.5 C10.8,2.5 12.8,3.9 13.5,6 " +
                "C15.6,6.2 17.1,7.8 17.1,9.8 C17.1,11.8 15.7,13.1 13.7,13.1 " +
                "L4.2,13.1 M9,13.9 V7.2 M6.7,9.5 L9,7.2 L11.3,9.5")
        };

        // MenuFlyout objects are created by XAML but are not part of the normal visual tree.
        // Wait until the view is fully attached, then move the selected-state dot out of the
        // MenuItem.Icon presenter. The presenter has a fixed icon slot and was scaling the whole
        // "dot + icon" grid down, which made the original 15-DIP view icon visibly smaller.
        if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
            Dispatcher.UIThread.Post(NormalizeDesktopViewModeMenuLayout, DispatcherPriority.Loaded);
    }

    private void NormalizeDesktopViewModeMenuLayout()
    {
        if (_desktopViewModeMenuLayoutNormalized)
            return;

        var changed = 0;

        // Toolbar view-mode flyout(s).
        foreach (var button in this.GetVisualDescendants().OfType<Button>())
        {
            if (button.Flyout is MenuFlyout flyout)
                changed += NormalizeViewModeMenuItems(flyout.Items);
        }

        // The file-area context menu contains another "查看方式" submenu.
        if (FileArea.ContextMenu is ContextMenu contextMenu)
            changed += NormalizeViewModeMenuItems(contextMenu.Items);

        if (changed > 0)
            _desktopViewModeMenuLayoutNormalized = true;
    }

    private static int NormalizeViewModeMenuItems(IEnumerable? items)
    {
        if (items is null)
            return 0;

        var changed = 0;
        foreach (var rawItem in items)
        {
            if (rawItem is not MenuItem menuItem)
                continue;

            if (NormalizeViewModeMenuItem(menuItem))
                changed++;

            changed += NormalizeViewModeMenuItems(menuItem.Items);
        }

        return changed;
    }

    private static bool NormalizeViewModeMenuItem(MenuItem menuItem)
    {
        if (menuItem.Tag is not string tag ||
            tag is not ("Details" or "LargeIcons" or "ExtraLargeIcons"))
        {
            return false;
        }

        // The previous layout stored both the dot and the Path inside MenuItem.Icon.
        // Extract them and rebuild the visible row as three independent columns:
        //   selection dot | original 15-DIP icon | label
        // This keeps the dot from participating in the icon presenter's size calculation.
        if (menuItem.Icon is not Grid oldIconGrid)
            return false;

        var dot = oldIconGrid.Children.OfType<Ellipse>().FirstOrDefault();
        var icon = oldIconGrid.Children.OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault();
        if (dot is null || icon is null)
            return false;

        oldIconGrid.Children.Remove(dot);
        oldIconGrid.Children.Remove(icon);

        var label = menuItem.Header as TextBlock ?? new TextBlock
        {
            Text = tag switch
            {
                "Details" => "详细信息",
                "LargeIcons" => "大图标",
                _ => "超大图标"
            }
        };

        // Detach the old header before placing the same TextBlock into the new Grid.
        menuItem.Header = null;
        menuItem.Icon = null;

        icon.Width = 15;
        icon.Height = 15;
        icon.Stretch = Stretch.Uniform;
        icon.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        icon.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;

        dot.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        dot.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        label.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;

        var header = new Grid
        {
            ColumnSpacing = 6,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        header.ColumnDefinitions.Add(new ColumnDefinition(8, GridUnitType.Pixel));
        header.ColumnDefinitions.Add(new ColumnDefinition(15, GridUnitType.Pixel));
        header.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Auto));

        Grid.SetColumn(dot, 0);
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(label, 2);
        header.Children.Add(dot);
        header.Children.Add(icon);
        header.Children.Add(label);

        menuItem.Header = header;
        return true;
    }
}
