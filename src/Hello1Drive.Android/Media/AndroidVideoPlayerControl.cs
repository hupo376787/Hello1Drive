using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Views;
using Android.Widget;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Hello1Drive.Android.Media;

/// <summary>
/// Hosts Android.Widget.VideoView inside the Avalonia visual tree.
/// The transport bar is deliberately minimal: one stateful play/pause button followed by
/// the seek progress on the same row. Android MediaController is not used because its stock
/// layout injects rewind/fast-forward buttons that Hello1Drive does not need.
/// </summary>
internal sealed class AndroidVideoPlayerControl : NativeControlHost
{
    private readonly string _localFilePath;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private FrameLayout? _root;
    private VideoView? _videoView;
    private LinearLayout? _controls;
    private global::Android.Widget.Button? _playPauseButton;
    private SeekBar? _seekBar;
    private TextView? _currentTimeText;
    private TextView? _durationText;
    private MediaPlayer? _mediaPlayer;
    private GestureDetector? _gestureDetector;
    private VideoGestureListener? _gestureListener;
    private VideoTouchListener? _touchListener;
    private PreparedListener? _preparedListener;
    private SeekListener? _seekListener;
    private bool _seeking;
    private bool _speedBoostActive;
    private bool _shutdown;

    public AndroidVideoPlayerControl(string localFilePath)
    {
        _localFilePath = localFilePath;
        _progressTimer.Tick += ProgressTimer_Tick;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var context = (parent as AndroidViewControlHandle)?.View.Context
            ?? global::Android.App.Application.Context;

        var root = new FrameLayout(context);
        root.SetBackgroundColor(Color.Black);

        var videoView = new VideoView(context);
        root.AddView(videoView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent)
        {
            Gravity = GravityFlags.Center
        });

        var controls = BuildControls(context);
        root.AddView(controls, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Bottom
        });

        var preparedListener = new PreparedListener(this);
        videoView.SetOnPreparedListener(preparedListener);

        var gestureListener = new VideoGestureListener(this);
        var gestureDetector = new GestureDetector(context, gestureListener);
        var touchListener = new VideoTouchListener(this, gestureDetector);
        videoView.SetOnTouchListener(touchListener);
        videoView.Clickable = true;
        videoView.SetVideoPath(_localFilePath);

        _root = root;
        _videoView = videoView;
        _controls = controls;
        _preparedListener = preparedListener;
        _gestureListener = gestureListener;
        _gestureDetector = gestureDetector;
        _touchListener = touchListener;

        UpdateTransportState();
        return new AndroidViewControlHandle(root);
    }

    private LinearLayout BuildControls(Context context)
    {
        var row = new LinearLayout(context)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal
        };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(context, 8), Dp(context, 6), Dp(context, 8), Dp(context, 6));
        row.SetBackgroundColor(Color.Argb(220, 20, 20, 20));

        var playPause = new global::Android.Widget.Button(context)
        {
            Text = "▶",
            TextSize = 20
        };
        playPause.SetTextColor(Color.White);
        playPause.SetBackgroundColor(Color.Transparent);
        playPause.SetPadding(0, 0, 0, 0);
        playPause.ContentDescription = "播放 / 暂停";
        playPause.Click += PlayPause_Click;
        row.AddView(playPause, new LinearLayout.LayoutParams(Dp(context, 44), Dp(context, 42)));

        var current = BuildTimeText(context, "00:00");
        row.AddView(current, new LinearLayout.LayoutParams(Dp(context, 52), ViewGroup.LayoutParams.WrapContent));

        var seek = new SeekBar(context) { Max = 1000, Progress = 0 };
        var seekListener = new SeekListener(this);
        seek.SetOnSeekBarChangeListener(seekListener);
        row.AddView(seek, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var duration = BuildTimeText(context, "00:00");
        row.AddView(duration, new LinearLayout.LayoutParams(Dp(context, 52), ViewGroup.LayoutParams.WrapContent));

        _playPauseButton = playPause;
        _currentTimeText = current;
        _seekBar = seek;
        _durationText = duration;
        _seekListener = seekListener;
        return row;
    }

    private static TextView BuildTimeText(Context context, string text)
    {
        var view = new TextView(context)
        {
            Text = text,
            TextSize = 12,
            Gravity = GravityFlags.Center
        };
        view.SetTextColor(Color.White);
        return view;
    }

    private static int Dp(Context context, float value) =>
        (int)Math.Round(value * (context.Resources?.DisplayMetrics?.Density ?? 1f));

    private void HandlePrepared(MediaPlayer mediaPlayer)
    {
        if (_shutdown || _videoView is null)
            return;

        _mediaPlayer = mediaPlayer;
        try
        {
            _videoView.Start();
            _controls!.Visibility = ViewStates.Visible;
            _progressTimer.Start();
            UpdateTransportState();
        }
        catch
        {
            // Keep the preview surface alive. The caller still exposes the system-app fallback.
        }
    }

    private void PlayPause_Click(object? sender, EventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (_shutdown || _videoView is null)
            return;

        EndTemporarySpeed();
        try
        {
            if (_videoView.IsPlaying)
                _videoView.Pause();
            else
                _videoView.Start();
            UpdateTransportState();
        }
        catch
        {
            // Ignore a tap while VideoView is changing state.
        }
    }

    private void ProgressTimer_Tick(object? sender, EventArgs e) => UpdateTransportState();

    private void UpdateTransportState()
    {
        if (_shutdown || _videoView is null)
            return;

        try
        {
            var duration = Math.Max(0, _videoView.Duration);
            var position = Math.Clamp(_videoView.CurrentPosition, 0, Math.Max(0, duration));
            if (_playPauseButton is not null)
                _playPauseButton.Text = _videoView.IsPlaying ? "Ⅱ" : "▶";
            if (_currentTimeText is not null)
                _currentTimeText.Text = FormatTime(position);
            if (_durationText is not null)
                _durationText.Text = FormatTime(duration);
            if (!_seeking && _seekBar is not null)
                _seekBar.Progress = duration > 0 ? Math.Clamp((int)Math.Round(position * 1000d / duration), 0, 1000) : 0;
        }
        catch
        {
            // Native playback state may be temporarily unavailable during activity transitions.
        }
    }

    private void BeginSeek() => _seeking = true;

    private void CommitSeek(int progress)
    {
        _seeking = false;
        if (_shutdown || _videoView is null)
            return;

        try
        {
            var duration = Math.Max(0, _videoView.Duration);
            if (duration > 0)
                _videoView.SeekTo((int)Math.Round(duration * Math.Clamp(progress, 0, 1000) / 1000d));
            UpdateTransportState();
        }
        catch { }
    }

    private void ShowControls()
    {
        if (_shutdown || _controls is null)
            return;
        _controls.Visibility = ViewStates.Visible;
        UpdateTransportState();
    }

    public void SetNativeOverlayVisible(bool visible)
    {
        if (_shutdown || _root is null)
            return;

        try { _root.Visibility = visible ? ViewStates.Visible : ViewStates.Invisible; }
        catch { }
    }

    private void BeginTemporarySpeed()
    {
        if (_shutdown || _speedBoostActive || _videoView?.IsPlaying != true || _mediaPlayer is null)
            return;

        try
        {
            using var playbackParams = new PlaybackParams();
            playbackParams.SetPitch(1.0f);
            playbackParams.SetSpeed(2.0f);
            _mediaPlayer.PlaybackParams = playbackParams;
            _speedBoostActive = true;
        }
        catch { _speedBoostActive = false; }
    }

    private void EndTemporarySpeed()
    {
        if (!_speedBoostActive)
            return;

        _speedBoostActive = false;
        if (_shutdown || _mediaPlayer is null)
            return;

        try
        {
            using var playbackParams = new PlaybackParams();
            playbackParams.SetPitch(1.0f);
            playbackParams.SetSpeed(1.0f);
            _mediaPlayer.PlaybackParams = playbackParams;
        }
        catch { }
    }

    private static string FormatTime(int milliseconds)
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

        EndTemporarySpeed();
        _shutdown = true;
        _progressTimer.Stop();
        _progressTimer.Tick -= ProgressTimer_Tick;

        if (_playPauseButton is not null)
            _playPauseButton.Click -= PlayPause_Click;

        if (_videoView is not null)
        {
            try { _videoView.SetOnTouchListener(null); } catch { }
            try { _videoView.SetOnPreparedListener(null); } catch { }
            try { _videoView.StopPlayback(); } catch { }
        }

        if (_seekBar is not null)
            _seekBar.SetOnSeekBarChangeListener(null);

        _seekListener?.Dispose();
        _seekListener = null;
        _touchListener?.Dispose();
        _touchListener = null;
        _gestureDetector?.Dispose();
        _gestureDetector = null;
        _gestureListener?.Dispose();
        _gestureListener = null;
        _preparedListener?.Dispose();
        _preparedListener = null;
        _mediaPlayer = null; // VideoView owns the MediaPlayer instance.
        _playPauseButton = null;
        _seekBar = null;
        _currentTimeText = null;
        _durationText = null;
        _controls = null;
        _videoView = null;
        _root = null;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Shutdown();
        base.DestroyNativeControlCore(control);
    }

    private sealed class PreparedListener(AndroidVideoPlayerControl owner)
        : Java.Lang.Object, MediaPlayer.IOnPreparedListener
    {
        public void OnPrepared(MediaPlayer? mp)
        {
            if (mp is not null)
                owner.HandlePrepared(mp);
        }
    }

    private sealed class SeekListener(AndroidVideoPlayerControl owner)
        : Java.Lang.Object, SeekBar.IOnSeekBarChangeListener
    {
        public void OnProgressChanged(SeekBar? seekBar, int progress, bool fromUser) { }
        public void OnStartTrackingTouch(SeekBar? seekBar) => owner.BeginSeek();
        public void OnStopTrackingTouch(SeekBar? seekBar) => owner.CommitSeek(seekBar?.Progress ?? 0);
    }

    private sealed class VideoGestureListener(AndroidVideoPlayerControl owner)
        : GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDown(MotionEvent e) => true;
        public override bool OnSingleTapConfirmed(MotionEvent e)
        {
            owner.ShowControls();
            return true;
        }
        public override bool OnDoubleTap(MotionEvent e)
        {
            owner.TogglePlayback();
            return true;
        }
        public override void OnLongPress(MotionEvent e) => owner.BeginTemporarySpeed();
    }

    private sealed class VideoTouchListener(AndroidVideoPlayerControl owner, GestureDetector detector)
        : Java.Lang.Object, View.IOnTouchListener
    {
        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (e is null)
                return false;

            _ = detector.OnTouchEvent(e);
            if (e.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
                owner.EndTemporarySpeed();
            return true;
        }
    }
}
