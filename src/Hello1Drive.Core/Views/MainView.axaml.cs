using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hello1Drive.Models;
using Hello1Drive.Services;
using Hello1Drive.ViewModels;

namespace Hello1Drive.Views;

public partial class MainView : UserControl
{
    private static readonly string[] BackgroundImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif", ".tif", ".tiff"];

    private readonly HttpClient _httpClient = new();
    private readonly DispatcherTimer _backgroundTimer = new();
    private readonly List<IStorageFile> _localBackgroundFiles = [];
    private readonly List<DriveItemModel> _oneDriveBackgroundFiles = [];
    private Bitmap? _backgroundBitmap;
    private int _backgroundIndex;
    private bool _changingBackground;
    private bool _settingsPanelAnimating;
    private bool _loaded;
    private bool _restoredTransfersResumeStarted;
    private CancellationTokenSource? _backgroundUrlApplyCts;
    private IDisposable? _backgroundScrimBinding;
    private DriveItemModel? _contextItem;
    private bool _previewAutoFit;
    private bool _previewPanning;
    private Point _previewPanPointerStart;
    private double _previewPanStartLeft;
    private double _previewPanStartTop;
    private IEmbeddedMediaPlayerSession? _embeddedMediaSession;
    private string? _embeddedMediaPath;

    private readonly Dictionary<string, Vector> _folderScrollPositions = new(StringComparer.Ordinal);
    private readonly HashSet<ScrollViewer> _hookedScrollViewers = [];
    private TopLevel? _topLevel;

    private bool _marqueeSelecting;
    private Point _marqueeStart;
    private readonly HashSet<string> _marqueeBaseSelection = new(StringComparer.Ordinal);

    private bool _draggingFloatingUpload;
    private bool _floatingUploadMoved;
    private Point _floatingUploadPointerStart;
    private double _floatingUploadStartLeft;
    private double _floatingUploadStartTop;

    public MainView()
    {
        InitializeComponent();
        Loaded += MainView_Loaded;
        Unloaded += MainView_Unloaded;
        _backgroundTimer.Tick += BackgroundTimer_Tick;

        // ListBox can mark pointer events handled. Register on the routed event with
        // handledEventsToo so desktop marquee selection still starts on empty list space.
        FileArea.AddHandler(InputElement.PointerPressedEvent, FileArea_PointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        FileArea.AddHandler(InputElement.PointerMovedEvent, FileArea_PointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        FileArea.AddHandler(InputElement.PointerReleasedEvent, FileArea_PointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(InputElement.KeyDownEvent, MainView_KeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
    }

    private async void MainView_Loaded(object? sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            var touchSelection = SelectionMode.Multiple | SelectionMode.Toggle;
            DetailsList.SelectionMode = touchSelection;
            LargeIconList.SelectionMode = touchSelection;
            ExtraLargeIconList.SelectionMode = touchSelection;
        }

        ConfigureBackgroundHost();
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.BackRequested += TopLevel_BackRequested;
            _topLevel.AddHandler(InputElement.PointerPressedEvent, TopLevel_PointerPressed,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        }

        if (DataContext is MainViewModel vm)
        {
            vm.PropertyChanged += ViewModel_PropertyChanged;
            vm.FolderNavigating += Vm_FolderNavigating;
            vm.FolderLoaded += Vm_FolderLoaded;
            ApplyTheme(vm.Settings.ThemeMode);
            ApplyFileItemBackground(vm);
            ApplySettingsAcrylicBlur(vm.AcrylicBlurPercent);
            ApplyAppBackgroundAcrylicBlur(vm.AcrylicBlurPercent);
            await ApplyWindowBackgroundAsync();
            await vm.InitializeAsync();
            _ = TryResumePersistedTransfersAsync(vm);
            Dispatcher.UIThread.Post(PositionFloatingUploadButton, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(HookListScrollViewers, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(UpdateIconPanelSizing, DispatcherPriority.Loaded);
        }
    }


    private async void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (e.PropertyName == nameof(MainViewModel.SelectedThemeText))
        {
            // Theme switching is live. A user-selected solid background is independent from
            // the app theme, so re-apply it after the theme resources have switched.
            ApplyTheme(vm.Settings.ThemeMode);
            ApplyFileItemBackground(vm);

            if (vm.Settings.BackgroundMode is WindowBackgroundMode.Default or WindowBackgroundMode.Color)
            {
                await Dispatcher.UIThread.InvokeAsync(
                    static () => { },
                    DispatcherPriority.Background);
                await ApplyWindowBackgroundAsync();
            }
            else
            {
                UseThemeBackgroundFrost();
            }
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.TransparentFileItemBackground))
        {
            ApplyFileItemBackground(vm);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedBackgroundModeText))
        {
            await ApplyWindowBackgroundAsync();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.BackgroundColorText) && vm.IsBackgroundColorMode)
        {
            try
            {
                var color = Color.Parse(vm.BackgroundColorText);
                SetBackgroundBitmap(null);
                SetBackgroundColor(new SolidColorBrush(color), preserveSolidColorAcrossTheme: true);
            }
            catch
            {
                // Ignore partial/invalid text while the user is still typing a color.
            }
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.BackgroundUrl) && vm.IsBackgroundUrlMode)
        {
            ScheduleBackgroundUrlApply(vm);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.BackgroundIntervalMinutes) && vm.IsBackgroundFolderMode)
        {
            StartBackgroundTimer(vm.BackgroundIntervalMinutes);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.AcrylicBlurPercent))
        {
            ApplySettingsAcrylicBlur(vm.AcrylicBlurPercent);
            ApplyAppBackgroundAcrylicBlur(vm.AcrylicBlurPercent);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.PreviewKind))
        {
            _previewAutoFit = vm.IsImagePreview;
            if (_previewAutoFit)
                Dispatcher.UIThread.Post(() => FitPreviewImageToViewport(vm));
            Dispatcher.UIThread.Post(() => SyncEmbeddedMediaPlayer(vm), DispatcherPriority.Loaded);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.PreviewCachedFilePath))
        {
            Dispatcher.UIThread.Post(() => SyncEmbeddedMediaPlayer(vm), DispatcherPriority.Loaded);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsPreviewVisible))
        {
            if (vm.IsPreviewVisible)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    PreviewOverlay.Focus();
                    SyncEmbeddedMediaPlayer(vm);
                }, DispatcherPriority.Loaded);
            }
            else
            {
                _previewAutoFit = false;
                _previewPanning = false;
                DisposeEmbeddedMediaPlayer();
            }
        }

        if (e.PropertyName == nameof(MainViewModel.IsAuthenticated))
        {
            if (vm.Settings.BackgroundMode == WindowBackgroundMode.OneDriveFolder)
            {
                // A OneDrive-backed wallpaper can only be resolved after authentication.
                await ApplyWindowBackgroundAsync();
            }

            if (vm.IsAuthenticated)
                _ = TryResumePersistedTransfersAsync(vm);
        }
    }

    private void SyncEmbeddedMediaPlayer(MainViewModel vm)
    {
        if (!vm.IsPreviewVisible || vm.PreviewKind != PreviewKind.Media ||
            string.IsNullOrWhiteSpace(vm.PreviewCachedFilePath) || !File.Exists(vm.PreviewCachedFilePath))
        {
            DisposeEmbeddedMediaPlayer();
            EmbeddedMediaPlayerHost.IsVisible = false;
            ExternalMediaFallback.IsVisible = vm.IsMediaPreview;
            return;
        }

        if (_embeddedMediaSession is not null &&
            string.Equals(_embeddedMediaPath, vm.PreviewCachedFilePath, StringComparison.Ordinal))
        {
            EmbeddedMediaPlayerHost.IsVisible = true;
            ExternalMediaFallback.IsVisible = false;
            return;
        }

        DisposeEmbeddedMediaPlayer();

        try
        {
            var session = AppServices.MediaPlayerFactory?.TryCreate(vm.PreviewCachedFilePath);
            if (session is null)
            {
                EmbeddedMediaPlayerHost.IsVisible = false;
                ExternalMediaFallback.IsVisible = true;
                return;
            }

            _embeddedMediaSession = session;
            _embeddedMediaPath = vm.PreviewCachedFilePath;
            EmbeddedMediaPlayerHost.Content = session.View;
            EmbeddedMediaPlayerHost.IsVisible = true;
            ExternalMediaFallback.IsVisible = false;
        }
        catch
        {
            DisposeEmbeddedMediaPlayer();
            EmbeddedMediaPlayerHost.IsVisible = false;
            ExternalMediaFallback.IsVisible = true;
        }
    }

    private void DisposeEmbeddedMediaPlayer()
    {
        EmbeddedMediaPlayerHost.Content = null;
        _embeddedMediaSession?.Dispose();
        _embeddedMediaSession = null;
        _embeddedMediaPath = null;
    }

    private void MainView_Unloaded(object? sender, RoutedEventArgs e)
    {
        _backgroundUrlApplyCts?.Cancel();
        _backgroundUrlApplyCts = null;
        _backgroundScrimBinding?.Dispose();
        _backgroundScrimBinding = null;
        DisposeEmbeddedMediaPlayer();

        if (_topLevel is not null)
        {
            _topLevel.BackRequested -= TopLevel_BackRequested;
            _topLevel.RemoveHandler(InputElement.PointerPressedEvent, TopLevel_PointerPressed);
            _topLevel = null;
        }
    }

    private async Task TryResumePersistedTransfersAsync(MainViewModel vm)
    {
        if (_restoredTransfersResumeStarted || !vm.IsAuthenticated)
            return;

        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null)
            return;

        _restoredTransfersResumeStarted = true;
        var pending = vm.GetRestoredPendingTransfers();
        if (pending.Count == 0)
            return;

        vm.IsTransferPanelVisible = true;
        foreach (var transfer in pending)
        {
            try
            {
                await PrepareAndRunRestoredTransferAsync(vm, provider, transfer);
            }
            catch (Exception ex)
            {
                vm.MarkTransferResumeUnavailable(transfer, $"恢复失败：{ex.Message}");
            }
        }

        await vm.FlushTransferPersistenceAsync();
    }

    private async Task PrepareAndRunRestoredTransferAsync(
        MainViewModel vm,
        IStorageProvider provider,
        TransferItemModel transfer)
    {
        var resume = transfer.ResumeInfo;
        if (resume is null || resume.Kind == TransferResumeKind.None)
        {
            vm.MarkTransferResumeUnavailable(transfer, "无法恢复：旧任务没有恢复信息");
            return;
        }

        if (string.IsNullOrWhiteSpace(resume.AccountId))
        {
            vm.MarkTransferResumeUnavailable(transfer, "无法恢复：任务缺少 Microsoft 账户信息");
            return;
        }

        if (!string.Equals(resume.AccountId, vm.CurrentAccountId, StringComparison.OrdinalIgnoreCase))
        {
            vm.MarkTransferResumeUnavailable(transfer, "无法恢复：任务属于另一个 Microsoft 账户");
            return;
        }

        Func<Task>? action = resume.Kind switch
        {
            TransferResumeKind.UploadFile => CreateResumeUploadAction(vm, provider, transfer, resume),
            TransferResumeKind.DownloadFile => CreateResumeDownloadFileAction(vm, provider, transfer, resume),
            TransferResumeKind.DownloadToFolder => CreateResumeDownloadFolderAction(vm, provider, transfer, resume),
            TransferResumeKind.CacheFile => CreateResumeCacheAction(vm, transfer, resume),
            _ => null
        };

        if (action is null)
        {
            vm.MarkTransferResumeUnavailable(transfer, "无法恢复：不支持的任务类型");
            return;
        }

        vm.MarkTransferResumePrepared(transfer, action);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            transfer.State = TransferState.Failed;
            transfer.Message = $"恢复失败：{ex.Message}";
            vm.ErrorMessage = ex.Message;
        }
    }

    private static Func<Task>? CreateResumeUploadAction(
        MainViewModel vm,
        IStorageProvider provider,
        TransferItemModel transfer,
        TransferResumeInfo resume)
    {
        if (string.IsNullOrWhiteSpace(resume.StorageBookmark))
            return null;

        return async () =>
        {
            using var file = await provider.OpenFileBookmarkAsync(resume.StorageBookmark);
            if (file is null)
                throw new IOException("无法重新打开待上传的本地文件");

            await using var stream = await file.OpenReadAsync();
            await vm.UploadFileAsync(resume.TargetFolderId, transfer.FileName, stream, refreshWhenDone: true, transfer);
        };
    }

    private static Func<Task>? CreateResumeDownloadFileAction(
        MainViewModel vm,
        IStorageProvider provider,
        TransferItemModel transfer,
        TransferResumeInfo resume)
    {
        if (string.IsNullOrWhiteSpace(resume.StorageBookmark) || string.IsNullOrWhiteSpace(resume.OneDriveItemId))
            return null;

        return async () =>
        {
            using var target = await provider.OpenFileBookmarkAsync(resume.StorageBookmark);
            if (target is null)
                throw new IOException("无法重新打开下载保存位置");

            var item = await AppServices.OneDrive.GetItemMetadataAsync(resume.OneDriveItemId);
            await using var stream = await target.OpenWriteAsync();
            if (stream.CanSeek)
                stream.SetLength(0);
            await vm.DownloadFileAsync(item, stream, transfer);
        };
    }

    private static Func<Task>? CreateResumeDownloadFolderAction(
        MainViewModel vm,
        IStorageProvider provider,
        TransferItemModel transfer,
        TransferResumeInfo resume)
    {
        if (string.IsNullOrWhiteSpace(resume.StorageBookmark) || string.IsNullOrWhiteSpace(resume.OneDriveItemId))
            return null;

        return async () =>
        {
            using var destinationRoot = await provider.OpenFolderBookmarkAsync(resume.StorageBookmark);
            if (destinationRoot is null)
                throw new IOException("无法重新打开下载目录");

            var item = await AppServices.OneDrive.GetItemMetadataAsync(resume.OneDriveItemId);
            IStorageFolder? leaf = null;
            IStorageFile? target = null;
            try
            {
                var parent = (IStorageFolder)destinationRoot;
                if (resume.RelativeFolderSegments.Length > 0)
                {
                    leaf = await EnsureFolderPathAsync(destinationRoot, resume.RelativeFolderSegments);
                    parent = leaf ?? destinationRoot;
                }

                var fileName = SanitizeFileName(item.Name);
                target = await FindChildFileAsync(parent, fileName) ?? await parent.CreateFileAsync(fileName);
                if (target is null)
                    throw new IOException($"无法创建文件：{fileName}");

                await using var stream = await target.OpenWriteAsync();
                if (stream.CanSeek)
                    stream.SetLength(0);
                await vm.DownloadFileAsync(item, stream, transfer);
            }
            finally
            {
                target?.Dispose();
                leaf?.Dispose();
            }
        };
    }

    private static Func<Task>? CreateResumeCacheAction(
        MainViewModel vm,
        TransferItemModel transfer,
        TransferResumeInfo resume)
    {
        if (string.IsNullOrWhiteSpace(resume.OneDriveItemId))
            return null;

        return async () =>
        {
            var item = await AppServices.OneDrive.GetItemMetadataAsync(resume.OneDriveItemId);
            await vm.ResumeCacheFileAsync(item, transfer);
        };
    }

    private void ScheduleBackgroundUrlApply(MainViewModel vm)
    {
        _backgroundUrlApplyCts?.Cancel();
        var cts = new CancellationTokenSource();
        _backgroundUrlApplyCts = cts;
        _ = ApplyBackgroundUrlAfterDelayAsync(vm, cts);
    }

    private async Task ApplyBackgroundUrlAfterDelayAsync(MainViewModel vm, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(700, cts.Token);
            if (vm.IsBackgroundUrlMode && !cts.IsCancellationRequested)
                await ApplyWindowBackgroundAsync();
        }
        catch (OperationCanceledException)
        {
            // The user is still typing; the newest value will be applied instead.
        }
        finally
        {
            if (ReferenceEquals(_backgroundUrlApplyCts, cts))
                _backgroundUrlApplyCts = null;
            cts.Dispose();
        }
    }

    private void Vm_FolderNavigating(object? sender, FolderNavigationEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var scroll = GetActiveScrollViewer(vm);
        if (scroll is not null)
            _folderScrollPositions[e.FolderKey] = scroll.Offset;
    }

    private void Vm_FolderLoaded(object? sender, FolderNavigationEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            HookListScrollViewers();
            var scroll = GetActiveScrollViewer(vm);
            if (scroll is null)
                return;

            if (e.ShouldRestoreScroll && _folderScrollPositions.TryGetValue(e.FolderKey, out var offset))
                scroll.Offset = offset;
            else
                scroll.Offset = new Vector(scroll.Offset.X, 0);
        }, DispatcherPriority.Loaded);
    }

    private ScrollViewer? GetActiveScrollViewer(MainViewModel vm)
    {
        var list = GetActiveFileList(vm);
        return list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    }

    private void HookListScrollViewers()
    {
        foreach (var list in new[] { DetailsList, LargeIconList, ExtraLargeIconList })
        {
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is not null && _hookedScrollViewers.Add(scroll))
                scroll.ScrollChanged += FileListScrollViewer_ScrollChanged;
        }
    }

    private async void FileListScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || !scroll.IsVisible || DataContext is not MainViewModel vm ||
            !vm.HasMoreItems || vm.IsLoadingMore)
            return;

        var remaining = scroll.Extent.Height - (scroll.Offset.Y + scroll.Viewport.Height);
        if (remaining <= Math.Max(240, scroll.Viewport.Height * 0.35))
            await vm.LoadMoreCurrentFolderAsync();
    }

    private void TopLevel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsTransferPanelVisible || !TransferPanel.IsVisible)
            return;

        // The transfer button itself is outside the panel. Do not perform click-away closing
        // on its PointerPressed event; otherwise the panel would close here and the button's
        // ToggleTransfersCommand would immediately open it again, making the button appear
        // to be "open only" instead of a real toggle.
        if (e.Source is Visual sourceVisual)
        {
            var clickedButton = sourceVisual as Button
                ?? sourceVisual.GetVisualAncestors().OfType<Button>().FirstOrDefault();
            if (clickedButton is not null && ReferenceEquals(clickedButton.Command, vm.ToggleTransfersCommand))
                return;
        }

        // Observe the whole TopLevel (including the custom desktop title bar). If the
        // pointer is outside the transfer card, close it without marking the event handled,
        // so the user's click can still perform its normal action underneath.
        var point = e.GetPosition(TransferPanel);
        var bounds = TransferPanel.Bounds;
        if (point.X < 0 || point.Y < 0 || point.X > bounds.Width || point.Y > bounds.Height)
            vm.IsTransferPanelVisible = false;
    }

    private async void TopLevel_BackRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (vm.IsPreviewVisible)
        {
            vm.ClosePreviewCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (vm.IsSettingsPanelVisible)
        {
            await CloseSettingsPanelAsync();
            e.Handled = true;
            return;
        }

        if (vm.IsTransferPanelVisible)
        {
            vm.IsTransferPanelVisible = false;
            e.Handled = true;
            return;
        }

        if (vm.Breadcrumbs.Count > 1)
        {
            vm.GoBackCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void MainView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsPreviewVisible)
            return;

        if (e.Key == Key.Escape)
        {
            vm.ClosePreviewCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Keep normal caret navigation inside the text editor.
        if (vm.IsTextPreview)
            return;

        if (e.Key is Key.Left or Key.PageUp)
        {
            vm.PreviewPreviousCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is Key.Right or Key.PageDown)
        {
            vm.PreviewNextCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && vm.IsImagePreview)
        {
            vm.ToggleSlideshowCommand.Execute(null);
            e.Handled = true;
        }
    }

    public void RequestCloseConfirmation()
    {
        if (DataContext is MainViewModel vm)
            vm.RequestCloseConfirmation();
    }

    private void CancelAppCloseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CancelCloseConfirmation();
    }

    private async void ConfirmAppCloseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsCloseConfirmVisible = false;
            await vm.PersistSettingsAsync();
            await vm.FlushTransferPersistenceAsync();
        }

        if (TopLevel.GetTopLevel(this) is MainWindow window)
            window.ConfirmClose();
    }

    public async Task ToggleSettingsPanelAsync()
    {
        if (DataContext is not MainViewModel vm || _settingsPanelAnimating)
            return;

        if (vm.IsSettingsPanelVisible)
            await CloseSettingsPanelAsync();
        else
            await OpenSettingsPanelAsync();
    }

    public async Task OpenSettingsPanelAsync()
    {
        if (DataContext is not MainViewModel vm || vm.IsSettingsPanelVisible || _settingsPanelAnimating)
            return;

        _settingsPanelAnimating = true;
        try
        {
            vm.IsSettingsPanelVisible = true;
            if (SettingsPanelHost.RenderTransform is not TranslateTransform transform)
                return;

            var width = SettingsPanelHost.Bounds.Width > 1 ? SettingsPanelHost.Bounds.Width : 430;
            transform.X = width;
            for (var i = 1; i <= 12; i++)
            {
                var t = i / 12.0;
                var eased = 1 - Math.Pow(1 - t, 3);
                transform.X = width * (1 - eased);
                await Task.Delay(12);
            }
            transform.X = 0;
        }
        finally
        {
            _settingsPanelAnimating = false;
        }
    }

    public async Task CloseSettingsPanelAsync()
    {
        if (DataContext is not MainViewModel vm || !vm.IsSettingsPanelVisible || _settingsPanelAnimating)
            return;

        _settingsPanelAnimating = true;
        try
        {
            if (SettingsPanelHost.RenderTransform is not TranslateTransform transform)
            {
                vm.IsSettingsPanelVisible = false;
                return;
            }

            var width = SettingsPanelHost.Bounds.Width > 1 ? SettingsPanelHost.Bounds.Width : 430;
            for (var i = 1; i <= 10; i++)
            {
                var t = i / 10.0;
                transform.X = width * t;
                await Task.Delay(10);
            }
            transform.X = width;
            vm.IsSettingsPanelVisible = false;
        }
        finally
        {
            _settingsPanelAnimating = false;
        }
    }

    private async void SettingsMenuItem_Click(object? sender, RoutedEventArgs e) => await ToggleSettingsPanelAsync();

    private async void OpenWebMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
            await topLevel.Launcher.LaunchUriAsync(new Uri("https://onedrive.live.com/"));
    }

    private void LogoutMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.RequestLogoutCommand.Execute(null);
    }

    private async void CloseSettingsButton_Click(object? sender, RoutedEventArgs e) => await CloseSettingsPanelAsync();
    private async void SettingsBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e) => await CloseSettingsPanelAsync();
    private void OverlayContent_PointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private async void BreadcrumbButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BreadcrumbItem item } && DataContext is MainViewModel vm)
            await vm.NavigateToBreadcrumbAsync(item);
    }

    private void ItemsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && DataContext is MainViewModel vm)
        {
            var selectedItems = list.SelectedItems?.OfType<DriveItemModel>() ?? Enumerable.Empty<DriveItemModel>();
            vm.SetSelectedItems(selectedItems);
        }
    }

    private async void ItemsList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: DriveItemModel item } && DataContext is MainViewModel vm)
            await vm.OpenItemAsync(item);
    }

    private void FileItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: DriveItemModel item } control)
            return;

        _contextItem = item;
        var point = e.GetCurrentPoint(control);
        if (point.Properties.IsRightButtonPressed)
            SelectContextItem(item);
    }

    private void FileItem_Holding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started || sender is not Control { DataContext: DriveItemModel item } control)
            return;

        _contextItem = item;
        SelectContextItem(item);
        control.ContextMenu?.Open(control);
        e.Handled = true;
    }

    private async void FileContext_Open_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && GetContextItem(sender) is { } item)
            await vm.OpenItemAsync(item);
    }

    private async void FileContext_Download_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || GetContextItem(sender) is not { } item)
            return;

        SelectContextItem(item);
        await DownloadSelectedAsync(vm);
    }

    private async void FileContext_Cache_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || GetContextItem(sender) is not { } item)
            return;

        SelectContextItem(item);
        var selected = vm.SelectedItemsSnapshot;
        IEnumerable<DriveItemModel> targets = selected.Any(x => x.Id == item.Id) ? selected : new[] { item };
        await vm.CacheItemsAsync(targets);
    }

    private void FileContext_Rename_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || GetContextItem(sender) is not { } item)
            return;

        SelectContextItem(item, forceSingle: true);
        vm.BeginRenameCommand.Execute(null);
    }

    private void FileContext_Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || GetContextItem(sender) is not { } item)
            return;

        SelectContextItem(item);
        vm.BeginDeleteCommand.Execute(null);
    }

    private async void FileContext_OpenWeb_Click(object? sender, RoutedEventArgs e)
    {
        var item = GetContextItem(sender);
        if (string.IsNullOrWhiteSpace(item?.WebUrl) || !Uri.TryCreate(item.WebUrl, UriKind.Absolute, out var uri))
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
            await topLevel.Launcher.LaunchUriAsync(uri);
    }

    private DriveItemModel? GetContextItem(object? sender)
    {
        if (sender is Control { DataContext: DriveItemModel item })
            return item;
        return _contextItem;
    }

    private void SelectContextItem(DriveItemModel item, bool forceSingle = false)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var list = vm.ViewMode switch
        {
            FileViewMode.LargeIcons => LargeIconList,
            FileViewMode.ExtraLargeIcons => ExtraLargeIconList,
            _ => DetailsList
        };

        var alreadySelected = list.SelectedItems?.OfType<DriveItemModel>().Any(x => x.Id == item.Id) == true;
        if (forceSingle || !alreadySelected)
        {
            list.SelectedItems?.Clear();
            list.SelectedItem = item;
            vm.SetSelectedItems([item]);
        }
    }

    private async void ViewModeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || DataContext is not MainViewModel vm)
            return;

        if (Enum.TryParse<FileViewMode>(tag, out var mode))
        {
            DetailsList?.SelectedItems?.Clear();
            LargeIconList?.SelectedItems?.Clear();
            ExtraLargeIconList?.SelectedItems?.Clear();
            vm.SetSelectedItems([]);
            await vm.SetViewModeAsync(mode);
            Dispatcher.UIThread.Post(UpdateIconPanelSizing, DispatcherPriority.Loaded);
        }
    }

    private void SortHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && DataContext is MainViewModel vm &&
            Enum.TryParse<FileSortColumn>(button.Tag?.ToString(), true, out var column))
        {
            vm.CycleSort(column);
            e.Handled = true;
        }
    }

    private void SortMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || DataContext is not MainViewModel vm)
            return;

        var tag = menuItem.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(tag))
            return;

        var parts = tag.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Enum.TryParse<FileSortColumn>(parts[0], true, out var column) ||
            !Enum.TryParse<SortCycleState>(parts[1], true, out var state))
            return;

        vm.SetSort(column, state);
    }

    private async void ViewContextMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || DataContext is not MainViewModel vm ||
            !Enum.TryParse<FileViewMode>(tag, out var mode))
            return;

        DetailsList.SelectedItems?.Clear();
        LargeIconList.SelectedItems?.Clear();
        ExtraLargeIconList.SelectedItems?.Clear();
        vm.SetSelectedItems([]);
        await vm.SetViewModeAsync(mode);
        Dispatcher.UIThread.Post(HookListScrollViewers, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(UpdateIconPanelSizing, DispatcherPriority.Loaded);
    }

    private void FileArea_NewFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.BeginCreateFolderCommand.Execute(null);
    }

    private void FileArea_Upload_Click(object? sender, RoutedEventArgs e) => UploadButton_Click(sender, e);

    private void FileArea_Holding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started || e.Source is StyledElement { DataContext: DriveItemModel })
            return;
        FileArea.ContextMenu?.Open(FileArea);
        e.Handled = true;
    }

    private ListBox GetActiveFileList(MainViewModel vm) => vm.ViewMode switch
    {
        FileViewMode.LargeIcons => LargeIconList,
        FileViewMode.ExtraLargeIcons => ExtraLargeIconList,
        _ => DetailsList
    };

    private static bool ShouldSuppressMarqueeStart(object? source)
    {
        if (source is StyledElement { DataContext: DriveItemModel })
            return true;

        if (source is not Visual visual)
            return false;

        static bool IsInteractive(Visual candidate) => candidate is Button or TextBox or ScrollBar or Thumb;
        if (IsInteractive(visual))
            return true;

        foreach (var ancestor in visual.GetVisualAncestors())
        {
            if (ancestor is StyledElement { DataContext: DriveItemModel } || IsInteractive(ancestor))
                return true;
        }

        return false;
    }

    private void FileArea_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateIconPanelSizing();

    private void UpdateIconPanelSizing()
    {
        var availableWidth = FileArea.Bounds.Width;
        if (availableWidth <= 1)
            return;

        UpdateWrapPanelCellWidth(LargeIconList, availableWidth, preferredWidth: 152, minWidth: 136, maxWidth: 184);
        UpdateWrapPanelCellWidth(ExtraLargeIconList, availableWidth, preferredWidth: 220, minWidth: 190, maxWidth: 276);
    }

    private static void UpdateWrapPanelCellWidth(ListBox list, double availableWidth, double preferredWidth, double minWidth, double maxWidth)
    {
        var panel = list.GetVisualDescendants().OfType<WrapPanel>().FirstOrDefault();
        if (panel is null)
            return;

        // Leave a little room for the vertical scrollbar, then distribute cells evenly.
        // This keeps icon tiles close to their intended size without hard-coding a width.
        var usableWidth = Math.Max(minWidth, availableWidth - 18);
        var columns = Math.Max(1, (int)Math.Floor(usableWidth / preferredWidth));
        var cellWidth = Math.Clamp(usableWidth / columns, minWidth, maxWidth);
        panel.ItemWidth = cellWidth;
    }

    private void FileArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || DataContext is not MainViewModel vm)
            return;

        var point = e.GetCurrentPoint(FileArea);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        // The right-most strip belongs to the active ListBox scrollbar. Never begin a
        // marquee there even if a platform reports the event source as a template Border.
        var pointerPosition = e.GetPosition(FileArea);
        if (pointerPosition.X >= Math.Max(0, FileArea.Bounds.Width - 22))
            return;

        // Do not steal pointer capture from real controls. In particular the ListBox scrollbar
        // thumb must remain draggable, and header buttons must receive Click for sorting.
        if (ShouldSuppressMarqueeStart(e.Source))
            return;

        _marqueeSelecting = true;
        _marqueeStart = e.GetPosition(FileArea);
        _marqueeBaseSelection.Clear();

        var list = GetActiveFileList(vm);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            foreach (var item in list.SelectedItems?.OfType<DriveItemModel>() ?? [])
                _marqueeBaseSelection.Add(item.Id);
        }
        else
        {
            list.SelectedItems?.Clear();
            vm.SetSelectedItems([]);
        }

        SelectionMarquee.IsVisible = false;
        e.Pointer.Capture(FileArea);
    }

    private void FileArea_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_marqueeSelecting || DataContext is not MainViewModel vm)
            return;

        var currentPoint = e.GetCurrentPoint(FileArea);
        if (!currentPoint.Properties.IsLeftButtonPressed)
            return;

        var current = e.GetPosition(FileArea);
        var x = Math.Min(_marqueeStart.X, current.X);
        var y = Math.Min(_marqueeStart.Y, current.Y);
        var width = Math.Abs(current.X - _marqueeStart.X);
        var height = Math.Abs(current.Y - _marqueeStart.Y);
        if (width < 3 && height < 3)
            return;

        SelectionMarquee.IsVisible = true;
        SelectionMarquee.Width = width;
        SelectionMarquee.Height = height;
        Canvas.SetLeft(SelectionMarquee, x);
        Canvas.SetTop(SelectionMarquee, y);

        var selectionRect = new Rect(x, y, width, height);
        var list = GetActiveFileList(vm);
        foreach (var container in list.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (container.DataContext is not DriveItemModel item)
                continue;
            var origin = container.TranslatePoint(new Point(0, 0), FileArea);
            if (origin is null)
                continue;

            var itemRect = new Rect(origin.Value.X, origin.Value.Y, container.Bounds.Width, container.Bounds.Height);
            var intersects = selectionRect.Left < itemRect.Right && selectionRect.Right > itemRect.Left &&
                             selectionRect.Top < itemRect.Bottom && selectionRect.Bottom > itemRect.Top;
            container.IsSelected = intersects || _marqueeBaseSelection.Contains(item.Id);
        }

        vm.SetSelectedItems(list.SelectedItems?.OfType<DriveItemModel>() ?? []);
        e.Handled = true;
    }

    private void FileArea_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_marqueeSelecting)
            return;

        _marqueeSelecting = false;
        SelectionMarquee.IsVisible = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void FloatingActionCanvas_SizeChanged(object? sender, SizeChangedEventArgs e) => PositionFloatingUploadButton();

    private void PositionFloatingUploadButton()
    {
        if (DataContext is not MainViewModel vm)
            return;

        var width = FloatingActionCanvas.Bounds.Width;
        var height = FloatingActionCanvas.Bounds.Height;
        if (width <= 1 || height <= 1)
            return;

        var buttonWidth = FloatingUploadDragHost.Bounds.Width > 1 ? FloatingUploadDragHost.Bounds.Width : 36;
        var buttonHeight = FloatingUploadDragHost.Bounds.Height > 1 ? FloatingUploadDragHost.Bounds.Height : 36;
        var availableX = Math.Max(0, width - buttonWidth);
        var availableY = Math.Max(0, height - buttonHeight);
        var (x, y) = vm.GetFloatingUploadPosition();
        Canvas.SetLeft(FloatingUploadDragHost, x * availableX);
        Canvas.SetTop(FloatingUploadDragHost, y * availableY);
    }

    private void FloatingUploadButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(FloatingActionCanvas);
        if (e.Pointer.Type == PointerType.Mouse && !point.Properties.IsLeftButtonPressed)
            return;

        _draggingFloatingUpload = true;
        _floatingUploadMoved = false;
        _floatingUploadPointerStart = e.GetPosition(FloatingActionCanvas);
        _floatingUploadStartLeft = GetCanvasCoordinate(FloatingUploadDragHost, true, 0);
        _floatingUploadStartTop = GetCanvasCoordinate(FloatingUploadDragHost, false, 0);
        e.Pointer.Capture(sender as IInputElement ?? FloatingUploadDragHost);
        e.Handled = true;
    }

    private void FloatingUploadButton_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_draggingFloatingUpload)
            return;

        var point = e.GetCurrentPoint(FloatingActionCanvas);
        if (e.Pointer.Type == PointerType.Mouse && !point.Properties.IsLeftButtonPressed)
            return;

        var current = e.GetPosition(FloatingActionCanvas);
        var dx = current.X - _floatingUploadPointerStart.X;
        var dy = current.Y - _floatingUploadPointerStart.Y;
        if (!_floatingUploadMoved && Math.Sqrt(dx * dx + dy * dy) < 3)
            return;
        _floatingUploadMoved = true;

        var buttonWidth = FloatingUploadDragHost.Bounds.Width > 1 ? FloatingUploadDragHost.Bounds.Width : 36;
        var buttonHeight = FloatingUploadDragHost.Bounds.Height > 1 ? FloatingUploadDragHost.Bounds.Height : 36;
        var maxX = Math.Max(0, FloatingActionCanvas.Bounds.Width - buttonWidth);
        var maxY = Math.Max(0, FloatingActionCanvas.Bounds.Height - buttonHeight);
        Canvas.SetLeft(FloatingUploadDragHost, Math.Clamp(_floatingUploadStartLeft + dx, 0, maxX));
        Canvas.SetTop(FloatingUploadDragHost, Math.Clamp(_floatingUploadStartTop + dy, 0, maxY));
        e.Handled = true;
    }

    private async void FloatingUploadButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_draggingFloatingUpload)
            return;

        _draggingFloatingUpload = false;
        e.Pointer.Capture(null);
        e.Handled = true;

        if (DataContext is not MainViewModel vm)
            return;

        if (!_floatingUploadMoved)
        {
            // The overlay owns input, so synthesize the former Button click behavior.
            UploadButton_Click(FloatingUploadButton, new RoutedEventArgs());
            return;
        }

        var buttonWidth = FloatingUploadDragHost.Bounds.Width > 1 ? FloatingUploadDragHost.Bounds.Width : 36;
        var buttonHeight = FloatingUploadDragHost.Bounds.Height > 1 ? FloatingUploadDragHost.Bounds.Height : 36;
        var maxX = Math.Max(1, FloatingActionCanvas.Bounds.Width - buttonWidth);
        var maxY = Math.Max(1, FloatingActionCanvas.Bounds.Height - buttonHeight);
        var left = GetCanvasCoordinate(FloatingUploadDragHost, true, 0);
        var top = GetCanvasCoordinate(FloatingUploadDragHost, false, 0);
        await vm.SaveFloatingUploadPositionAsync(left / maxX, top / maxY);
    }

    private async void UploadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        // Capture the destination before the picker opens. Navigation can continue while
        // the batch uploads, but every job must stay bound to this original OneDrive folder.
        var targetFolderId = vm.CurrentFolderId;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要上传的文件",
            AllowMultiple = true
        });
        if (files.Count == 0)
            return;

        // Register the whole batch before the first byte is uploaded, so the transfer
        // panel immediately represents all selected files. Keep a storage bookmark so
        // unfinished uploads can be reopened on the next launch.
        var jobs = new List<UploadJob>();
        foreach (var file in files)
        {
            var transfer = vm.RegisterTransfer(file.Name, TransferDirection.Upload);
            string? bookmark = null;
            try { bookmark = await file.SaveBookmarkAsync(); } catch { }
            vm.SetTransferResumeInfo(transfer, new TransferResumeInfo
            {
                AccountId = vm.CurrentAccountId,
                Kind = TransferResumeKind.UploadFile,
                TargetFolderId = targetFolderId,
                StorageBookmark = bookmark
            });
            jobs.Add(new UploadJob(file, transfer));
        }

        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            var refreshWhenDone = i == jobs.Count - 1;
            job.Transfer.RetryAction = () => UploadStorageFileAsync(vm, targetFolderId, job.File, job.Transfer, refreshWhenDone: true);
            await UploadStorageFileAsync(vm, targetFolderId, job.File, job.Transfer, refreshWhenDone);
        }
    }

    private static async Task UploadStorageFileAsync(
        MainViewModel vm,
        string? targetFolderId,
        IStorageFile file,
        TransferItemModel transfer,
        bool refreshWhenDone)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            await vm.UploadFileAsync(targetFolderId, file.Name, stream, refreshWhenDone, transfer);
            if (transfer.State == TransferState.Completed)
            {
                transfer.RetryAction = null;
                file.Dispose();
            }
        }
        catch (Exception ex)
        {
            transfer.State = TransferState.Failed;
            transfer.Message = ex.Message;
            vm.ErrorMessage = ex.Message;
        }
    }

    private async void OpenSelectedButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { SelectedItem: { } item } vm)
            await vm.OpenItemAsync(item);
    }

    private async void DownloadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await DownloadSelectedAsync(vm);
    }

    private async Task DownloadSelectedAsync(MainViewModel vm)
    {
        var selected = vm.SelectedItemsSnapshot.ToArray();
        if (selected.Length == 0)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        // A single file keeps the familiar Save As flow.
        if (selected.Length == 1 && selected[0].IsFile)
        {
            var target = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存文件",
                SuggestedFileName = selected[0].Name
            });
            if (target is null)
                return;

            var transfer = vm.RegisterTransfer(selected[0].Name, TransferDirection.Download);
            string? targetBookmark = null;
            try { targetBookmark = await target.SaveBookmarkAsync(); } catch { }
            vm.SetTransferResumeInfo(transfer, new TransferResumeInfo
            {
                AccountId = vm.CurrentAccountId,
                Kind = TransferResumeKind.DownloadFile,
                OneDriveItemId = selected[0].Id,
                StorageBookmark = targetBookmark
            });
            transfer.RetryAction = () => DownloadToStorageFileAsync(vm, selected[0], target, transfer);
            await DownloadToStorageFileAsync(vm, selected[0], target, transfer);
            return;
        }

        var folderResults = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择下载保存位置",
            AllowMultiple = false
        });
        var destinationRoot = folderResults.FirstOrDefault();
        if (destinationRoot is null)
            return;

        string? destinationBookmark = null;
        try { destinationBookmark = await destinationRoot.SaveBookmarkAsync(); } catch { }

        // Resolve folders recursively before transfers begin. This also means a selected
        // empty folder is recreated locally even though it produces no file transfer row.
        var plans = new List<DownloadPlan>();
        var folderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in selected)
            await CollectDownloadPlanAsync(item, [], plans, folderPaths);

        foreach (var path in folderPaths.OrderBy(static x => x.Count(c => c == '/')))
        {
            var segments = SplitRelativePath(path);
            using var created = await EnsureFolderPathAsync(destinationRoot, segments);
        }

        // As with uploads, every file row is registered before transfer starts.
        foreach (var plan in plans)
        {
            plan.Transfer = vm.RegisterTransfer(plan.DisplayName, TransferDirection.Download);
            vm.SetTransferResumeInfo(plan.Transfer, new TransferResumeInfo
            {
                AccountId = vm.CurrentAccountId,
                Kind = TransferResumeKind.DownloadToFolder,
                OneDriveItemId = plan.Item.Id,
                StorageBookmark = destinationBookmark,
                RelativeFolderSegments = plan.FolderSegments.ToArray()
            });
            var captured = plan;
            plan.Transfer.RetryAction = () => DownloadPlanItemAsync(vm, destinationRoot, captured);
        }

        foreach (var plan in plans)
            await DownloadPlanItemAsync(vm, destinationRoot, plan);
    }

    private async Task CollectDownloadPlanAsync(
        DriveItemModel item,
        IReadOnlyList<string> parentSegments,
        List<DownloadPlan> plans,
        HashSet<string> folderPaths)
    {
        if (item.IsFile)
        {
            plans.Add(new DownloadPlan(item, parentSegments.ToArray()));
            return;
        }

        var next = parentSegments.Concat([SanitizeFileName(item.Name)]).ToArray();
        folderPaths.Add(string.Join("/", next));
        var children = await AppServices.OneDrive.GetChildrenAsync(item.Id);
        foreach (var child in children)
            await CollectDownloadPlanAsync(child, next, plans, folderPaths);
    }

    private static string[] SplitRelativePath(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<IStorageFolder?> EnsureFolderPathAsync(IStorageFolder root, IReadOnlyList<string> segments)
    {
        IStorageFolder current = root;
        IStorageFolder? ownedCurrent = null;

        try
        {
            foreach (var rawSegment in segments)
            {
                var segment = SanitizeFileName(rawSegment);
                var next = await FindChildFolderAsync(current, segment) ?? await current.CreateFolderAsync(segment);
                if (next is null)
                    throw new IOException($"无法创建下载目录：{segment}");

                ownedCurrent?.Dispose();
                ownedCurrent = next;
                current = next;
            }

            var result = ownedCurrent;
            ownedCurrent = null;
            return result;
        }
        finally
        {
            ownedCurrent?.Dispose();
        }
    }

    private static Task<IStorageFolder?> FindChildFolderAsync(IStorageFolder parent, string name) =>
        parent.GetFolderAsync(name);

    private static Task<IStorageFile?> FindChildFileAsync(IStorageFolder parent, string name) =>
        parent.GetFileAsync(name);

    private static async Task DownloadPlanItemAsync(MainViewModel vm, IStorageFolder destinationRoot, DownloadPlan plan)
    {
        if (plan.Transfer is null)
            return;

        IStorageFolder? leaf = null;
        IStorageFile? target = null;
        try
        {
            var parent = destinationRoot;
            if (plan.FolderSegments.Length > 0)
            {
                leaf = await EnsureFolderPathAsync(destinationRoot, plan.FolderSegments);
                parent = leaf ?? destinationRoot;
            }

            var fileName = SanitizeFileName(plan.Item.Name);
            target = await FindChildFileAsync(parent, fileName) ?? await parent.CreateFileAsync(fileName);
            if (target is null)
                throw new IOException($"无法创建文件：{fileName}");

            await using var stream = await target.OpenWriteAsync();
            if (stream.CanSeek)
                stream.SetLength(0);
            await vm.DownloadFileAsync(plan.Item, stream, plan.Transfer);
            if (plan.Transfer.State == TransferState.Completed)
                plan.Transfer.RetryAction = null;
        }
        catch (Exception ex)
        {
            plan.Transfer.State = TransferState.Failed;
            plan.Transfer.Message = ex.Message;
            vm.ErrorMessage = ex.Message;
        }
        finally
        {
            target?.Dispose();
            leaf?.Dispose();
        }
    }

    private static async Task DownloadToStorageFileAsync(
        MainViewModel vm,
        DriveItemModel item,
        IStorageFile target,
        TransferItemModel transfer)
    {
        try
        {
            await using var stream = await target.OpenWriteAsync();
            if (stream.CanSeek)
                stream.SetLength(0);
            await vm.DownloadFileAsync(item, stream, transfer);
            if (transfer.State == TransferState.Completed)
            {
                transfer.RetryAction = null;
                target.Dispose();
            }
        }
        catch (Exception ex)
        {
            transfer.State = TransferState.Failed;
            transfer.Message = ex.Message;
            vm.ErrorMessage = ex.Message;
        }
    }

    private void DownloadAllFilesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        vm.ShowConfirmation(
            "下载所有 OneDrive 文件",
            "此操作会递归下载 OneDrive 中的全部文件和文件夹，可能消耗大量网络流量和本地磁盘空间。是否继续？",
            async () => await DownloadAllOneDriveAsync(vm),
            useBusy: false);
    }

    private async Task DownloadAllOneDriveAsync(MainViewModel vm)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 OneDrive 全量下载位置",
            AllowMultiple = false
        });
        var destinationRoot = folders.FirstOrDefault();
        if (destinationRoot is null)
            return;

        string? destinationBookmark = null;
        try { destinationBookmark = await destinationRoot.SaveBookmarkAsync(); } catch { }

        var rootItems = await AppServices.OneDrive.GetChildrenAsync(null);
        var plans = new List<DownloadPlan>();
        var folderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in rootItems)
            await CollectDownloadPlanAsync(item, [], plans, folderPaths);

        foreach (var path in folderPaths.OrderBy(static x => x.Count(c => c == '/')))
        {
            using var created = await EnsureFolderPathAsync(destinationRoot, SplitRelativePath(path));
        }

        foreach (var plan in plans)
        {
            plan.Transfer = vm.RegisterTransfer(plan.DisplayName, TransferDirection.Download);
            vm.SetTransferResumeInfo(plan.Transfer, new TransferResumeInfo
            {
                AccountId = vm.CurrentAccountId,
                Kind = TransferResumeKind.DownloadToFolder,
                OneDriveItemId = plan.Item.Id,
                StorageBookmark = destinationBookmark,
                RelativeFolderSegments = plan.FolderSegments.ToArray()
            });
            var captured = plan;
            plan.Transfer.RetryAction = () => DownloadPlanItemAsync(vm, destinationRoot, captured);
        }

        foreach (var plan in plans)
            await DownloadPlanItemAsync(vm, destinationRoot, plan);
    }

    private async void RetryTransferButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TransferItemModel transfer } && DataContext is MainViewModel vm)
            await vm.RetryTransferAsync(transfer);
    }

    private void PreviewOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ClosePreviewCommand.Execute(null);
    }

    private void PreviewImage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsImagePreview)
            return;

        var point = e.GetCurrentPoint(PreviewImageViewport);
        if (e.Pointer.Type == PointerType.Mouse && !point.Properties.IsLeftButtonPressed)
            return;

        _previewPanning = true;
        _previewPanPointerStart = e.GetPosition(PreviewImageViewport);
        _previewPanStartLeft = GetCanvasCoordinate(PreviewImageElement, true,
            (PreviewImageViewport.Bounds.Width - vm.PreviewImageWidth) / 2);
        _previewPanStartTop = GetCanvasCoordinate(PreviewImageElement, false,
            (PreviewImageViewport.Bounds.Height - vm.PreviewImageHeight) / 2);
        e.Pointer.Capture(PreviewImageViewport);
        e.Handled = true;
    }

    private void PreviewImage_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_previewPanning || DataContext is not MainViewModel vm || !vm.IsImagePreview)
            return;

        var point = e.GetCurrentPoint(PreviewImageViewport);
        if (e.Pointer.Type == PointerType.Mouse && !point.Properties.IsLeftButtonPressed)
            return;

        var current = e.GetPosition(PreviewImageViewport);
        if (Math.Abs(current.X - _previewPanPointerStart.X) > 1 ||
            Math.Abs(current.Y - _previewPanPointerStart.Y) > 1)
        {
            _previewAutoFit = false;
        }
        Canvas.SetLeft(PreviewImageElement, _previewPanStartLeft + current.X - _previewPanPointerStart.X);
        Canvas.SetTop(PreviewImageElement, _previewPanStartTop + current.Y - _previewPanPointerStart.Y);
        e.Handled = true;
    }

    private void PreviewImage_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_previewPanning)
            return;
        _previewPanning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void PreviewImage_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsImagePreview)
            return;

        if (_previewAutoFit)
        {
            _previewAutoFit = false;
            vm.SetPreviewZoomToActualSize();
            Canvas.SetLeft(PreviewImageElement, (PreviewImageViewport.Bounds.Width - vm.PreviewImageWidth) / 2);
            Canvas.SetTop(PreviewImageElement, (PreviewImageViewport.Bounds.Height - vm.PreviewImageHeight) / 2);
        }
        else
        {
            _previewAutoFit = true;
            FitPreviewImageToViewport(vm);
        }

        e.Handled = true;
    }

    private void PreviewImage_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsImagePreview || Math.Abs(e.Delta.Y) < double.Epsilon)
            return;

        _previewAutoFit = false;
        var pointer = e.GetPosition(PreviewImageViewport);
        var oldZoom = Math.Max(0.01, vm.PreviewZoom);
        var oldLeft = GetCanvasCoordinate(PreviewImageElement, isLeft: true,
            (PreviewImageViewport.Bounds.Width - vm.PreviewImageWidth) / 2);
        var oldTop = GetCanvasCoordinate(PreviewImageElement, isLeft: false,
            (PreviewImageViewport.Bounds.Height - vm.PreviewImageHeight) / 2);

        var imageX = (pointer.X - oldLeft) / oldZoom;
        var imageY = (pointer.Y - oldTop) / oldZoom;

        vm.AdjustPreviewZoom(e.Delta.Y > 0 ? 1.12 : 1 / 1.12);
        var newZoom = Math.Max(0.01, vm.PreviewZoom);
        Canvas.SetLeft(PreviewImageElement, pointer.X - imageX * newZoom);
        Canvas.SetTop(PreviewImageElement, pointer.Y - imageY * newZoom);
        e.Handled = true;
    }

    private void PreviewImageViewport_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_previewAutoFit && DataContext is MainViewModel vm)
            FitPreviewImageToViewport(vm);
    }

    private void FitPreviewImageToViewport(MainViewModel vm)
    {
        if (!vm.IsImagePreview)
            return;

        var width = PreviewImageViewport.Bounds.Width;
        var height = PreviewImageViewport.Bounds.Height;
        if (width <= 1 || height <= 1)
            return;

        vm.FitPreviewImage(width, height);
        Canvas.SetLeft(PreviewImageElement, (width - vm.PreviewImageWidth) / 2);
        Canvas.SetTop(PreviewImageElement, (height - vm.PreviewImageHeight) / 2);
    }

    private static double GetCanvasCoordinate(Control control, bool isLeft, double fallback)
    {
        var value = isLeft ? Canvas.GetLeft(control) : Canvas.GetTop(control);
        return double.IsNaN(value) ? fallback : value;
    }

    private void PreviewPrevious_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.PreviewPreviousCommand.Execute(null);
    }

    private void PreviewNext_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.PreviewNextCommand.Execute(null);
    }

    private void PreviewSlideshow_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ToggleSlideshowCommand.Execute(null);
    }

    private void PreviewDetails_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.TogglePreviewDetailsCommand.Execute(null);
    }

    private async void PreviewDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { PreviewItem: { IsFile: true } item } vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var target = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存文件",
            SuggestedFileName = item.Name
        });
        if (target is null)
            return;

        var transfer = vm.RegisterTransfer(item.Name, TransferDirection.Download);
        string? targetBookmark = null;
        try { targetBookmark = await target.SaveBookmarkAsync(); } catch { }
        vm.SetTransferResumeInfo(transfer, new TransferResumeInfo
        {
            AccountId = vm.CurrentAccountId,
            Kind = TransferResumeKind.DownloadFile,
            OneDriveItemId = item.Id,
            StorageBookmark = targetBookmark
        });
        transfer.RetryAction = () => DownloadToStorageFileAsync(vm, item, target, transfer);
        await DownloadToStorageFileAsync(vm, item, target, transfer);
    }

    private async void OpenMediaButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        if (!string.IsNullOrWhiteSpace(vm.PreviewCachedFilePath) && File.Exists(vm.PreviewCachedFilePath))
        {
            try
            {
                using var storageFile = await topLevel.StorageProvider.TryGetFileFromPathAsync(vm.PreviewCachedFilePath);
                if (storageFile is not null && await topLevel.Launcher.LaunchFileAsync(storageFile))
                    return;
            }
            catch
            {
                // Sandboxed mobile/browser targets may not expose app-private paths through
                // IStorageProvider. Fall back to the OneDrive download URL below.
            }
        }

        if (!string.IsNullOrWhiteSpace(vm.PreviewMediaUrl) &&
            Uri.TryCreate(vm.PreviewMediaUrl, UriKind.Absolute, out var uri))
        {
            await topLevel.Launcher.LaunchUriAsync(uri);
        }
    }

    private async void PickLocalImageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null)
            return;

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择窗体背景图片",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });
        using var file = files.FirstOrDefault();
        if (file is null)
            return;

        var bookmark = await file.SaveBookmarkAsync();
        if (string.IsNullOrWhiteSpace(bookmark))
            return;
        vm.UpdateLocalImageSetting(bookmark, file.Name);
        await ApplyWindowBackgroundAsync();
    }

    private async void PickLocalFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null)
            return;

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择背景图片文件夹",
            AllowMultiple = false
        });
        using var folder = folders.FirstOrDefault();
        if (folder is null)
            return;

        var bookmark = await folder.SaveBookmarkAsync();
        if (string.IsNullOrWhiteSpace(bookmark))
            return;
        vm.UpdateLocalFolderSetting(bookmark, folder.Name);
        await ApplyWindowBackgroundAsync();
    }

    private async void UseCurrentOneDriveFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.UseCurrentOneDriveFolderAsBackground();
            await ApplyWindowBackgroundAsync();
        }
    }

    private void ApplyTheme(AppThemeMode mode)
    {
        if (Application.Current is null)
            return;
        Application.Current.RequestedThemeVariant = mode switch
        {
            AppThemeMode.Light => ThemeVariant.Light,
            AppThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private static void ApplyFileItemBackground(MainViewModel vm)
    {
        if (Application.Current is not { } app)
            return;

        IBrush brush;
        if (vm.TransparentFileItemBackground)
        {
            brush = Brushes.Transparent;
        }
        else
        {
            var isDark = app.ActualThemeVariant == ThemeVariant.Dark || app.RequestedThemeVariant == ThemeVariant.Dark;
            brush = new SolidColorBrush(Color.Parse(isDark ? "#26000000" : "#38FFFFFF"));
        }

        app.Resources["HelloFileItemBrush"] = brush;
    }

    private void ApplySettingsAcrylicBlur(double percent)
    {
        var normalized = Math.Clamp(double.IsFinite(percent) ? percent : 50, 0, 100);
        // 50% maps to the previous Radius=22 appearance; 0% is sharp and 100% is strongly frosted.
        SettingsAcrylicBackgroundImage.Effect = new BlurEffect
        {
            Radius = 44d * normalized / 100d
        };
    }

    private void ApplyAppBackgroundAcrylicBlur(double percent)
    {
        var normalized = Math.Clamp(double.IsFinite(percent) ? percent : 50d, 0d, 100d);
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.SetWindowBackgroundAcrylic(normalized);
            return;
        }

        var ratio = normalized / 100d;
        var radius = 28d * ratio;
        BackgroundImageLayer.Effect = radius <= 0.01 ? null : new BlurEffect { Radius = radius };
        BackgroundColorLayer.Effect = radius <= 0.01 ? null : new BlurEffect { Radius = radius };
        BackgroundScrimLayer.Opacity = 0.08 + (0.39 * ratio);
    }

    private async Task ApplyWindowBackgroundAsync()
    {
        if (DataContext is not MainViewModel vm)
            return;

        _backgroundTimer.Stop();
        _backgroundIndex = 0;
        DisposeLocalBackgroundFiles();
        _oneDriveBackgroundFiles.Clear();
        SetBackgroundBitmap(null);

        if (vm.Settings.BackgroundMode != WindowBackgroundMode.Color)
            UseThemeBackgroundFrost();

        try
        {
            switch (vm.Settings.BackgroundMode)
            {
                case WindowBackgroundMode.Color:
                    SetBackgroundColor(
                        new SolidColorBrush(Color.Parse(vm.Settings.BackgroundColor)),
                        preserveSolidColorAcrossTheme: true);
                    break;

                case WindowBackgroundMode.LocalImage:
                    await LoadLocalImageBookmarkAsync(vm.Settings.LocalImageBookmark);
                    break;

                case WindowBackgroundMode.Url:
                    await LoadBackgroundUrlAsync(vm.Settings.BackgroundUrl);
                    break;

                case WindowBackgroundMode.LocalFolder:
                    await LoadLocalFolderBackgroundsAsync(vm.Settings.LocalFolderBookmark);
                    StartBackgroundTimer(vm.Settings.BackgroundIntervalMinutes);
                    break;

                case WindowBackgroundMode.OneDriveFolder:
                    if (vm.IsAuthenticated)
                    {
                        await LoadOneDriveFolderBackgroundsAsync(vm.Settings.OneDriveBackgroundFolderId);
                        StartBackgroundTimer(vm.Settings.BackgroundIntervalMinutes);
                    }
                    else
                    {
                        ApplyDefaultBackgroundColor();
                    }
                    break;

                default:
                    ApplyDefaultBackgroundColor();
                    break;
            }
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = $"背景加载失败：{ex.Message}";
            SetBackgroundBitmap(null);
            ApplyDefaultBackgroundColor();
        }
    }

    private void ApplyDefaultBackgroundColor()
    {
        var app = Application.Current;
        var isDarkTheme = app?.RequestedThemeVariant == ThemeVariant.Dark ||
                          (app?.RequestedThemeVariant == ThemeVariant.Default &&
                           app.ActualThemeVariant == ThemeVariant.Dark);
        SetBackgroundColor(new SolidColorBrush(Color.Parse(isDarkTheme ? "#202124" : "#F7F7F8")));
    }

    private void ConfigureBackgroundHost()
    {
        // Desktop has a custom title bar. Its wallpaper must be owned by MainWindow so
        // one UniformToFill image spans the title bar and content without a crop seam.
        if (TopLevel.GetTopLevel(this) is MainWindow)
        {
            BackgroundColorLayer.IsVisible = false;
            BackgroundImageLayer.IsVisible = false;
            BackgroundScrimLayer.IsVisible = false;
        }
        else
        {
            BackgroundColorLayer.IsVisible = true;
            BackgroundScrimLayer.IsVisible = true;
        }
    }

    private void SetBackgroundColor(IBrush brush, bool preserveSolidColorAcrossTheme = false)
    {
        SettingsAcrylicBase.Background = brush;
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.SetWindowBackgroundColor(brush, preserveSolidColorAcrossTheme);
            BackgroundColorLayer.IsVisible = false;
            return;
        }

        BackgroundColorLayer.Background = brush;
        BackgroundColorLayer.IsVisible = true;

        if (preserveSolidColorAcrossTheme)
        {
            // Keep the frost layer on the same user color. Otherwise its DynamicResource
            // changes with Light/Dark and visually replaces/tints the selected HEX color.
            _backgroundScrimBinding?.Dispose();
            _backgroundScrimBinding = null;
            BackgroundScrimLayer.Background = brush;
        }
        else
        {
            UseThemeBackgroundFrost();
        }
    }

    private void UseThemeBackgroundFrost()
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.UseThemeBackgroundFrost();
            return;
        }

        _backgroundScrimBinding?.Dispose();
        _backgroundScrimBinding = BackgroundScrimLayer.Bind(
            Border.BackgroundProperty,
            this.GetResourceObservable("SystemControlBackgroundAltHighBrush"));
    }

    private async Task LoadLocalImageBookmarkAsync(string bookmark)
    {
        if (string.IsNullOrWhiteSpace(bookmark))
            return;
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null)
            return;
        using var file = await provider.OpenFileBookmarkAsync(bookmark);
        if (file is null)
            return;
        await using var stream = await file.OpenReadAsync();
        SetBackgroundBitmap(new Bitmap(stream));
    }

    private async Task LoadBackgroundUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;
        var bytes = await _httpClient.GetByteArrayAsync(uri);
        using var stream = new MemoryStream(bytes);
        SetBackgroundBitmap(new Bitmap(stream));
    }

    private async Task LoadLocalFolderBackgroundsAsync(string bookmark)
    {
        if (string.IsNullOrWhiteSpace(bookmark))
            return;
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null)
            return;
        using var folder = await provider.OpenFolderBookmarkAsync(bookmark);
        if (folder is null)
            return;

        await foreach (var item in folder.GetItemsAsync())
        {
            if (item is IStorageFile file && IsBackgroundImage(file.Name))
            {
                _localBackgroundFiles.Add(file);
            }
            else
            {
                item.Dispose();
            }
        }
        if (_localBackgroundFiles.Count > 0)
            await ShowLocalBackgroundAsync(_localBackgroundFiles[0]);
    }

    private async Task LoadOneDriveFolderBackgroundsAsync(string folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId) || DataContext is not MainViewModel)
            return;
        var parentId = folderId == "__ROOT__" ? null : folderId;
        var items = await AppServices.OneDrive.GetChildrenAsync(parentId);
        _oneDriveBackgroundFiles.AddRange(items.Where(x => x.IsImage));
        if (_oneDriveBackgroundFiles.Count > 0)
            await ShowOneDriveBackgroundAsync(_oneDriveBackgroundFiles[0]);
    }

    private void StartBackgroundTimer(double minutes)
    {
        var hasSlides = _localBackgroundFiles.Count > 1 || _oneDriveBackgroundFiles.Count > 1;
        if (!hasSlides)
            return;
        _backgroundTimer.Interval = TimeSpan.FromMinutes(Math.Max(0.1, minutes));
        _backgroundTimer.Start();
    }

    private async void BackgroundTimer_Tick(object? sender, EventArgs e)
    {
        if (_changingBackground)
            return;
        _changingBackground = true;
        try
        {
            if (_localBackgroundFiles.Count > 0)
            {
                _backgroundIndex = (_backgroundIndex + 1) % _localBackgroundFiles.Count;
                await ShowLocalBackgroundAsync(_localBackgroundFiles[_backgroundIndex]);
            }
            else if (_oneDriveBackgroundFiles.Count > 0)
            {
                _backgroundIndex = (_backgroundIndex + 1) % _oneDriveBackgroundFiles.Count;
                await ShowOneDriveBackgroundAsync(_oneDriveBackgroundFiles[_backgroundIndex]);
            }
        }
        finally
        {
            _changingBackground = false;
        }
    }

    private async Task ShowLocalBackgroundAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        SetBackgroundBitmap(new Bitmap(stream));
    }

    private async Task ShowOneDriveBackgroundAsync(DriveItemModel item)
    {
        await using var memory = new MemoryStream();
        await AppServices.OneDrive.DownloadFileAsync(item.Id, memory);
        memory.Position = 0;
        SetBackgroundBitmap(new Bitmap(memory));
    }

    private void SetBackgroundBitmap(Bitmap? bitmap)
    {
        var previous = _backgroundBitmap;
        _backgroundBitmap = bitmap;

        SettingsAcrylicBackgroundImage.Source = bitmap;
        SettingsAcrylicBackgroundImage.IsVisible = bitmap is not null;

        if (bitmap is not null)
            UseThemeBackgroundFrost();

        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.SetWindowBackgroundImage(bitmap);
            BackgroundImageLayer.Source = null;
            BackgroundImageLayer.IsVisible = false;
        }
        else
        {
            BackgroundImageLayer.Source = bitmap;
            BackgroundImageLayer.IsVisible = bitmap is not null;
        }

        previous?.Dispose();
    }


    private void DisposeLocalBackgroundFiles()
    {
        foreach (var file in _localBackgroundFiles)
            file.Dispose();
        _localBackgroundFiles.Clear();
    }

    private static bool IsBackgroundImage(string name) =>
        BackgroundImageExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "download" : name;
    }
    private sealed record UploadJob(IStorageFile File, TransferItemModel Transfer);

    private sealed class DownloadPlan
    {
        public DownloadPlan(DriveItemModel item, string[] folderSegments)
        {
            Item = item;
            FolderSegments = folderSegments;
        }

        public DriveItemModel Item { get; }
        public string[] FolderSegments { get; }
        public TransferItemModel? Transfer { get; set; }
        public string DisplayName => FolderSegments.Length == 0
            ? Item.Name
            : string.Join("/", FolderSegments.Append(Item.Name));
    }

}
