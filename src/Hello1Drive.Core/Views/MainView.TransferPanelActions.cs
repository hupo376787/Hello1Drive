using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Views;

public partial class MainView
{
    private Button? _finishedTransferActionButton;
    private MainViewModel? _finishedTransferActionViewModel;
    private bool _finishedTransferActionHooked;
    private bool _preparingPersistedFailedRetries;
    private MainViewModel? _stableFolderLoadedViewModel;

    // The old mobile restore path remembered only an integer row index. That is not a stable
    // position once a folder receives a cloud diff while we are inside a child folder. Keep the
    // first visible OneDrive item ID as the primary anchor and use the saved index only as fallback.
    private readonly Dictionary<string, string> _nativeFolderScrollAnchorIds = new(StringComparer.Ordinal);
    private MainViewModel? _nativeScrollAnchorViewModel;

    private Button? _mobileSelectionDeleteButton;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Loaded += TransferActions_Loaded;
        Unloaded += TransferActions_Unloaded;
    }

    private void TransferActions_Loaded(object? sender, RoutedEventArgs e)
    {
        // MainView_Loaded is registered by the constructor before this handler. Its synchronous
        // pre-await section has therefore already subscribed the default FolderLoaded handler.
        // Replace just that handler with the guarded version below before initialization finishes.
        AttachStableFolderLoadedHandler();
        AttachNativeScrollAnchorHandler();
        AttachFinishedTransferAction();
        PolishMobileSelectionActionBar();

        if (DataContext is MainViewModel { IsAuthenticated: true } vm)
            _ = PreparePersistedFailedTransferRetriesAsync(vm);
    }

    private void TransferActions_Unloaded(object? sender, RoutedEventArgs e)
    {
        DetachFinishedTransferAction();
        DetachStableFolderLoadedHandler();
        DetachNativeScrollAnchorHandler();

        if (_mobileSelectionDeleteButton is not null)
            _mobileSelectionDeleteButton.Click -= MobileSelectionDeleteDialog_Click;
        _mobileSelectionDeleteButton = null;

        if (DataContext is MainViewModel vm)
        {
            // The original unload path intentionally handled timers/native controls, but it left
            // these long-lived VM subscriptions attached and kept _loaded=true. If Android removes
            // and later reattaches the Avalonia visual tree while backgrounding/recreating the
            // Activity, the native list could stay destroyed and the old handlers could be doubled.
            vm.PropertyChanged -= ViewModel_PropertyChanged;
            vm.FolderNavigating -= Vm_FolderNavigating;
            vm.FolderLoaded -= Vm_FolderLoaded;
            vm.FolderItemsIncrementalChanging -= Vm_FolderItemsIncrementalChanging;
            vm.FolderItemsIncrementalChanged -= Vm_FolderItemsIncrementalChanged;
        }

        // Allow a real visual-tree reattach to rebuild the native host and reconnect the VM once.
        _loaded = false;
    }

    private void AttachStableFolderLoadedHandler()
    {
        if (DataContext is not MainViewModel vm)
            return;
        if (ReferenceEquals(_stableFolderLoadedViewModel, vm))
            return;

        DetachStableFolderLoadedHandler();

        // LoadCurrentFolderAsync's same-folder force-remote path intentionally keeps the existing
        // slots visible until a complete metadata diff is ready. It still raises FolderLoaded early,
        // though, which made MainView restore scroll and call RefreshNativePresentation even though
        // nothing had changed yet. Replace that presentation handler with one that ignores only this
        // early revalidation signal; real navigation/loading still flows through Vm_FolderLoaded.
        vm.FolderLoaded -= Vm_FolderLoaded;
        vm.FolderLoaded += Vm_FolderLoadedStable;
        _stableFolderLoadedViewModel = vm;
    }

    private void DetachStableFolderLoadedHandler()
    {
        if (_stableFolderLoadedViewModel is not { } vm)
            return;

        vm.FolderLoaded -= Vm_FolderLoadedStable;
        _stableFolderLoadedViewModel = null;
    }

    private void AttachNativeScrollAnchorHandler()
    {
        if (!UsesNativeMobileFileList || DataContext is not MainViewModel vm)
            return;
        if (ReferenceEquals(_nativeScrollAnchorViewModel, vm))
            return;

        DetachNativeScrollAnchorHandler();
        vm.FolderNavigating += CaptureNativeFolderScrollAnchor;
        _nativeScrollAnchorViewModel = vm;
    }

    private void DetachNativeScrollAnchorHandler()
    {
        if (_nativeScrollAnchorViewModel is not { } vm)
            return;

        vm.FolderNavigating -= CaptureNativeFolderScrollAnchor;
        _nativeScrollAnchorViewModel = null;
    }

    private void CaptureNativeFolderScrollAnchor(object? sender, FolderNavigationEventArgs e)
    {
        if (!UsesNativeMobileFileList || sender is not MainViewModel vm || _nativeMobileFileListHost is null)
            return;

        var slots = vm.MobileItems;
        if (slots.Count == 0)
            return;

        var index = Math.Clamp(_nativeMobileFileListHost.LastFirstVisibleIndex, 0, slots.Count - 1);
        string? anchorId = null;

        // The first visible slot is normally loaded. If it is a temporary virtual placeholder,
        // search the nearest loaded slot rather than throwing away the stable OneDrive identity.
        for (var i = index; i < Math.Min(slots.Count, index + 12); i++)
        {
            if (slots[i].Item is { Id.Length: > 0 } item)
            {
                anchorId = item.Id;
                break;
            }
        }

        if (anchorId is null)
        {
            for (var i = index - 1; i >= Math.Max(0, index - 12); i--)
            {
                if (slots[i].Item is { Id.Length: > 0 } item)
                {
                    anchorId = item.Id;
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(anchorId))
            _nativeFolderScrollAnchorIds[e.FolderKey] = anchorId;
    }

    private void Vm_FolderLoadedStable(object? sender, FolderNavigationEventArgs e)
    {
        if (sender is MainViewModel vm &&
            e.Reason == FolderNavigationReason.Refresh &&
            vm.MobileItems.Any(static slot => slot.Item is not null) &&
            vm.StatusText.Contains("正在同步", StringComparison.Ordinal))
        {
            // Same-folder refresh/revalidation deliberately keeps the current scene authoritative.
            // Do not ask the native/desktop surface to present the exact same slots a second time;
            // the later incremental diff will notify only changed rows and preserve the viewport.
            return;
        }

        var resolvedNativePosition = -1;
        if (sender is MainViewModel loadedVm && UsesNativeMobileFileList && e.ShouldRestoreScroll &&
            _nativeFolderScrollAnchorIds.TryGetValue(e.FolderKey, out var anchorId))
        {
            for (var i = 0; i < loadedVm.MobileItems.Count; i++)
            {
                if (string.Equals(loadedVm.MobileItems[i].Id, anchorId, StringComparison.Ordinal))
                {
                    resolvedNativePosition = i;
                    _nativeFolderScrollPositions[e.FolderKey] = i;
                    break;
                }
            }
        }

        // Sort is intentionally not suppressed here. Its existing FolderLoaded semantics reset the
        // viewport to the top; treating Sort like a background Refresh would leave users anchored
        // in the middle of a newly ordered folder.
        Vm_FolderLoaded(sender, e);

        if (sender is not MainViewModel currentVm || !UsesNativeMobileFileList || !e.ShouldRestoreScroll)
            return;

        if (resolvedNativePosition < 0 &&
            _nativeFolderScrollPositions.TryGetValue(e.FolderKey, out var savedPosition))
        {
            resolvedNativePosition = savedPosition;
        }

        if (resolvedNativePosition < 0)
            return;

        var position = resolvedNativePosition;

        // Vm_FolderLoaded historically called ScrollToPosition and then RefreshNativePresentation.
        // Some RecyclerView/UICollectionView reload paths apply their layout after that first scroll
        // and move the list again. Re-apply the stable anchor once after the native presentation has
        // completed. This is the key fix for "back to parent but not at the old position".
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(DataContext, currentVm) ||
                !string.Equals(CurrentMobileFolderKey(currentVm), e.FolderKey, StringComparison.Ordinal))
            {
                return;
            }

            _nativeMobileFileListHost?.ScrollToPosition(position);
        }, DispatcherPriority.Loaded);
    }

    private static string CurrentMobileFolderKey(MainViewModel vm) =>
        string.IsNullOrWhiteSpace(vm.CurrentFolderId) ? "__ROOT__" : vm.CurrentFolderId;

    private void PolishMobileSelectionActionBar()
    {
        if (!IsMobilePlatform)
            return;

        var actionStrip = EnumerateControls(MobileSelectionActionBar)
            .OfType<StackPanel>()
            .FirstOrDefault(panel =>
                panel.Orientation == Orientation.Horizontal &&
                panel.Children.OfType<Button>().Count() >= 4);
        if (actionStrip is null)
            return;

        var buttons = actionStrip.Children.OfType<Button>().ToArray();
        var shareButton = buttons.FirstOrDefault(button => ButtonContainsText(button, "分享"));
        var deleteButton = buttons.FirstOrDefault(button => ButtonContainsText(button, "删除"));

        // Sharing is intentionally the least common bulk action. Keep the destructive/local file
        // actions together and move Share to the far right as requested.
        if (shareButton is not null && !ReferenceEquals(actionStrip.Children.LastOrDefault(), shareButton))
        {
            actionStrip.Children.Remove(shareButton);
            actionStrip.Children.Add(shareButton);
        }

        if (deleteButton is null)
            return;

        // Replace the XAML page-style delete confirmation with a platform-native alert. Because
        // this is another partial of MainView we can unsubscribe the original private handler
        // directly instead of layering a second routed click on top of it.
        deleteButton.Click -= MobileSelectionDelete_Click;
        deleteButton.Click -= MobileSelectionDeleteDialog_Click;
        deleteButton.Click += MobileSelectionDeleteDialog_Click;
        _mobileSelectionDeleteButton = deleteButton;
    }

    private static bool ButtonContainsText(Button button, string expected)
    {
        if (button.Content is string text && string.Equals(text, expected, StringComparison.Ordinal))
            return true;

        return EnumerateControls(button)
            .OfType<TextBlock>()
            .Any(textBlock => string.Equals(textBlock.Text, expected, StringComparison.Ordinal));
    }

    private async void MobileSelectionDeleteDialog_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var selected = vm.SelectedItemsSnapshot.ToArray();
        if (selected.Length == 0)
            return;

        var title = selected.Length == 1 ? "删除项目" : $"删除 {selected.Length} 个项目";
        var message = selected.Length == 1
            ? $"确定删除“{selected[0].Name}”吗？此操作会将项目移入 OneDrive 回收站。"
            : $"确定删除已选择的 {selected.Length} 个项目吗？这些项目会移入 OneDrive 回收站。";

        var confirmation = AppServices.PlatformConfirmationService;
        if (confirmation is null)
        {
            // Non-mobile/fallback heads keep the existing Avalonia confirmation behavior.
            vm.ShowConfirmation(
                title,
                message,
                async () => await DeleteMobileSelectionWithoutReloadAsync(vm, selected),
                useBusy: false);
            return;
        }

        var accepted = await confirmation.ConfirmAsync(title, message, "删除", "取消");
        if (!accepted)
            return;

        await DeleteMobileSelectionWithoutReloadAsync(vm, selected);
    }

    private async Task DeleteMobileSelectionWithoutReloadAsync(
        MainViewModel vm,
        IReadOnlyList<DriveItemModel> selected)
    {
        if (_mobileSelectionDeleteButton is { } deleteButton)
            deleteButton.IsEnabled = false;

        var deletedIds = new List<string>(selected.Count);
        var failures = new List<string>();

        try
        {
            foreach (var item in selected)
            {
                try
                {
                    await AppServices.OneDrive.DeleteAsync(item.Id);
                    AppServices.FileCache.Invalidate(item.Id);
                    AppServices.ThumbnailCache.Invalidate(item.Id);
                    deletedIds.Add(item.Id);
                }
                catch (Exception ex)
                {
                    failures.Add($"{item.Name}：{ex.Message}");
                }
            }

            if (deletedIds.Count > 0)
            {
                // The successful rows disappear through the same stable-slot incremental path used
                // by cloud reconciliation. Do not clear the folder and do not call
                // LoadCurrentFolderAsync/RefreshCurrentFolderAsync here.
                vm.RemoveCurrentFolderItemsIncrementally(deletedIds);
            }

            ClearListSelections();

            if (failures.Count == 0)
            {
                vm.ErrorMessage = null;
                vm.StatusText = deletedIds.Count == 1
                    ? "已移入 OneDrive 回收站"
                    : $"已删除 {deletedIds.Count} 个项目";
            }
            else
            {
                vm.ErrorMessage = string.Join(Environment.NewLine, failures.Take(3));
                vm.StatusText = deletedIds.Count > 0
                    ? $"已删除 {deletedIds.Count} 项，{failures.Count} 项失败"
                    : "删除失败";
            }
        }
        finally
        {
            if (_mobileSelectionDeleteButton is { } button)
                button.IsEnabled = true;
        }
    }

    private void AttachFinishedTransferAction()
    {
        if (_finishedTransferActionHooked || DataContext is not MainViewModel vm)
            return;

        // Prefer the generated command as the locator. On a visual-tree reattach our previous
        // local Command=null override can still be present, so also recognize the two footer labels.
        var button = this.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Command, vm.ClearFinishedTransfersCommand) ||
                candidate.Content is string text && (text == "清理完成" || text == "重试失败"));
        if (button is null)
            return;

        _finishedTransferActionHooked = true;
        _finishedTransferActionButton = button;
        _finishedTransferActionViewModel = vm;

        // Prevent Button.OnClick from executing ClearFinishedTransfersCommand before our retry
        // handler gets a chance to inspect the failed rows. This also guarantees a failed transfer
        // can never disappear merely because the user pressed the footer action.
        button.Command = null;
        button.Click += FinishedTransferActionButton_Click;
        vm.PropertyChanged += FinishedTransferActionViewModel_PropertyChanged;
        UpdateFinishedTransferActionButton();
    }

    private void DetachFinishedTransferAction()
    {
        if (_finishedTransferActionButton is not null)
            _finishedTransferActionButton.Click -= FinishedTransferActionButton_Click;
        if (_finishedTransferActionViewModel is not null)
            _finishedTransferActionViewModel.PropertyChanged -= FinishedTransferActionViewModel_PropertyChanged;

        _finishedTransferActionButton = null;
        _finishedTransferActionViewModel = null;
        _finishedTransferActionHooked = false;
    }

    private void FinishedTransferActionViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_finishedTransferActionViewModel is not { } vm)
            return;

        if (e.PropertyName == nameof(MainViewModel.IsAuthenticated))
        {
            if (vm.IsAuthenticated)
                _ = PreparePersistedFailedTransferRetriesAsync(vm);
            return;
        }

        // RaiseTransferSummary publishes both of these whenever a transfer changes state.
        if (e.PropertyName is nameof(MainViewModel.TransferSummaryText) or nameof(MainViewModel.ActiveTransferCount))
            UpdateFinishedTransferActionButton();
    }

    /// <summary>
    /// Failed rows are deliberately persisted, but the original restart path only prepared
    /// Waiting/Running rows. Recreate RetryAction for persisted Failed rows as well so the footer's
    /// "重试失败" action keeps working after an app/process restart instead of becoming a dead end.
    /// </summary>
    private async Task PreparePersistedFailedTransferRetriesAsync(MainViewModel vm)
    {
        if (_preparingPersistedFailedRetries || !vm.IsAuthenticated)
            return;

        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null)
            return;

        _preparingPersistedFailedRetries = true;
        try
        {
            foreach (var transfer in vm.Transfers
                         .Where(static x => x.State == TransferState.Failed && x.RetryAction is null && x.ResumeInfo is not null)
                         .OrderBy(static x => x.StartedAt)
                         .ToArray())
            {
                var resume = transfer.ResumeInfo!;
                if (string.IsNullOrWhiteSpace(resume.AccountId) ||
                    !string.Equals(resume.AccountId, vm.CurrentAccountId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Func<Task>? action = resume.Kind switch
                {
                    TransferResumeKind.UploadFile => CreateResumeUploadAction(vm, provider, transfer, resume),
                    TransferResumeKind.DownloadFile => CreateResumeDownloadFileAction(vm, provider, transfer, resume),
                    TransferResumeKind.DownloadToFolder => CreateResumeDownloadFolderAction(vm, provider, transfer, resume),
                    TransferResumeKind.CacheFile => CreateResumeCacheAction(vm, transfer, resume),
                    _ => null
                };

                if (action is not null)
                    vm.MarkTransferResumePrepared(transfer, action);
            }

            await vm.FlushTransferPersistenceAsync();
        }
        catch
        {
            // A bookmark/provider can become unavailable after an OS restart. Keep the Failed row
            // visible rather than clearing it; its existing message explains the failure state.
        }
        finally
        {
            _preparingPersistedFailedRetries = false;
            UpdateFinishedTransferActionButton();
        }
    }

    private void UpdateFinishedTransferActionButton()
    {
        if (_finishedTransferActionButton is not { } button ||
            _finishedTransferActionViewModel is not { } vm)
        {
            return;
        }

        var hasFailed = vm.Transfers.Any(static transfer => transfer.State == TransferState.Failed);
        button.Content = hasFailed ? "重试失败" : "清理完成";

        // Failed work is retried as one deliberate batch after the current queue becomes idle.
        // The normal cleanup action remains available while other transfers are still running.
        button.IsEnabled = !hasFailed || vm.ActiveTransferCount == 0;
    }

    private async void FinishedTransferActionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_finishedTransferActionViewModel is not { } vm ||
            _finishedTransferActionButton is not { } button)
        {
            return;
        }

        if (vm.Transfers.Any(static transfer => transfer.State == TransferState.Failed && transfer.RetryAction is null))
            await PreparePersistedFailedTransferRetriesAsync(vm);

        var failed = vm.Transfers
            .Where(static transfer => transfer.State == TransferState.Failed)
            .OrderBy(static transfer => transfer.StartedAt)
            .ToArray();

        if (failed.Length == 0)
        {
            if (vm.ClearFinishedTransfersCommand.CanExecute(null))
                vm.ClearFinishedTransfersCommand.Execute(null);
            UpdateFinishedTransferActionButton();
            return;
        }

        // Keep every failed row visible until its own retry succeeds. Retry sequentially to avoid
        // turning a recovered network connection into an uncontrolled burst of file streams.
        button.IsEnabled = false;
        try
        {
            foreach (var transfer in failed)
            {
                if (transfer.State != TransferState.Failed || transfer.RetryAction is null)
                    continue;

                await vm.RetryTransferAsync(transfer);
            }
        }
        finally
        {
            UpdateFinishedTransferActionButton();
        }
    }
}
