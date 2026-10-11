using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Views;

public partial class MainView
{
    private string? _desktopFocusedItemId;
    private Button? _desktopPreviewPreviousButton;
    private Button? _desktopPreviewNextButton;

    private void InitializeDesktopExperienceEnhancements()
    {
        if (IsMobilePlatform)
            return;

        EqualizeDesktopDestinationButtons();
        InitializeDesktopPreviewEdgeNavigation();
    }

    private void DisposeDesktopExperienceEnhancements()
    {
        if (IsMobilePlatform)
            return;

        PreviewImageViewport.PointerMoved -= DesktopPreviewImageViewport_PointerMoved;
        PreviewImageViewport.PointerExited -= DesktopPreviewImageViewport_PointerExited;

        if (_desktopPreviewPreviousButton is not null)
            _desktopPreviewPreviousButton.Click -= DesktopPreviewPreviousButton_Click;
        if (_desktopPreviewNextButton is not null)
            _desktopPreviewNextButton.Click -= DesktopPreviewNextButton_Click;
    }

    private void EqualizeDesktopDestinationButtons()
    {
        if (IsMobilePlatform)
            return;

        var cancel = EnumerateControls(MobileDestinationOverlay)
            .OfType<Button>()
            .FirstOrDefault(button => ButtonContainsText(button, "取消"));
        if (cancel is null)
            return;

        const double actionWidth = 120;
        cancel.Width = actionWidth;
        MobileDestinationConfirmButton.Width = actionWidth;
        cancel.HorizontalContentAlignment = HorizontalAlignment.Center;
        MobileDestinationConfirmButton.HorizontalContentAlignment = HorizontalAlignment.Center;
    }

    private void InitializeDesktopPreviewEdgeNavigation()
    {
        if (IsMobilePlatform || _desktopPreviewPreviousButton is not null)
            return;

        _desktopPreviewPreviousButton = CreateDesktopPreviewEdgeButton(
            "上一张",
            HorizontalAlignment.Left,
            "M10.5,2 L4,7 L10.5,12");
        _desktopPreviewNextButton = CreateDesktopPreviewEdgeButton(
            "下一张",
            HorizontalAlignment.Right,
            "M3.5,2 L10,7 L3.5,12");

        _desktopPreviewPreviousButton.Click += DesktopPreviewPreviousButton_Click;
        _desktopPreviewNextButton.Click += DesktopPreviewNextButton_Click;
        PreviewImageViewport.Children.Add(_desktopPreviewPreviousButton);
        PreviewImageViewport.Children.Add(_desktopPreviewNextButton);
        PreviewImageViewport.PointerMoved += DesktopPreviewImageViewport_PointerMoved;
        PreviewImageViewport.PointerExited += DesktopPreviewImageViewport_PointerExited;
    }

    private static Button CreateDesktopPreviewEdgeButton(string toolTip, HorizontalAlignment alignment, string geometry)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            Stroke = Brushes.White,
            StrokeThickness = 1.9,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse(geometry)
        };

        var button = new Button
        {
            Width = 48,
            Height = 48,
            Padding = new Thickness(14),
            Margin = alignment == HorizontalAlignment.Left ? new Thickness(14, 0, 0, 0) : new Thickness(0, 0, 14, 0),
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(178, 24, 24, 24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(24),
            Content = path,
            IsVisible = false,
            ZIndex = 60
        };
        ToolTip.SetTip(button, toolTip);
        return button;
    }

    private void DesktopPreviewImageViewport_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_desktopPreviewPreviousButton is null || _desktopPreviewNextButton is null ||
            DataContext is not MainViewModel { IsImagePreview: true })
        {
            return;
        }

        var width = PreviewImageViewport.Bounds.Width;
        if (width <= 1)
            return;

        var x = e.GetPosition(PreviewImageViewport).X;
        var edgeWidth = Math.Clamp(width * 0.14, 84, 150);
        _desktopPreviewPreviousButton.IsVisible = x <= edgeWidth;
        _desktopPreviewNextButton.IsVisible = x >= width - edgeWidth;
    }

    private void DesktopPreviewImageViewport_PointerExited(object? sender, PointerEventArgs e) =>
        HideDesktopPreviewEdgeButtons();

    private void HideDesktopPreviewEdgeButtons()
    {
        if (_desktopPreviewPreviousButton is not null)
            _desktopPreviewPreviousButton.IsVisible = false;
        if (_desktopPreviewNextButton is not null)
            _desktopPreviewNextButton.IsVisible = false;
    }

    private void DesktopPreviewPreviousButton_Click(object? sender, RoutedEventArgs e)
    {
        PreviewPrevious_Click(sender, e);
        e.Handled = true;
    }

    private void DesktopPreviewNextButton_Click(object? sender, RoutedEventArgs e)
    {
        PreviewNext_Click(sender, e);
        e.Handled = true;
    }

    private async void DesktopFileSurface_KeyDown(object? sender, KeyEventArgs e)
    {
        if (IsMobilePlatform || e.Handled || DataContext is not MainViewModel vm ||
            vm.IsPreviewVisible || vm.IsSettingsPanelVisible || vm.IsTransferPanelVisible ||
            vm.IsPromptVisible || vm.IsCloseConfirmVisible || vm.IsLogoutConfirmVisible ||
            vm.VirtualItems.Count == 0)
            return;

        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _desktopSelectedIds.Clear();
            foreach (var slot in vm.VirtualItems)
                if (slot.Item is { } item)
                    _desktopSelectedIds.Add(item.Id);
            ApplyDesktopSelection(vm);
            e.Handled = true;
            return;
        }

        var focusId = _desktopFocusedItemId ?? vm.SelectedItem?.Id;
        var current = -1;
        for (var i = 0; i < vm.VirtualItems.Count; i++)
            if (string.Equals(vm.VirtualItems[i].Item?.Id, focusId, StringComparison.Ordinal) && focusId is not null)
            {
                current = i;
                break;
            }

        if (e.Key is Key.Enter or Key.Space && current >= 0 && vm.VirtualItems[current].Item is { } focused)
        {
            e.Handled = true;
            _contextItem = focused;
            if (e.Key == Key.Enter)
                await OpenDriveItemAsync(vm, focused);
            else
                ApplyDesktopPointerSelection(vm, focused, e.KeyModifiers);
            return;
        }

        var columns = DesktopFileSurface.Columns;
        var page = Math.Max(columns, (int)(DesktopVirtualScrollViewer.Viewport.Height /
            DesktopFileSurface.MouseWheelLineHeight) * columns);
        var target = e.Key switch
        {
            Key.Home => 0,
            Key.End => vm.VirtualItems.Count - 1,
            Key.Left => current - 1,
            Key.Right => current + 1,
            Key.Up => current - columns,
            Key.Down => current + columns,
            Key.PageUp => current - page,
            Key.PageDown => current + page,
            _ => -1
        };
        if (e.Key is not (Key.Home or Key.End or Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown))
            return;

        e.Handled = true;
        target = current < 0 && e.Key is not (Key.Home or Key.End)
            ? Math.Max(0, DesktopFileSurface.GetVisibleRange().First)
            : Math.Clamp(target, 0, vm.VirtualItems.Count - 1);
        // Skip metadata placeholders; they are visible slots but cannot be opened/selected yet.
        var direction = e.Key is Key.End or Key.Left or Key.Up or Key.PageUp ? -1 : 1;
        while (target >= 0 && target < vm.VirtualItems.Count && vm.VirtualItems[target].Item is null)
            target += direction;
        if (target < 0 || target >= vm.VirtualItems.Count || vm.VirtualItems[target].Item is not { } next)
            return;

        _desktopFocusedItemId = next.Id;
        _contextItem = next;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            ApplyDesktopPointerSelection(vm, next, e.KeyModifiers);

        var rect = DesktopFileSurface.GetItemBounds(target);
        var scroll = DesktopVirtualScrollViewer;
        var offset = scroll.Offset.Y;
        if (rect.Top < offset)
            offset = rect.Top;
        else if (rect.Bottom > offset + scroll.Viewport.Height)
            offset = Math.Max(0, rect.Bottom - scroll.Viewport.Height);
        scroll.Offset = new Vector(0, offset);
        SyncDesktopVirtualSurfaceViewport(scroll);
    }
}
