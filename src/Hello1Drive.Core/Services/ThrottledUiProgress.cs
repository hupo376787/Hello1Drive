using System.Diagnostics;
using Avalonia.Threading;

namespace Hello1Drive.Services;

/// <summary>
/// Progress reporter for high-frequency transfer loops. Reports can arrive from worker threads,
/// but UI updates are marshalled back to Avalonia at a bounded rate so fast network/disk I/O
/// cannot flood the dispatcher with hundreds of progress notifications per second.
/// </summary>
internal sealed class ThrottledUiProgress : IProgress<double>
{
    private readonly Action<double> _handler;
    private readonly TimeSpan _minimumInterval;
    private readonly object _sync = new();
    private long _lastDispatchTimestamp;

    public ThrottledUiProgress(Action<double> handler, TimeSpan? minimumInterval = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _minimumInterval = minimumInterval ?? TimeSpan.FromMilliseconds(100);
    }

    public void Report(double value)
    {
        value = Math.Clamp(value, 0, 1);
        var now = Stopwatch.GetTimestamp();

        lock (_sync)
        {
            if (value < 1 && _lastDispatchTimestamp != 0 &&
                Stopwatch.GetElapsedTime(_lastDispatchTimestamp, now) < _minimumInterval)
            {
                return;
            }

            _lastDispatchTimestamp = now;
        }

        Dispatcher.UIThread.Post(() => _handler(value), DispatcherPriority.Background);
    }
}
