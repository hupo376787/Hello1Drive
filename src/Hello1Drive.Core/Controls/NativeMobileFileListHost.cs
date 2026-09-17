using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
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
    private readonly Dictionary<string, NativeFolderViewportAnchor> _folderViewportAnchors = new(StringComparer.Ordinal);
    private long _folderViewportRestoreVersion;

    public event EventHandler<NativeMobileFileItemEventArgs>? ItemTapped;
    public event EventHandler<NativeMobileFileItemEventArgs>? ItemLongPressed;
    public event EventHandler<NativeMobileFileScrollEventArgs>? ScrollStateChanged;
    public event EventHandler? HostStateChanged;
    public event EventHandler<NativeMobileFileScrollToEventArgs>? ScrollToPositionRequested;
    public event EventHandler? FloatingUploadRequested;
    public event EventHandler<NativeFloatingUploadPositionEventArgs>? FloatingUploadPositionChanged;
    public Func<Task>? RefreshRequestedAsync { get; set; }

    public MainViewModel? ViewModel => _viewModel;
    public IReadOnlyList<string> SelectedIds => _selectedIds;
    public bool SelectionMode => _selectionMode;
    public int LastFirstVisibleIndex { get; private set; }
    public bool FloatingUploadVisible => _viewModel?.ShowFloatingUploadButton == true;
    public double FloatingUploadX => Math.Clamp(_viewModel?.Settings.FloatingUploadX ?? 0.94, 0, 1);
    public double FloatingUploadY => Math.Clamp(_viewModel?.Settings.FloatingUploadY ?? 0.90, 0, 1);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var vm = DataContext as MainViewModel;
        if (ReferenceEquals(_viewModel, vm))
            return;

        if (_viewModel is not null)
        {
            _viewModel.FolderNavigating -= ViewModel_FolderNavigating;
            _viewModel.FolderLoaded -= ViewModel_FolderLoaded;
        }

        _viewModel = vm;
        if (_viewModel is not null)
        {
            _viewModel.FolderNavigating += ViewModel_FolderNavigating;
            _viewModel.FolderLoaded += ViewModel_FolderLoaded;
        }

        HostStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ViewModel_FolderNavigating(object? sender, FolderNavigationEventArgs e)
    {
        if (!ReferenceEquals(sender, _viewModel) || _viewModel is null || _viewModel.MobileItems.Count == 0)
            return;

        _folderViewportAnchors[e.FolderKey] = NativeFolderViewportAnchorResolver.Capture(
            _viewModel.MobileItems,
            LastFirstVisibleIndex);
    }

    private void ViewModel_FolderLoaded(object? sender, FolderNavigationEventArgs e)
    {
        if (!ReferenceEquals(sender, _viewModel) || _viewModel is null || _viewModel.MobileItems.Count == 0)
            return;

        // Same-folder cloud refresh deliberately keeps the existing native list alive until its
        // incremental diff arrives. Re-scrolling here would manufacture a navigation jump.
        if (e.Reason == FolderNavigationReason.Refresh)
            return;

        var target = 0;
        if (e.ShouldRestoreScroll && _folderViewportAnchors.TryGetValue(e.FolderKey, out var anchor))
            target = NativeFolderViewportAnchorResolver.Resolve(_viewModel.MobileItems, anchor);

        RestoreFolderViewport(e.FolderKey, target);
    }

    private void RestoreFolderViewport(string folderKey, int position)
    {
        if (_viewModel is null || _viewModel.MobileItems.Count == 0)
            return;

        var target = Math.Clamp(position, 0, _viewModel.MobileItems.Count - 1);
        var version = unchecked(++_folderViewportRestoreVersion);

        // Correct the position before the next frame. Older MainView compatibility handlers may
        // also request a position; the two deferred passes below intentionally run after them and
        // make this stable identity+slot-delta result authoritative.
        ScrollToPosition(target);
        Dispatcher.UIThread.Post(() =>
        {
            if (!CanApplyFolderViewportRestore(version, folderKey))
                return;

            ScrollToPosition(target);
            Dispatcher.UIThread.Post(() =>
            {
                if (CanApplyFolderViewportRestore(version, folderKey))
                    ScrollToPosition(target);
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private bool CanApplyFolderViewportRestore(long version, string folderKey) =>
        version == _folderViewportRestoreVersion &&
        _viewModel is not null &&
        string.Equals(NativeFolderViewportAnchorResolver.FolderKey(_viewModel), folderKey, StringComparison.Ordinal);

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

    public void RaiseFloatingUploadRequested() => FloatingUploadRequested?.Invoke(this, EventArgs.Empty);

    public void RaiseFloatingUploadPositionChanged(double normalizedX, double normalizedY) =>
        FloatingUploadPositionChanged?.Invoke(
            this,
            new NativeFloatingUploadPositionEventArgs(
                Math.Clamp(normalizedX, 0, 1),
                Math.Clamp(normalizedY, 0, 1)));

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

public sealed class NativeFloatingUploadPositionEventArgs(double normalizedX, double normalizedY) : EventArgs
{
    public double NormalizedX { get; } = normalizedX;
    public double NormalizedY { get; } = normalizedY;
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
