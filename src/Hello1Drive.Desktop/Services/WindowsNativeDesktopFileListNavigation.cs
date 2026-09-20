namespace Hello1Drive.Desktop.Services;

internal sealed partial class WindowsNativeDesktopFileListController
{
    private const int LVM_SCROLL_NAVIGATION = LVM_FIRST + 20;
    private long _lastAppliedHostScrollRequestVersion;

    /// <summary>
    /// MainView's native host owns folder navigation history. Apply its requested first-visible
    /// position only after the current SysListView32 presentation has synchronized with the VM.
    /// This keeps folder changes from reusing the child folder's current native scroll position.
    /// </summary>
    private void ApplyPendingHostScrollRequest()
    {
        var version = _host.ScrollRequestVersion;
        if (_disposed || version <= 0 || version == _lastAppliedHostScrollRequestVersion)
            return;

        _lastAppliedHostScrollRequestVersion = version;
        if (_viewModel is null || _viewModel.VirtualItems.Count == 0 || ListHandle == 0)
            return;

        var position = Math.Clamp(_host.RequestedScrollPosition, 0, _viewModel.VirtualItems.Count - 1);

        // ENSUREVISIBLE first brings a far-away row into the viewport. LVM_SCROLL then aligns that
        // row with the top edge instead of accepting ListView's nearest-edge placement (which is
        // commonly about one viewport away when navigating back to a parent folder).
        SendMessage(ListHandle, LVM_ENSUREVISIBLE, (nint)position, 0);
        for (var pass = 0; pass < 2; pass++)
        {
            if (!TryGetNativeItemRect(position, out var rect))
                break;

            var deltaY = rect.top;
            if (Math.Abs(deltaY) <= 1)
                break;

            SendMessage(ListHandle, LVM_SCROLL_NAVIGATION, 0, (nint)deltaY);
        }

        ClampNativeIconScrollToContent();
        ReportScrollPosition();
        QueueVisibleThumbnails(allowNetwork: !_scrolling);
        InvalidateVisibleItems();
    }
}
