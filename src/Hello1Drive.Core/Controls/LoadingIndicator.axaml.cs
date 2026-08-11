using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Media;

namespace Hello1Drive.Controls;

public partial class LoadingIndicator : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(24) };
    private readonly RotateTransform _rotation = new();
    private double _angle;

    public LoadingIndicator()
    {
        InitializeComponent();
        SpinnerArc.RenderTransform = _rotation;
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + 12d) % 360d;
            _rotation.Angle = _angle;
        };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }
}
