using Avalonia.Controls;
using Avalonia.Platform;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Controls;

/// <summary>
/// Hosts the platform-native high-performance file list on Android and iOS.
/// The control intentionally contains no platform API references so Hello1Drive.Core stays portable.
/// </summary>
public sealed class NativeMobileFileListHost : NativeControlHost
{
    private IPlatformHandle? _nativeHandle;
    private MainViewModel? _viewModel;
    private string[] _selectedIds = [];
    private bool _selectionMode;

    public event EventHandler<NativeMobileFileItemEventArgs>? ItemTapped;
    public event EventHandler<NativeMobileFileItemEventArgs>? ItemLongPressed;
    public event EventHandler<NativeMobileFileScrollEventArgs>? ScrollStateChanged;
    public event EventHandler? HostStateChanged;
    public event EventHandler<NativeMobileFileScrollToEventArgs>? ScrollToPositionRequested;
    public Func<Task>? RefreshRequestedAsync { get; set; }

    public MainViewModel? ViewModel => _viewModel;
    public IReadOnlyList<string> SelectedIds => _selectedIds;
    public bool SelectionMode => _selectionMode;
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

    public void UpdateSelectionState(IEnumerable<string> selectedIds, bool selectionMode)
    {
        _selectedIds = selectedIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _selectionMode = selectionMode;
        HostStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshNativePresentation() => HostStateChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseItemTapped(DriveItemModel item) =>
        ItemTapped?.Invoke(this, new NativeMobileFileItemEventArgs(item));

    public void RaiseItemLongPressed(DriveItemModel item) =>
        ItemLongPressed?.Invoke(this, new NativeMobileFileItemEventArgs(item));

    public void RaiseScrollStateChanged(bool isScrolling, int firstVisibleIndex, int lastVisibleIndex)
    {
        LastFirstVisibleIndex = Math.Max(0, firstVisibleIndex);
        ScrollStateChanged?.Invoke(this, new NativeMobileFileScrollEventArgs(isScrolling, firstVisibleIndex, lastVisibleIndex));
    }

    public Task RaiseRefreshRequestedAsync() => RefreshRequestedAsync?.Invoke() ?? Task.CompletedTask;

    public void ScrollToPosition(int position) =>
        ScrollToPositionRequested?.Invoke(this, new NativeMobileFileScrollToEventArgs(Math.Max(0, position)));

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var factory = AppServices.NativeMobileFileListFactory;
        if (factory is null)
            return base.CreateNativeControlCore(parent);

        _nativeHandle = factory.CreateControl(parent, this);
        return _nativeHandle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try
        {
            AppServices.NativeMobileFileListFactory?.DestroyControl(control);
        }
        finally
        {
            _nativeHandle = null;
            base.DestroyNativeControlCore(control);
        }
    }
}

public sealed class NativeMobileFileItemEventArgs(DriveItemModel item) : EventArgs
{
    public DriveItemModel Item { get; } = item;
}

public sealed class NativeMobileFileScrollEventArgs(bool isScrolling, int firstVisibleIndex, int lastVisibleIndex) : EventArgs
{
    public bool IsScrolling { get; } = isScrolling;
    public int FirstVisibleIndex { get; } = firstVisibleIndex;
    public int LastVisibleIndex { get; } = lastVisibleIndex;
}

public sealed class NativeMobileFileScrollToEventArgs(int position) : EventArgs
{
    public int Position { get; } = position;
}
