using Avalonia.Controls;
using Avalonia.Platform;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Controls;

/// <summary>
/// Portable Avalonia host for the Windows native file list. The actual HWND implementation lives
/// in Hello1Drive.Desktop so Core remains platform-neutral.
/// </summary>
public sealed class NativeDesktopFileListHost : NativeControlHost
{
    private IPlatformHandle? _nativeHandle;
    private MainViewModel? _viewModel;

    public event EventHandler? HostStateChanged;
    public event EventHandler<NativeDesktopSelectionChangedEventArgs>? SelectionChanged;
    public event EventHandler<NativeDesktopFileItemEventArgs>? ItemDoubleTapped;
    public event EventHandler<NativeDesktopFileItemEventArgs>? ItemContextRequested;
    public event EventHandler<NativeDesktopFileScrollEventArgs>? ScrollStateChanged;

    public MainViewModel? ViewModel => _viewModel;
    public int LastFirstVisibleIndex { get; private set; }

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
