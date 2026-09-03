using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;
using Hello1Drive.Views;

namespace Hello1Drive.Controls;

/// <summary>
/// Portable Avalonia host for the Windows native file list. The actual HWND implementation lives
/// in Hello1Drive.Desktop so Core remains platform-neutral.
/// </summary>
public sealed class NativeDesktopFileListHost : NativeControlHost
{
    private IPlatformHandle? _nativeHandle;
    private MainViewModel? _viewModel;
    private MainWindow? _backgroundWindow;
    private bool _backdropRefreshPending;
    private bool _backdropContentDirty = true;
    private byte[]? _backdropImageBytes;
    private long _backdropContentVersion;
    private long _backdropGeometryVersion;

    public NativeDesktopFileListHost()
    {
        AttachedToVisualTree += NativeDesktopFileListHost_AttachedToVisualTree;
        DetachedFromVisualTree += NativeDesktopFileListHost_DetachedFromVisualTree;
        SizeChanged += NativeDesktopFileListHost_SizeChanged;
    }

    public event EventHandler? HostStateChanged;
    public event EventHandler<NativeDesktopSelectionChangedEventArgs>? SelectionChanged;
    public event EventHandler<NativeDesktopFileItemEventArgs>? ItemDoubleTapped;
    public event EventHandler<NativeDesktopFileItemEventArgs>? ItemContextRequested;
    public event EventHandler<NativeDesktopFileScrollEventArgs>? ScrollStateChanged;

    public MainViewModel? ViewModel => _viewModel;
    public int LastFirstVisibleIndex { get; private set; }

    // Native HWND children cannot alpha-compose Avalonia visuals beneath them. Instead the host
    // supplies a rendered snapshot of the window's background-only layers. The Win32 list paints
    // the matching crop itself, so text keeps normal ClearType rendering and view switches cannot
    // expose a black/white native backbuffer.
    public byte[]? BackdropImageBytes => _backdropImageBytes;
    public Size BackdropViewportSize { get; private set; }
    public Point BackdropOrigin { get; private set; }
    public long BackdropContentVersion => _backdropContentVersion;
    public long BackdropGeometryVersion => _backdropGeometryVersion;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var vm = DataContext as MainViewModel;
        if (ReferenceEquals(_viewModel, vm))
            return;
        _viewModel = vm;
        HostStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshNativePresentation() => HostStateChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseSelectionChanged(IReadOnlyList<string> itemIds) =>
        SelectionChanged?.Invoke(this, new NativeDesktopSelectionChangedEventArgs(itemIds));

    public void RaiseItemDoubleTapped(DriveItemModel item) =>
        ItemDoubleTapped?.Invoke(this, new NativeDesktopFileItemEventArgs(item));

    public void RaiseItemContextRequested(DriveItemModel item) =>
        ItemContextRequested?.Invoke(this, new NativeDesktopFileItemEventArgs(item));

    public void RaiseScrollStateChanged(int firstVisibleIndex, int lastVisibleIndex)
    {
        LastFirstVisibleIndex = Math.Max(0, firstVisibleIndex);
        ScrollStateChanged?.Invoke(this,
            new NativeDesktopFileScrollEventArgs(LastFirstVisibleIndex, Math.Max(LastFirstVisibleIndex, lastVisibleIndex)));
    }

    private void NativeDesktopFileListHost_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        AttachBackgroundWindow();
        ScheduleBackdropRefresh(captureContent: true);
    }

    private void NativeDesktopFileListHost_DetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachBackgroundWindow();
    }

    private void NativeDesktopFileListHost_SizeChanged(object? sender, SizeChangedEventArgs e) =>
        ScheduleBackdropRefresh(captureContent: false);

    private void AttachBackgroundWindow()
    {
        var window = TopLevel.GetTopLevel(this) as MainWindow;
        if (ReferenceEquals(_backgroundWindow, window))
            return;

        DetachBackgroundWindow();
        _backgroundWindow = window;
        if (_backgroundWindow is null)
            return;

        _backgroundWindow.BackgroundVisualChanged += BackgroundWindow_BackgroundVisualChanged;
        _backgroundWindow.SizeChanged += BackgroundWindow_SizeChanged;
    }

    private void DetachBackgroundWindow()
    {
        if (_backgroundWindow is null)
            return;
        _backgroundWindow.BackgroundVisualChanged -= BackgroundWindow_BackgroundVisualChanged;
        _backgroundWindow.SizeChanged -= BackgroundWindow_SizeChanged;
        _backgroundWindow = null;
    }

    private void BackgroundWindow_BackgroundVisualChanged(object? sender, EventArgs e) =>
        ScheduleBackdropRefresh(captureContent: true);

    private void BackgroundWindow_SizeChanged(object? sender, SizeChangedEventArgs e) =>
        ScheduleBackdropRefresh(captureContent: true);

    private void ScheduleBackdropRefresh(bool captureContent)
    {
        if (captureContent)
            _backdropContentDirty = true;

        if (_backdropRefreshPending)
            return;
        _backdropRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _backdropRefreshPending = false;
            RefreshBackdropSnapshot();
        }, DispatcherPriority.Background);
    }

    private void RefreshBackdropSnapshot()
    {
        AttachBackgroundWindow();
        if (_backgroundWindow is not { } window)
            return;

        var viewport = window.Bounds.Size;
        var origin = TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);
        var changed = false;

        if (_backdropContentDirty)
        {
            _backdropContentDirty = false;
            using var snapshot = window.CaptureWindowBackgroundSnapshot();
            if (snapshot is not null)
            {
                using var stream = new MemoryStream();
#pragma warning disable CS0618
                snapshot.Save(stream);
#pragma warning restore CS0618
                _backdropImageBytes = stream.ToArray();
            }
            else
            {
                _backdropImageBytes = null;
            }
            unchecked { _backdropContentVersion++; }
            changed = true;
        }

        var normalizedViewport = new Size(Math.Max(1, viewport.Width), Math.Max(1, viewport.Height));
        if (Math.Abs(BackdropViewportSize.Width - normalizedViewport.Width) > 0.25 ||
            Math.Abs(BackdropViewportSize.Height - normalizedViewport.Height) > 0.25 ||
            Math.Abs(BackdropOrigin.X - origin.X) > 0.25 ||
            Math.Abs(BackdropOrigin.Y - origin.Y) > 0.25)
        {
            BackdropViewportSize = normalizedViewport;
            BackdropOrigin = origin;
            unchecked { _backdropGeometryVersion++; }
            changed = true;
        }

        if (changed)
            HostStateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var factory = AppServices.NativeDesktopFileListFactory;
        if (factory is null)
            return base.CreateNativeControlCore(parent);

        _nativeHandle = factory.CreateControl(parent, this);
        return _nativeHandle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try
        {
            AppServices.NativeDesktopFileListFactory?.DestroyControl(control);
        }
        finally
        {
            _nativeHandle = null;
            base.DestroyNativeControlCore(control);
        }
    }
}

public sealed class NativeDesktopSelectionChangedEventArgs(IReadOnlyList<string> itemIds) : EventArgs
{
    public IReadOnlyList<string> ItemIds { get; } = itemIds;
}

public sealed class NativeDesktopFileItemEventArgs(DriveItemModel item) : EventArgs
{
    public DriveItemModel Item { get; } = item;
}

public sealed class NativeDesktopFileScrollEventArgs(int firstVisibleIndex, int lastVisibleIndex) : EventArgs
{
    public int FirstVisibleIndex { get; } = firstVisibleIndex;
    public int LastVisibleIndex { get; } = lastVisibleIndex;
}
