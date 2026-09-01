using System.Collections;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Hello1Drive.Views;

public partial class MainView
{
    private bool _desktopMenuLayoutNormalized;

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
        // Wait until the view is fully attached, then normalize desktop view/sort rows to:
        //   selected-state dot | full-size icon | text
        // The dot gets its own fixed column and never participates in the icon presenter's sizing.
        if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
        {
            Dispatcher.UIThread.Post(NormalizeDesktopMenuLayout, DispatcherPriority.Loaded);
        }
        else
        {
            // Mobile keeps touch scrolling, but the transfer list should not draw a right-side scrollbar.
            Dispatcher.UIThread.Post(HideMobileTransferListScrollbar, DispatcherPriority.Loaded);
        }
    }

    private void HideMobileTransferListScrollbar()
    {
        var transferList = this.GetVisualDescendants()
            .OfType<ListBox>()
            .FirstOrDefault(listBox => listBox.Classes.Contains("transferList"));

        if (transferList is null)
            return;

        transferList.SetValue(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Hidden);
    }

    private void NormalizeDesktopMenuLayout()
    {
        if (_desktopMenuLayoutNormalized)
            return;

        var changed = 0;

        // Toolbar sort/view flyouts.
        foreach (var button in this.GetVisualDescendants().OfType<Button>())
        {
            if (button.Flyout is MenuFlyout flyout)
                changed += NormalizeDesktopMenuItems(flyout.Items);
        }

        // The file-area context menu contains another view/sort submenu.
        if (FileArea.ContextMenu is ContextMenu contextMenu)
            changed += NormalizeDesktopMenuItems(contextMenu.Items);

        if (changed > 0)
            _desktopMenuLayoutNormalized = true;
    }

    private static int NormalizeDesktopMenuItems(IEnumerable? items)
    {
        if (items is null)
            return 0;

        var changed = 0;
        foreach (var rawItem in items)
        {
            if (rawItem is not MenuItem menuItem)
                continue;

            if (NormalizeViewModeMenuItem(menuItem) || NormalizeSortMenuItem(menuItem))
                changed++;

            changed += NormalizeDesktopMenuItems(menuItem.Items);
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

        // The XAML stores both the dot and the Path inside MenuItem.Icon. Extract them and
        // rebuild the visible row so the 15-DIP icon keeps its original size.
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

        menuItem.Header = null;
        menuItem.Icon = null;

        PrepareMenuIcon(icon);
        PrepareSelectionDot(dot);
        label.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        menuItem.Header = BuildThreeColumnMenuHeader(dot, icon, label);
        return true;
    }

    private static bool NormalizeSortMenuItem(MenuItem menuItem)
    {
        if (menuItem.Tag is not string tag || tag is not (
                "Inherit:Default" or
                "Name:Ascending" or
                "Name:Descending" or
                "Modified:Ascending" or
                "Modified:Descending" or
                "Size:Ascending" or
                "Size:Descending"))
        {
            return false;
        }

        // Sort selection dots currently live in the Header while some items also use MenuItem.Icon.
        // Pull the dot and label out, discard the old icon, and rebuild every sort row consistently.
        if (menuItem.Header is not Grid oldHeaderGrid)
            return false;

        var dot = oldHeaderGrid.Children.OfType<Ellipse>().FirstOrDefault();
        var label = oldHeaderGrid.Children.OfType<TextBlock>().FirstOrDefault();
        if (dot is null || label is null)
            return false;

        oldHeaderGrid.Children.Remove(dot);
        oldHeaderGrid.Children.Remove(label);
        menuItem.Header = null;
        menuItem.Icon = null;

        PrepareSelectionDot(dot);
        label.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;

        var icon = CreateSortMenuIcon(tag);
        menuItem.Header = BuildThreeColumnMenuHeader(dot, icon, label);
        return true;
    }

    private static Grid BuildThreeColumnMenuHeader(Control dot, Control icon, Control label)
    {
        var header = new Grid
        {
            ColumnSpacing = 0,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        // Wider independent columns keep both markers visually centered without changing glyph size.
        header.ColumnDefinitions.Add(new ColumnDefinition(10, GridUnitType.Pixel));
        header.ColumnDefinitions.Add(new ColumnDefinition(50, GridUnitType.Pixel));
        header.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Auto));

        Grid.SetColumn(dot, 0);
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(label, 2);
        header.Children.Add(dot);
        header.Children.Add(icon);
        header.Children.Add(label);
        return header;
    }

    private static void PrepareSelectionDot(Ellipse dot)
    {
        dot.Width = 6;
        dot.Height = 6;
        dot.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        dot.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
    }

    private static void PrepareMenuIcon(Avalonia.Controls.Shapes.Path icon)
    {
        icon.Width = 15;
        icon.Height = 15;
        icon.Stretch = Stretch.Uniform;
        icon.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        icon.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
    }

    private static Avalonia.Controls.Shapes.Path CreateSortMenuIcon(string tag)
    {
        // Each sorting family has its own visual language:
        // default = neutral list, name = A/Z, date = calendar, size = graduated boxes.
        var data = tag switch
        {
            "Inherit:Default" =>
                "M2,3 H12 M2,7 H12 M2,11 H12 M4.2,1.8 V4.2 M9.2,5.8 V8.2 M6.4,9.8 V12.2",

            "Name:Ascending" =>
                "M3.2,12 V3 M1.4,5 L3.2,3 L5,5 " +
                "M7.6,7 L9.8,3 L12,7 M8.3,5.6 H11.3 M7.7,9 H12 L7.7,12 H12",
            "Name:Descending" =>
                "M3.2,3 V12 M1.4,10 L3.2,12 L5,10 " +
                "M7.7,3 H12 L7.7,6 H12 M7.6,12 L9.8,8 L12,12 M8.3,10.6 H11.3",

            "Modified:Ascending" =>
                "M3.2,12 V4 M1.4,5.8 L3.2,4 L5,5.8 " +
                "M7,4 H13 V12 H7 Z M8.5,2.5 V5 M11.5,2.5 V5 M7,6.5 H13 " +
                "M8.5,8.5 H10 M11.5,8.5 H12 M8.5,10.5 H10",
            "Modified:Descending" =>
                "M3.2,4 V12 M1.4,10.2 L3.2,12 L5,10.2 " +
                "M7,4 H13 V12 H7 Z M8.5,2.5 V5 M11.5,2.5 V5 M7,6.5 H13 " +
                "M8.5,8.5 H10 M11.5,8.5 H12 M8.5,10.5 H10",

            "Size:Ascending" =>
                "M3.2,12 V3 M1.4,5 L3.2,3 L5,5 " +
                "M8,3.5 H9.5 V5 H8 Z M8,6.5 H10.5 V9 H8 Z M8,10 H12 V14 H8 Z",
            _ =>
                "M3.2,3 V12 M1.4,10 L3.2,12 L5,10 " +
                "M8,2 H12 V6 H8 Z M8,7 H10.5 V9.5 H8 Z M8,11 H9.5 V12.5 H8 Z"
        };

        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data)
        };
        icon.Classes.Add("menuIcon");
        icon.Classes.Add("iconSort");
        PrepareMenuIcon(icon);
        return icon;
    }
}
