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

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Loaded += TransferActions_Loaded;
        Unloaded += TransferActions_Unloaded;
    }

    private void TransferActions_Loaded(object? sender, RoutedEventArgs e)
    {
        AttachFinishedTransferAction();
    }

    private void TransferActions_Unloaded(object? sender, RoutedEventArgs e)
    {
        DetachFinishedTransferAction();
    }

    private void AttachFinishedTransferAction()
    {
        if (_finishedTransferActionHooked || DataContext is not MainViewModel vm)
            return;

        // The XAML button intentionally keeps the generated command as its locator. Once the
        // visual tree is loaded, replace that one binding with the smarter dual-purpose action:
        // retry failed rows first; only when no failure remains does it clean completed rows.
        var button = this.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Command, vm.ClearFinishedTransfersCommand));
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
        // RaiseTransferSummary publishes both of these whenever a transfer changes state.
        if (e.PropertyName is nameof(MainViewModel.TransferSummaryText) or nameof(MainViewModel.ActiveTransferCount))
            UpdateFinishedTransferActionButton();
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
