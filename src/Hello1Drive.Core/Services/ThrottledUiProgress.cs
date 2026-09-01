using System.Diagnostics;
using Avalonia.Threading;

namespace Hello1Drive.Services;

/// <summary>
/// Progress reporter for high-frequency transfer loops. Reports can arrive from worker threads,
/// but UI updates are marshalled back to Avalonia at a bounded rate. Only one dispatcher callback
/// may be pending at a time: when a phone UI is suspended in the background, network progress is
/// coalesced into the newest value instead of queuing hundreds/thousands of stale UI callbacks.
/// </summary>
internal sealed class ThrottledUiProgress : IProgress<double>
{
    private readonly Action<double> _handler;
    private readonly TimeSpan _minimumInterval;
    private readonly object _sync = new();
    private long _lastDispatchTimestamp;
    private double _latestValue;
    private bool _dispatchPending;

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
            _latestValue = value;

            // If Avalonia is background-suspended the previously posted callback can remain queued
            // for a long time. Do not add another one; simply replace the value it will consume.
            if (_dispatchPending)
                return;

            if (value < 1 && _lastDispatchTimestamp != 0 &&
                Stopwatch.GetElapsedTime(_lastDispatchTimestamp, now) < _minimumInterval)
            {
                return;
            }

            _lastDispatchTimestamp = now;
            _dispatchPending = true;
        }

        Dispatcher.UIThread.Post(DrainLatest, DispatcherPriority.Background);
    }

    private void DrainLatest()
    {
        double value;
        lock (_sync)
        {
            value = _latestValue;
            _dispatchPending = false;
            _lastDispatchTimestamp = Stopwatch.GetTimestamp();
        }

        _handler(value);

        // A final 100% report may have arrived while this dispatcher callback was executing.
        // Re-post it immediately so completion presentation cannot be lost behind throttling.
        lock (_sync)
        {
            if (_latestValue < 1 || _latestValue <= value || _dispatchPending)
                return;
            _dispatchPending = true;
        }
        Dispatcher.UIThread.Post(DrainLatest, DispatcherPriority.Background);
    }
}
