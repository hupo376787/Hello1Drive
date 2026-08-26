using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Hello1Drive.Views;

public partial class MainView
{
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Android/iOS render their FAB on the native list layer. Desktop keeps the Avalonia button;
        // replace its old generic arrow/tray with the same cloud-upload language used by Android.
        // Doing this here avoids adding another visual layer and leaves the existing drag surface,
        // hit testing, accent color and 48-DIP geometry untouched.
        FloatingUploadButton.Content = new Path
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
    }
}
