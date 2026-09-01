using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Views;

public partial class MainView
{
    private Button? _mobileSelectionRenameButton;
    private bool _desktopSelectionContextActionsAdded;
    private Border? _desktopDestinationDialogChrome;
    private bool _desktopDestinationOverlayHooksAttached;

    private void InitializeSelectionActionEnhancements()
    {
        if (IsMobilePlatform)
        {
            EnsureMobileSelectionRenameButton();
        }
        else
        {
            EnsureDesktopSelectionContextActions();
            EnsureDesktopDestinationDialogPresentation();
            InitializeDesktopExperienceEnhancements();
        }
    }

    private void DisposeSelectionActionEnhancements()
    {
        if (_mobileSelectionRenameButton is not null)
            _mobileSelectionRenameButton.Click -= MobileSelectionRename_Click;
        _mobileSelectionRenameButton = null;

        if (!IsMobilePlatform)
            DisposeDesktopExperienceEnhancements();

        if (_desktopDestinationOverlayHooksAttached)
        {
            MobileDestinationOverlay.PointerPressed -= DesktopDestinationOverlay_PointerPressed;
            MobileDestinationOverlay.SizeChanged -= DesktopDestinationOverlay_SizeChanged;
            _desktopDestinationOverlayHooksAttached = false;
        }
    }

    private void EnsureMobileSelectionRenameButton()
    {
        var actionStrip = EnumerateControls(MobileSelectionActionBar)
            .OfType<StackPanel>()
            .FirstOrDefault(panel =>
                panel.Orientation == Avalonia.Layout.Orientation.Horizontal &&
                panel.Children.OfType<Button>().Any(button => ButtonContainsText(button, "删除")));
        if (actionStrip is null)
            return;

        var existingRename = actionStrip.Children
            .OfType<Button>()
            .FirstOrDefault(button => ButtonContainsText(button, "重命名"));
        if (existingRename is not null)
        {
            existingRename.Click -= MobileSelectionRename_Click;
            existingRename.Click += MobileSelectionRename_Click;
            _mobileSelectionRenameButton = existingRename;
            return;
        }

        var deleteButton = actionStrip.Children
            .OfType<Button>()
            .FirstOrDefault(button => ButtonContainsText(button, "删除"));
        if (deleteButton is null)
            return;

        var renameButton = new Button
        {
            Padding = new Thickness(10, 6),
            Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = "重命名" }
                }
            }
        };
        renameButton.Click += MobileSelectionRename_Click;

        var deleteIndex = actionStrip.Children.IndexOf(deleteButton);
        actionStrip.Children.Insert(Math.Min(actionStrip.Children.Count, deleteIndex + 1), renameButton);
        _mobileSelectionRenameButton = renameButton;
    }

    private void MobileSelectionRename_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var selected = vm.SelectedItemsSnapshot.ToArray();
        if (selected.Length == 0)
            return;

        vm.BeginSelectionRename(selected);
        e.Handled = true;
    }

    private void EnsureDesktopSelectionContextActions()
    {
        if (_desktopSelectionContextActionsAdded || IsMobilePlatform)
            return;

        var menu = GetOrCreateDesktopFileItemContextMenu();
        var items = menu.ItemsSource?.Cast<object>().ToList() ?? [];
        if (items.OfType<MenuItem>().Any(item => string.Equals(item.Header?.ToString(), "复制到", StringComparison.Ordinal)))
        {
            _desktopSelectionContextActionsAdded = true;
            return;
        }

        var copyTo = new MenuItem { Header = "复制到" };
        copyTo.Click += DesktopSelectionCopyTo_Click;
        var moveTo = new MenuItem { Header = "移动到" };
        moveTo.Click += DesktopSelectionMoveTo_Click;
        var share = new MenuItem { Header = "分享" };
        share.Click += DesktopSelectionShare_Click;

        // Put destination/share commands with the other file operations, immediately before Rename.
        var renameIndex = items.FindIndex(item => item is MenuItem menuItem &&
            string.Equals(menuItem.Header?.ToString(), "重命名", StringComparison.Ordinal));
        if (renameIndex < 0)
            renameIndex = items.Count;

        items.Insert(renameIndex++, copyTo);
        items.Insert(renameIndex++, moveTo);
        items.Insert(renameIndex, share);
        menu.ItemsSource = items;
        _desktopSelectionContextActionsAdded = true;
    }

    private void EnsureDesktopDestinationDialogPresentation()
    {
        if (IsMobilePlatform)
            return;

        if (_desktopDestinationDialogChrome is null)
        {
            // MobileDestinationOverlay was originally a full-page mobile sheet. On desktop keep the
            // existing folder-browser content, but host it inside a centered modal surface and turn
            // the remaining area into a dismissible scrim.
            var content = MobileDestinationOverlay.Children.OfType<Grid>().FirstOrDefault();
            if (content is not null)
            {
                var originalBackground = MobileDestinationOverlay.Background;
                MobileDestinationOverlay.Children.Remove(content);

                _desktopDestinationDialogChrome = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = originalBackground,
                    CornerRadius = new CornerRadius(12),
                    ClipToBounds = true,
                    Child = content
                };

                MobileDestinationOverlay.Background = new SolidColorBrush(Color.FromArgb(112, 0, 0, 0));
                MobileDestinationOverlay.Children.Add(_desktopDestinationDialogChrome);
            }
        }

        if (!_desktopDestinationOverlayHooksAttached)
        {
            MobileDestinationOverlay.PointerPressed += DesktopDestinationOverlay_PointerPressed;
            MobileDestinationOverlay.SizeChanged += DesktopDestinationOverlay_SizeChanged;
            _desktopDestinationOverlayHooksAttached = true;
        }

        ApplyDesktopDestinationDialogBounds();
    }

    private void DesktopDestinationOverlay_SizeChanged(object? sender, SizeChangedEventArgs e) =>
        ApplyDesktopDestinationDialogBounds();

    private void ApplyDesktopDestinationDialogBounds()
    {
        if (IsMobilePlatform || _desktopDestinationDialogChrome is not { } chrome)
            return;

        var width = MobileDestinationOverlay.Bounds.Width;
        var height = MobileDestinationOverlay.Bounds.Height;
        if (width <= 1)
            width = Bounds.Width;
        if (height <= 1)
            height = Bounds.Height;
        if (width <= 1 || height <= 1)
            return;

        // Requested desktop size: exactly half of the app width. Keep a modest vertical margin so
        // the surface reads as a dialog instead of another full-screen page.
        chrome.Width = width * 0.5;
        chrome.Height = Math.Max(320, height * 0.84);
    }

    private void DesktopDestinationOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsMobilePlatform || !MobileDestinationOverlay.IsVisible)
            return;

        // Routed pointer events keep the original hit-test source. A click on the modal Border or
        // anything inside it therefore does nothing; only the exposed outer Grid (the scrim) closes.
        if (ReferenceEquals(e.Source, MobileDestinationOverlay))
        {
            CloseMobileDestinationPicker();
            e.Handled = true;
        }
    }

    private async void DesktopSelectionCopyTo_Click(object? sender, RoutedEventArgs e) =>
        await OpenDesktopSelectionDestinationPickerAsync(MobileDestinationOperation.Copy);

    private async void DesktopSelectionMoveTo_Click(object? sender, RoutedEventArgs e) =>
        await OpenDesktopSelectionDestinationPickerAsync(MobileDestinationOperation.Move);

    private void DesktopSelectionShare_Click(object? sender, RoutedEventArgs e)
    {
        // The existing share implementation already works on desktop: when no native share service
        // is registered it creates OneDrive links and copies the combined text to the clipboard.
        MobileSelectionShare_Click(sender, e);
    }

    private async Task OpenDesktopSelectionDestinationPickerAsync(MobileDestinationOperation operation)
    {
        if (IsMobilePlatform || operation == MobileDestinationOperation.None || DataContext is not MainViewModel vm)
            return;

        var selected = vm.SelectedItemsSnapshot.ToArray();
        if (selected.Length == 0)
            return;

        EnsureDesktopDestinationDialogPresentation();

        _mobileDestinationOperation = operation;
        _mobileDestinationPendingItems = selected;
        MobileDestinationTitle.Text = operation == MobileDestinationOperation.Move ? "移动到" : "复制到";
        MobileDestinationConfirmButton.Content = operation == MobileDestinationOperation.Move ? "移动到这里" : "复制到这里";

        // Reuse the existing OneDrive-only destination browser. On desktop it behaves as a modal
        // in-app folder chooser instead of invoking an OS local-folder picker.
        MobileDestinationOverlay.IsVisible = true;
        ApplyDesktopDestinationDialogBounds();
        Dispatcher.UIThread.Post(ApplyDesktopDestinationDialogBounds, DispatcherPriority.Loaded);
        vm.IsBusy = true;
        try
        {
            var root = await AppServices.OneDrive.GetItemMetadataAsync(null);
            if (string.IsNullOrWhiteSpace(root.Id))
                throw new InvalidOperationException("无法获取 OneDrive 根目录 ID。");

            _mobileDestinationFolderId = root.Id;
            _mobileDestinationBreadcrumbItems.Clear();
            _mobileDestinationBreadcrumbItems.Add(new BreadcrumbItem("OneDrive", root.Id));
            await NavigateMobileDestinationAsync(root.Id);
        }
        catch (OperationCanceledException)
        {
            // Closing/navigating the destination browser superseded the request.
        }
        catch (Exception ex)
        {
            if (!SuppressTransientNetworkError(vm, ex))
                vm.ErrorMessage = ex.Message;
            CloseMobileDestinationPicker();
        }
        finally
        {
            if (MobileDestinationOverlay.IsVisible)
                vm.IsBusy = false;
        }
    }
}
