using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Hello1Drive.Models;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Views;

public partial class MainView
{
    private Button? _finishedTransferActionButton;
    private MainViewModel? _finishedTransferActionViewModel;
    private bool _finishedTransferActionHooked;
    private bool _preparingPersistedFailedRetries;
    private MainViewModel? _stableFolderLoadedViewModel;

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
        AttachFinishedTransferAction();

        if (DataContext is MainViewModel { IsAuthenticated: true } vm)
            _ = PreparePersistedFailedTransferRetriesAsync(vm);
    }

    private void TransferActions_Unloaded(object? sender, RoutedEventArgs e)
    {
        DetachFinishedTransferAction();
        DetachStableFolderLoadedHandler();

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

        // Sort is intentionally not suppressed here. Its existing FolderLoaded semantics reset the
        // viewport to the top; treating Sort like a background Refresh would leave users anchored
        // in the middle of a newly ordered folder.
        Vm_FolderLoaded(sender, e);
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
