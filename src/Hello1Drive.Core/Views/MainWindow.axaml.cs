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

    public event EventHandler? BackgroundVisualChanged;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                UpdateWindowFrame();
        };
        ActualThemeVariantChanged += (_, _) => BackgroundVisualChanged?.Invoke(this, EventArgs.Empty);
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

    public void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
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

        BackgroundVisualChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetWindowBackgroundImage(Bitmap? bitmap)
    {
        if (bitmap is not null)
            UseThemeBackgroundFrost();

        WindowBackgroundImageLayer.Source = bitmap;
        WindowBackgroundImageLayer.IsVisible = bitmap is not null;
        BackgroundVisualChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UseThemeBackgroundFrost()
    {
        _backgroundFrostBinding?.Dispose();
        _backgroundFrostBinding = WindowBackgroundFrostLayer.Bind(
            Border.BackgroundProperty,
            this.GetResourceObservable("SystemControlBackgroundAltHighBrush"));
        BackgroundVisualChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetWindowBackgroundAcrylic(double percent)
    {
        var normalized = Math.Clamp(double.IsFinite(percent) ? percent : 50d, 0d, 100d) / 100d;
        var blurRadius = 28d * normalized;

        // Gaussian blur only changes image content. Blurring the full-window solid-color layer has
        // no useful visual result but can add another off-screen effect pass whenever the window is
        // composited during scrolling, so keep the expensive effect on the image layer only.
        WindowBackgroundImageLayer.Effect = blurRadius <= 0.01 ? null : new BlurEffect { Radius = blurRadius };
        WindowBackgroundColorLayer.Effect = null;

        // A solid color cannot visually blur by itself, so the translucent frost layer also
        // follows the slider. This makes the control meaningful for both image and color backgrounds.
        WindowBackgroundFrostLayer.Opacity = 0.08 + (0.39 * normalized);
        BackgroundVisualChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Renders only the wallpaper layers into an opaque bitmap for native HWND children.
    /// NativeControlHost is an HWND airspace island, so a native ListView cannot truly alpha-blend
    /// with Avalonia content behind it. Painting this exact background snapshot avoids color-key
    /// artifacts while preserving the same image stretch, blur and frost seen by the rest of the UI.
    /// </summary>
    public Bitmap? CaptureWindowBackgroundSnapshot()
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1 || !IsVisible)
            return null;

        var scale = Math.Max(1d, RenderScaling);
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(width * scale)),
            Math.Max(1, (int)Math.Ceiling(height * scale)));
        var dpi = new Vector(96d * scale, 96d * scale);
        var target = new RenderTargetBitmap(pixelSize, dpi);
        var targetRect = new Rect(0, 0, width, height);

        try
        {
            using (var context = target.CreateDrawingContext())
            {
                if (WindowBackgroundColorLayer.Background is { } colorBrush)
                    context.DrawRectangle(colorBrush, null, targetRect);

                if (WindowBackgroundImageLayer.IsVisible && WindowBackgroundImageLayer.Source is not null)
                {
                    using var imageLayer = new RenderTargetBitmap(pixelSize, dpi);
                    imageLayer.Render(WindowBackgroundImageLayer);
                    context.DrawImage(imageLayer, new Rect(imageLayer.Size), targetRect);
                }

                if (WindowBackgroundFrostLayer.Background is { } frostBrush && WindowBackgroundFrostLayer.Opacity > 0.001)
                {
                    using (context.PushOpacity(WindowBackgroundFrostLayer.Opacity))
                        context.DrawRectangle(frostBrush, null, targetRect);
                }
            }

            return target;
        }
        catch
        {
            target.Dispose();
            return null;
        }
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
