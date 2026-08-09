using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Hello1Drive.Models;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Views;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private IDisposable? _backgroundFrostBinding;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                UpdateWindowFrame();
        };
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount >= 2)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || sender is not Control { Tag: string edgeTag })
            return;

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || !Enum.TryParse<WindowEdge>(edgeTag, out var edge))
            return;

        BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object? sender, RoutedEventArgs e) => ToggleMaximizeRestore();
    private void CloseButton_Click(object? sender, RoutedEventArgs e) => MainContent.RequestCloseConfirmation();

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        MainContent.RequestCloseConfirmation();
    }

    public void ConfirmClose()
    {
        _allowClose = true;
        Close();
    }


    private async void BreadcrumbButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BreadcrumbItem item } && DataContext is MainViewModel vm)
            await vm.NavigateToBreadcrumbAsync(item);
    }

    private async void AccountOpenWebMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
            await topLevel.Launcher.LaunchUriAsync(new Uri("https://onedrive.live.com/"));
    }

    private async void SettingsButton_Click(object? sender, RoutedEventArgs e) => await MainContent.ToggleSettingsPanelAsync();
    private async void AccountSettingsMenuItem_Click(object? sender, RoutedEventArgs e) => await MainContent.ToggleSettingsPanelAsync();

    private void AccountLogoutMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.RequestLogoutCommand.Execute(null);
    }

    public void SetWindowBackgroundColor(IBrush brush, bool preserveSolidColorAcrossTheme = false)
    {
        WindowBackgroundColorLayer.Background = brush;

        if (preserveSolidColorAcrossTheme)
        {
            // The selected HEX color belongs to the wallpaper, not to Light/Dark theme resources.
            // Use the same brush for the frost layer so changing theme cannot tint/replace it.
            _backgroundFrostBinding?.Dispose();
            _backgroundFrostBinding = null;
            WindowBackgroundFrostLayer.Background = brush;
        }
        else
        {
            UseThemeBackgroundFrost();
        }
    }

    public void SetWindowBackgroundImage(Bitmap? bitmap)
    {
        if (bitmap is not null)
            UseThemeBackgroundFrost();

        WindowBackgroundImageLayer.Source = bitmap;
        WindowBackgroundImageLayer.IsVisible = bitmap is not null;
    }

    public void UseThemeBackgroundFrost()
    {
        _backgroundFrostBinding?.Dispose();
        _backgroundFrostBinding = WindowBackgroundFrostLayer.Bind(
            Border.BackgroundProperty,
            this.GetResourceObservable("SystemControlBackgroundAltHighBrush"));
    }

    public void SetWindowBackgroundAcrylic(double percent)
    {
        var normalized = Math.Clamp(double.IsFinite(percent) ? percent : 50d, 0d, 100d) / 100d;
        var blurRadius = 28d * normalized;
        WindowBackgroundImageLayer.Effect = blurRadius <= 0.01 ? null : new BlurEffect { Radius = blurRadius };
        WindowBackgroundColorLayer.Effect = blurRadius <= 0.01 ? null : new BlurEffect { Radius = blurRadius };

        // A solid color cannot visually blur by itself, so the translucent frost layer also
        // follows the slider. This makes the control meaningful for both image and color backgrounds.
        WindowBackgroundFrostLayer.Opacity = 0.08 + (0.39 * normalized);
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateWindowFrame();
    }

    private void UpdateWindowFrame()
    {
        WindowFrame.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(10);
        // Do not paint a translucent frame around the client area. The invisible resize
        // hit targets remain active, so the window still resizes from all four edges/corners.
        WindowFrame.BorderThickness = new Thickness(0);
        ResizeGripLayer.IsVisible = WindowState == WindowState.Normal;
        ResizeGripLayer.IsHitTestVisible = WindowState == WindowState.Normal;
    }
}
