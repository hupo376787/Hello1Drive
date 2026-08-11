using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace Hello1Drive.Desktop.Media;

public partial class EmbeddedMediaPlayerControl : UserControl
{
    private LibVLCSharp.Shared.MediaPlayer? _player;
    private LibVLCSharp.Shared.Media? _media;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _seeking;
    private bool _started;
    private bool _playbackEnded;
    private bool _shutdown;

    // Public parameterless constructor is intentionally kept for Avalonia's
    // runtime XAML loader. The real media session uses the overload below.
    public EmbeddedMediaPlayerControl()
    {
        InitializeComponent();

        _timer.Tick += Timer_Tick;
        Loaded += OnLoaded;

        // Slider's internal Thumb handles pointer events itself. Register with
        // handledEventsToo=true so dragging the thumb and clicking the track are
        // both seen by this control and can be committed as a seek operation.
        PositionSlider.AddHandler(
            PointerPressedEvent,
            PositionSlider_PointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            true);
        PositionSlider.AddHandler(
            PointerReleasedEvent,
            PositionSlider_PointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            true);

        AddHandler(
            KeyDownEvent,
            OnKeyDown,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            true);
    }

    public EmbeddedMediaPlayerControl(
        LibVLCSharp.Shared.MediaPlayer player,
        LibVLCSharp.Shared.Media media,
        string displayName)
        : this()
    {
        _player = player;
        _media = media;
        VideoPlayer.MediaPlayer = _player;
        VolumeSlider.Value = _player.Volume;

        _player.EncounteredError += Player_EncounteredError;
        _player.EndReached += Player_EndReached;
        _player.Playing += Player_Playing;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_started || _shutdown || _player is null || _media is null)
            return;

        _started = true;
        _playbackEnded = false;
        _player.Media = _media;
        _player.Play();
        _timer.Start();
        Focus();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_shutdown || _player is null)
            return;

        var length = Math.Max(0, _player.Length);
        var time = Math.Max(0, _player.Time);
        var canSeek = _player.IsSeekable && length > 0;

        PositionSlider.IsEnabled = canSeek;

        if (!_seeking)
        {
            PositionSlider.Maximum = Math.Max(1, length);
            PositionSlider.Value = Math.Min(time, PositionSlider.Maximum);
            CurrentTimeText.Text = FormatTime(time);
        }

        DurationText.Text = FormatTime(length);
        PlayPauseButton.Content = _player.IsPlaying ? "⏸" : "▶";
        MuteButton.Content = _player.Mute || _player.Volume <= 0 ? "🔇" : "🔊";
    }

    private void PlayPauseButton_Click(object? sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (_player is null || _media is null)
            return;

        if (_player.IsPlaying)
        {
            _player.Pause();
            return;
        }

        if (_playbackEnded)
        {
            // LibVLC reaches an Ended state after natural playback completion.
            // Calling Play() alone may not recreate the input pipeline on every
            // backend, so explicitly reset the player and re-attach the same Media.
            // This runs from the UI click after EndReached has returned, avoiding
            // Stop() from inside the LibVLC callback itself.
            _playbackEnded = false;
            try
            {
                _player.Stop();
                _player.Media = _media;
                _player.Play();
            }
            catch
            {
                _playbackEnded = true;
                throw;
            }
            return;
        }

        _player.Play();
    }

    private void MuteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_player is null)
            return;

        _player.Mute = !_player.Mute;
        MuteButton.Content = _player.Mute ? "🔇" : "🔊";
    }

    private void VolumeSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_shutdown || _player is null)
            return;

        _player.Volume = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        if (_player.Volume > 0 && _player.Mute)
            _player.Mute = false;
    }

    private void PositionSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_shutdown || _player is null || !_player.IsSeekable)
            return;

        _seeking = true;
        CurrentTimeText.Text = FormatTime((long)PositionSlider.Value);
    }

    private void PositionSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_seeking)
            return;

        // While the user drags the thumb, keep the time label synchronized with
        // the proposed position. The actual seek is committed on pointer release,
        // which avoids flooding LibVLC with seek requests during rapid dragging.
        CurrentTimeText.Text = FormatTime((long)e.NewValue);
    }

    private void PositionSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_seeking)
            return;

        _seeking = false;
        CommitSeek();
    }

    private void CommitSeek()
    {
        if (_shutdown || _player is null || !_player.IsSeekable)
            return;

        var length = Math.Max(0, _player.Length);
        var target = Math.Clamp((long)Math.Round(PositionSlider.Value), 0, length);
        _player.Time = target;
        CurrentTimeText.Text = FormatTime(target);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_player is null)
            return;

        if (e.Key == Key.Space)
        {
            TogglePlayback();
            e.Handled = true;
            return;
        }

        if (!_player.IsSeekable)
            return;

        if (e.Key == Key.Left)
        {
            _player.Time = Math.Max(0, _player.Time - 5_000);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            _player.Time = Math.Min(Math.Max(0, _player.Length), _player.Time + 5_000);
            e.Handled = true;
        }
    }

    private void Player_Playing(object? sender, EventArgs e)
    {
        _playbackEnded = false;
    }

    private void Player_EndReached(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_shutdown)
                return;

            _playbackEnded = true;
            PositionSlider.Value = 0;
            CurrentTimeText.Text = "00:00";
            PlayPauseButton.Content = "▶";
        });
    }

    private void Player_EncounteredError(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_shutdown)
                return;

            ErrorText.Text = "当前文件的封装格式或编解码器不可用。可使用系统应用打开。";
            ErrorOverlay.IsVisible = true;
        });
    }

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    public void Shutdown()
    {
        if (_shutdown)
            return;

        _shutdown = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        Loaded -= OnLoaded;

        if (_player is not null)
        {
            _player.EncounteredError -= Player_EncounteredError;
            _player.EndReached -= Player_EndReached;
            _player.Playing -= Player_Playing;
        }

        VideoPlayer.MediaPlayer = null;
    }
}
