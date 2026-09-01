using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Hello1Drive.Controls;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Views;

public partial class MainView
{
    private NativeDesktopFileListHost? _nativeDesktopFileListHost;
    private MainViewModel? _nativeDesktopHookedViewModel;
    private Button? _desktopPreviewPreviousButton;
    private Button? _desktopPreviewNextButton;

    private void InitializeDesktopExperienceEnhancements()
    {
        if (IsMobilePlatform)
            return;

        EqualizeDesktopDestinationButtons();
        InitializeDesktopPreviewEdgeNavigation();
        InitializeNativeDesktopFileList();
        AttachNativeDesktopViewModelEvents();
    }

    private void DisposeDesktopExperienceEnhancements()
    {
        if (IsMobilePlatform)
            return;

        PreviewImageViewport.PointerMoved -= DesktopPreviewImageViewport_PointerMoved;
        PreviewImageViewport.PointerExited -= DesktopPreviewImageViewport_PointerExited;

        if (_desktopPreviewPreviousButton is not null)
            _desktopPreviewPreviousButton.Click -= DesktopPreviewPreviousButton_Click;
        if (_desktopPreviewNextButton is not null)
            _desktopPreviewNextButton.Click -= DesktopPreviewNextButton_Click;

        if (_nativeDesktopFileListHost is not null)
        {
            _nativeDesktopFileListHost.SelectionChanged -= NativeDesktopFileList_SelectionChanged;
            _nativeDesktopFileListHost.ItemDoubleTapped -= NativeDesktopFileList_ItemDoubleTapped;
            _nativeDesktopFileListHost.ItemContextRequested -= NativeDesktopFileList_ItemContextRequested;
        }

        DetachNativeDesktopViewModelEvents();
    }

    private void EqualizeDesktopDestinationButtons()
    {
        if (IsMobilePlatform)
            return;

        var cancel = EnumerateControls(MobileDestinationOverlay)
            .OfType<Button>()
            .FirstOrDefault(button => ButtonContainsText(button, "取消"));
        if (cancel is null)
            return;

        const double actionWidth = 120;
        cancel.Width = actionWidth;
        MobileDestinationConfirmButton.Width = actionWidth;
        cancel.HorizontalContentAlignment = HorizontalAlignment.Center;
        MobileDestinationConfirmButton.HorizontalContentAlignment = HorizontalAlignment.Center;
    }

    private void InitializeDesktopPreviewEdgeNavigation()
    {
        if (IsMobilePlatform || _desktopPreviewPreviousButton is not null)
            return;

        _desktopPreviewPreviousButton = CreateDesktopPreviewEdgeButton(
            "上一张",
            HorizontalAlignment.Left,
            "M10.5,2 L4,7 L10.5,12");
        _desktopPreviewNextButton = CreateDesktopPreviewEdgeButton(
            "下一张",
            HorizontalAlignment.Right,
            "M3.5,2 L10,7 L3.5,12");

        _desktopPreviewPreviousButton.Click += DesktopPreviewPreviousButton_Click;
        _desktopPreviewNextButton.Click += DesktopPreviewNextButton_Click;
        PreviewImageViewport.Children.Add(_desktopPreviewPreviousButton);
        PreviewImageViewport.Children.Add(_desktopPreviewNextButton);
        PreviewImageViewport.PointerMoved += DesktopPreviewImageViewport_PointerMoved;
        PreviewImageViewport.PointerExited += DesktopPreviewImageViewport_PointerExited;
    }

    private static Button CreateDesktopPreviewEdgeButton(string toolTip, HorizontalAlignment alignment, string geometry)
    {
        var path = new Path
        {
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            Stroke = Brushes.White,
            StrokeThickness = 1.9,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse(geometry)
        };

        var button = new Button
        {
            Width = 48,
            Height = 48,
            Padding = new Thickness(14),
            Margin = alignment == HorizontalAlignment.Left ? new Thickness(14, 0, 0, 0) : new Thickness(0, 0, 14, 0),
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(178, 24, 24, 24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(24),
            Content = path,
            IsVisible = false,
            ZIndex = 60
        };
        ToolTip.SetTip(button, toolTip);
        return button;
    }

    private void DesktopPreviewImageViewport_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_desktopPreviewPreviousButton is null || _desktopPreviewNextButton is null ||
            DataContext is not MainViewModel { IsImagePreview: true })
        {
            return;
        }

        var width = PreviewImageViewport.Bounds.Width;
        if (width <= 1)
            return;

        var x = e.GetPosition(PreviewImageViewport).X;
        var edgeWidth = Math.Clamp(width * 0.14, 84, 150);
        _desktopPreviewPreviousButton.IsVisible = x <= edgeWidth;
        _desktopPreviewNextButton.IsVisible = x >= width - edgeWidth;
    }

    private void DesktopPreviewImageViewport_PointerExited(object? sender, PointerEventArgs e) =>
        HideDesktopPreviewEdgeButtons();

    private void HideDesktopPreviewEdgeButtons()
    {
        if (_desktopPreviewPreviousButton is not null)
            _desktopPreviewPreviousButton.IsVisible = false;
        if (_desktopPreviewNextButton is not null)
            _desktopPreviewNextButton.IsVisible = false;
    }

    private void DesktopPreviewPreviousButton_Click(object? sender, RoutedEventArgs e)
    {
        PreviewPrevious_Click(sender, e);
        e.Handled = true;
    }

    private void DesktopPreviewNextButton_Click(object? sender, RoutedEventArgs e)
    {
        PreviewNext_Click(sender, e);
        e.Handled = true;
    }

    private void InitializeNativeDesktopFileList()
    {
        if (IsMobilePlatform || AppServices.NativeDesktopFileListFactory is null || _nativeDesktopFileListHost is not null)
            return;

        if (DesktopVirtualScrollViewer.Parent is not Grid desktopGrid)
            return;

        var host = new NativeDesktopFileListHost
        {
            DataContext = DataContext,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRow(host, 1);

        host.SelectionChanged += NativeDesktopFileList_SelectionChanged;
        host.ItemDoubleTapped += NativeDesktopFileList_ItemDoubleTapped;
        host.ItemContextRequested += NativeDesktopFileList_ItemContextRequested;

        desktopGrid.Children.Add(host);
        _nativeDesktopFileListHost = host;

        // Keep the existing Avalonia virtual surface as the non-Windows fallback, but do not leave
        // two scroll engines alive on Windows. SysListView32 now owns wheel input and scrolling.
        DesktopVirtualScrollViewer.IsVisible = false;
        host.IsVisible = true;
        Dispatcher.UIThread.Post(host.RefreshNativePresentation, DispatcherPriority.Loaded);
    }

    private void AttachNativeDesktopViewModelEvents()
    {
        if (_nativeDesktopFileListHost is null || DataContext is not MainViewModel vm ||
            ReferenceEquals(_nativeDesktopHookedViewModel, vm))
        {
            return;
        }

        DetachNativeDesktopViewModelEvents();
        vm.PropertyChanged += NativeDesktopViewModel_PropertyChanged;
        vm.FolderLoaded += NativeDesktopViewModel_FolderLoaded;
        vm.FolderItemsIncrementalChanged += NativeDesktopViewModel_FolderItemsIncrementalChanged;
        _nativeDesktopHookedViewModel = vm;
    }

    private void DetachNativeDesktopViewModelEvents()
    {
        if (_nativeDesktopHookedViewModel is not { } vm)
            return;
        vm.PropertyChanged -= NativeDesktopViewModel_PropertyChanged;
        vm.FolderLoaded -= NativeDesktopViewModel_FolderLoaded;
        vm.FolderItemsIncrementalChanged -= NativeDesktopViewModel_FolderItemsIncrementalChanged;
        _nativeDesktopHookedViewModel = null;
    }

    private void NativeDesktopViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ViewMode) or nameof(MainViewModel.SelectedThemeText) or
            nameof(MainViewModel.TransparentFileItemBackground))
        {
            _nativeDesktopFileListHost?.RefreshNativePresentation();
        }
    }

    private void NativeDesktopViewModel_FolderLoaded(object? sender, FolderNavigationEventArgs e) =>
        _nativeDesktopFileListHost?.RefreshNativePresentation();

    private void NativeDesktopViewModel_FolderItemsIncrementalChanged(object? sender, EventArgs e) =>
        _nativeDesktopFileListHost?.RefreshNativePresentation();

    private void NativeDesktopFileList_SelectionChanged(object? sender, NativeDesktopSelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        _desktopSelectedIds.Clear();
        foreach (var id in e.ItemIds)
            _desktopSelectedIds.Add(id);
        _desktopSelectionAnchorId = e.ItemIds.LastOrDefault();
        ApplyDesktopSelection(vm);
    }

    private async void NativeDesktopFileList_ItemDoubleTapped(object? sender, NativeDesktopFileItemEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        _contextItem = e.Item;
        _desktopSelectedIds.Clear();
        _desktopSelectedIds.Add(e.Item.Id);
        _desktopSelectionAnchorId = e.Item.Id;
        ApplyDesktopSelection(vm);
        await OpenDriveItemAsync(vm, e.Item);
    }

    private void NativeDesktopFileList_ItemContextRequested(object? sender, NativeDesktopFileItemEventArgs e)
    {
        if (DataContext is not MainViewModel vm || _nativeDesktopFileListHost is null)
            return;

        _contextItem = e.Item;
        if (!_desktopSelectedIds.Contains(e.Item.Id))
        {
            _desktopSelectedIds.Clear();
            _desktopSelectedIds.Add(e.Item.Id);
            _desktopSelectionAnchorId = e.Item.Id;
            ApplyDesktopSelection(vm);
        }

        var menu = GetOrCreateDesktopFileItemContextMenu();
        if (_desktopOpenWebMenuItem is not null)
            _desktopOpenWebMenuItem.IsVisible = e.Item.HasWebUrl;
        menu.Open(_nativeDesktopFileListHost);
    }
}
