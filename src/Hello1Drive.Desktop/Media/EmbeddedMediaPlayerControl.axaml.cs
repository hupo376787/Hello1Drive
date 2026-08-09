using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace Hello1Drive.Desktop.Media;

public partial class EmbeddedMediaPlayerControl : UserControl
{
    private readonly LibVLCSharp.Shared.MediaPlayer _player;
    private readonly LibVLCSharp.Shared.Media _media;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _seeking;
    private bool _started;
    private bool _shutdown;

    public EmbeddedMediaPlayerControl(LibVLCSharp.Shared.MediaPlayer player, LibVLCSharp.Shared.Media media, string displayName)
    {
        InitializeComponent();
        _player = player;
        _media = media;
        VideoPlayer.MediaPlayer = _player;
        VolumeSlider.Value = _player.Volume;

        _timer.Tick += Timer_Tick;
        Loaded += OnLoaded;
        _player.EncounteredError += Player_EncounteredError;
        _player.EndReached += Player_EndReached;
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_started || _shutdown)
            return;
        _started = true;
        _player.Media = _media;
        _player.Play();
        _timer.Start();
        Focus();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_shutdown)
            return;

        var length = Math.Max(0, _player.Length);
        var time = Math.Max(0, _player.Time);

        if (!_seeking)
        {
            PositionSlider.Maximum = Math.Max(1, length);
            PositionSlider.Value = Math.Min(time, PositionSlider.Maximum);
        }

        CurrentTimeText.Text = FormatTime(time);
        DurationText.Text = FormatTime(length);
        PlayPauseButton.Content = _player.IsPlaying ? "⏸" : "▶";
        MuteButton.Content = _player.Mute || _player.Volume <= 0 ? "🔇" : "🔊";
    }

    private void PlayPauseButton_Click(object? sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (_player.IsPlaying)
            _player.Pause();
        else
            _player.Play();
    }

    private void MuteButton_Click(object? sender, RoutedEventArgs e)
    {
        _player.Mute = !_player.Mute;
        MuteButton.Content = _player.Mute ? "🔇" : "🔊";
    }

    private void VolumeSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_shutdown)
            return;
        _player.Volume = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        if (_player.Volume > 0 && _player.Mute)
            _player.Mute = false;
    }

    private void PositionSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _seeking = true;
    }

    private void PositionSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_seeking)
            return;
        _seeking = false;
        if (_player.IsSeekable)
            _player.Time = Math.Clamp((long)PositionSlider.Value, 0, Math.Max(0, _player.Length));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
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

    private void Player_EndReached(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_shutdown)
                return;
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
            ErrorText.Text = "当前文件的封装格式或编解码器不可用。可关闭预览后使用系统播放器打开。";
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
        _player.EncounteredError -= Player_EncounteredError;
        _player.EndReached -= Player_EndReached;
        Loaded -= OnLoaded;
        VideoPlayer.MediaPlayer = null;
    }
}
