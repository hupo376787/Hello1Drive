using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hello1Drive.Controls;
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
    private bool _settingsOpenedFromMobileProfile;
    private bool _loaded;
    private bool _restoredTransfersResumeStarted;
    private CancellationTokenSource? _backgroundUrlApplyCts;
    private ContextMenu? _desktopFileItemContextMenu;
    private MenuItem? _desktopOpenWebMenuItem;
    private IDisposable? _backgroundScrimBinding;
    private IDisposable? _mobileProfileScrimBinding;
    private IDisposable? _mobileTransferScrimBinding;
    private IDisposable? _mobileSettingsScrimBinding;
    private DriveItemModel? _contextItem;
    private bool _previewAutoFit;
    private bool _previewPanning;
    private Point _previewPanPointerStart;
    private double _previewPanStartLeft;
    private double _previewPanStartTop;
    private readonly PinchGestureRecognizer _previewPinchGestureRecognizer = new();
    private readonly ScrollGestureRecognizer _previewScrollGestureRecognizer = new()
    {
        CanHorizontallyScroll = false,
        CanVerticallyScroll = false,
        IsScrollInertiaEnabled = false,
        ScrollStartDistance = 6
    };
    private double _previewLastPinchScale = 1.0;
    private bool _previewPinching;
    private bool _mobilePreviewZoomMode;
    private bool _syncingMobileImageCarousel;
    private string[] _mobileCarouselImageIds = [];
    private CancellationTokenSource? _previewZoomAnimationCts;
    private const double PreviewDoubleTapAnimationMilliseconds = 220;

    // Mobile preview long-press opens the same action surface as the ⋮ button.
    // Movement/pinch/swipe cancels the pending hold so it never competes with
    // Carousel paging or zoomed-image panning.
    private readonly DispatcherTimer _previewLongPressTimer = new() { Interval = TimeSpan.FromMilliseconds(560) };
    private bool _previewLongPressPending;
    private Point _previewLongPressStart;
    private IEmbeddedMediaOverlayController? _mobilePreviewActionsOverlayController;

    private IEmbeddedMediaPlayerSession? _embeddedMediaSession;
    private string? _embeddedMediaPath;

    private readonly Dictionary<string, Vector> _folderScrollPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nativeFolderScrollPositions = new(StringComparer.Ordinal);
    private TopLevel? _topLevel;

    private bool _marqueeSelecting;
    private bool _mobileSelectionMode;
    private readonly HashSet<string> _mobileSelectedIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DriveItemModel> _mobileSelectedItemsById = new(StringComparer.Ordinal);
    private readonly HashSet<string> _desktopSelectedIds = new(StringComparer.Ordinal);
    private string? _desktopSelectionAnchorId;
    private Point _marqueeStart;
    private readonly HashSet<string> _marqueeBaseSelection = new(StringComparer.Ordinal);

    private double _lastMobileScrollOffset;
    private double _mobileChromeCollapseOffset;
    private double _mobileTopToolbarExpandedHeight;
    private double _mobileActionToolbarExpandedHeight;
    private ScrollViewer? _lastMobileScrollViewer;
    private DateTime _mobileScrollLastActivityUtc;
    private DateTime _mobileThumbnailLastQueueUtc;
    private int _mobileThumbnailIdleRecoveryVersion;
    private bool _mobileScrollGestureActive;
    private int _mobileScrollGestureId = -1;
    private int _mobileScrollCompletionVersion;
    // ScrollGestureEnded is the primary stop signal. This timer is only a conservative fallback
    // for programmatic scrolling or a platform gesture that never emits the terminal event.
    private readonly DispatcherTimer _mobileScrollIdleTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _desktopScrollIdleTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private DateTime _desktopScrollLastActivityUtc;
    private int _desktopThumbnailIdleRecoveryVersion;
    private bool _mobileFolderNavigationInProgress;
    private bool _mobileRefreshInProgress;

    // Mobile long-press selection is implemented explicitly instead of Avalonia's Holding recognizer.
    // This lets us cancel the pending long-press as soon as a touch starts becoming a scroll, which
    // prevents accidental selections during slow drags/flings.
    private const ulong MobileLongPressThresholdMilliseconds = 620;
    private readonly DispatcherTimer _mobileLongPressTimer = new() { Interval = TimeSpan.FromMilliseconds(MobileLongPressThresholdMilliseconds) };
    private DriveItemModel? _mobileLongPressItem;
    private Point _mobileLongPressStart;
    private ulong _mobileLongPressStartTimestamp;
    private bool _mobileLongPressStartedWhileSelectionMode;
    private bool _mobileLongPressMoved;
    private ScrollViewer? _mobileLongPressScrollViewer;
    private double _mobileLongPressStartScrollOffsetY;
    private bool _mobileLongPressSelectionActivated;
    private string? _suppressNextMobileTapItemId;

    private readonly AvaloniaList<DriveItemModel> _mobileDestinationFolders = [];
    private readonly AvaloniaList<BreadcrumbItem> _mobileDestinationBreadcrumbItems = [];
    private IReadOnlyList<DriveItemModel> _mobileDestinationPendingItems = Array.Empty<DriveItemModel>();
    private string? _mobileDestinationFolderId;
    private MobileDestinationOperation _mobileDestinationOperation;
    private CancellationTokenSource? _mobileDestinationNavigationCts;
    private long _mobileDestinationNavigationVersion;

    private enum MobileDestinationOperation
    {
        None,
        Move,
        Copy
    }

    private static bool IsMobilePlatform => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
    private static bool UsesNativeMobileFileList => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
    private NativeMobileFileListHost? _nativeMobileFileListHost;

    private bool _draggingFloatingUpload;
    private bool _floatingUploadMoved;
    private Point _floatingUploadPointerStart;
    private double _floatingUploadStartLeft;
    private double _floatingUploadStartTop;

    public MainView()
    {
        InitializeComponent();

        // Android RecyclerView and iOS UICollectionView live on platform-native view layers above
        // Avalonia. Render the upload FAB inside the same native root on both platforms so file
        // cells can never cover it; desktop keeps the Avalonia FAB.
        if (UsesNativeMobileFileList)
            FloatingActionCanvas.IsVisible = false;

        MobileDestinationFolderList.ItemsSource = _mobileDestinationFolders;
        MobileDestinationBreadcrumbs.ItemsSource = _mobileDestinationBreadcrumbItems;
        Loaded += MainView_Loaded;
        Unloaded += MainView_Unloaded;
        _backgroundTimer.Tick += BackgroundTimer_Tick;
        _mobileScrollIdleTimer.Tick += MobileScrollIdleTimer_Tick;
        _desktopScrollIdleTimer.Tick += DesktopScrollIdleTimer_Tick;
        _mobileLongPressTimer.Tick += MobileLongPressTimer_Tick;
        _previewLongPressTimer.Tick += PreviewLongPressTimer_Tick;

        // File item surfaces can mark pointer events handled. Register on the routed event with
        // handledEventsToo so desktop marquee selection still starts on empty repeater space.
        FileArea.AddHandler(InputElement.PointerPressedEvent, FileArea_PointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        FileArea.AddHandler(InputElement.PointerMovedEvent, FileArea_PointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        FileArea.AddHandler(InputElement.PointerReleasedEvent, FileArea_PointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(InputElement.KeyDownEvent, MainView_KeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        if (IsMobilePlatform)
        {

            // The file-area ContextMenu is a desktop-only surface. Detaching it on mobile
            // prevents Android's press-and-hold context gesture from opening a desktop menu
            // when the user holds an empty part of the file list.
            FileArea.ContextMenu = null;

            // Android and iOS use platform-native virtualized file lists, so Avalonia list gesture
            // recognizers are not attached on either native mobile path.
            if (!UsesNativeMobileFileList)
            {
                foreach (var scroll in new[] { MobileDetailsScrollViewer, MobileLargeIconScrollViewer, MobileExtraLargeIconScrollViewer })
                {
                    scroll.AddHandler(InputElement.ScrollGestureEvent, MobileFileList_ScrollGesture,
                        RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
                    scroll.AddHandler(InputElement.ScrollGestureInertiaStartingEvent, MobileFileList_ScrollGestureInertiaStarting,
                        RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
                    scroll.AddHandler(InputElement.ScrollGestureEndedEvent, MobileFileList_ScrollGestureEnded,
                        RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
                }
            }

            // Mobile gallery gestures: pinch zoom, one-finger pan while zoomed, and
            // horizontal swipe paging while the image is fitted to the viewport.
            PreviewImageViewport.GestureRecognizers.Add(_previewPinchGestureRecognizer);
            PreviewImageViewport.GestureRecognizers.Add(_previewScrollGestureRecognizer);
            PreviewImageViewport.AddHandler(InputElement.PinchEvent, PreviewImage_Pinch, RoutingStrategies.Bubble, true);
            PreviewImageViewport.AddHandler(InputElement.PinchEndedEvent, PreviewImage_PinchEnded, RoutingStrategies.Bubble, true);
            PreviewImageViewport.AddHandler(InputElement.ScrollGestureEvent, PreviewImage_ScrollGesture, RoutingStrategies.Bubble, true);

            // handledEventsToo is required because Carousel may mark touch events handled.
            PreviewImageViewport.AddHandler(InputElement.PointerPressedEvent, PreviewLongPress_PointerPressed,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
            PreviewImageViewport.AddHandler(InputElement.PointerMovedEvent, PreviewLongPress_PointerMoved,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
            PreviewImageViewport.AddHandler(InputElement.PointerReleasedEvent, PreviewLongPress_PointerReleased,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        }
    }

    private async void MainView_Loaded(object? sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;

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
            ApplyStartupBackgroundShell(vm);

            // Local / URL backgrounds do not depend on OneDrive authentication. Start resolving
            // them immediately so the real wallpaper is already visible when the splash fades,
            // instead of waiting several seconds for account/folder synchronization to finish.
            var startupBackgroundTask = vm.Settings.BackgroundMode == WindowBackgroundMode.OneDriveFolder
                ? Task.CompletedTask
                : ApplyWindowBackgroundAsync();

            // Start initialization immediately, but keep the splash only for a short, fixed
            // first-frame interval. With a startup snapshot the cached directory is already
            // restored behind the splash, so network synchronization must not lengthen it.
            var initializeTask = vm.InitializeAsync();
            await Task.Delay(520);
            StartupSplashOverlay.Opacity = 0;
            await Task.Delay(170);
            StartupSplashOverlay.IsVisible = false;
            UpdateNativeMobileFileListVisibility();
            await initializeTask;

            // OneDrive-folder wallpaper needs an authenticated Graph session, so resolve only
            // that mode after initialization. Other modes were already started before the splash.
            if (vm.Settings.BackgroundMode == WindowBackgroundMode.OneDriveFolder)
                _ = ApplyWindowBackgroundAsync();
            else
                _ = startupBackgroundTask;

            _ = TryResumePersistedTransfersAsync(vm);
            Dispatcher.UIThread.Post(PositionFloatingUploadButton, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(HookListScrollViewers, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(UpdateIconPanelSizing, DispatcherPriority.Loaded);
            if (IsMobilePlatform && !UsesNativeMobileFileList)
                Dispatcher.UIThread.Post(UpdateResponsiveMobileIconLayouts, DispatcherPriority.Loaded);
            if (IsMobilePlatform && !UsesNativeMobileFileList)
                Dispatcher.UIThread.Post(() => ResetMobileChrome(vm, recaptureHeights: true), DispatcherPriority.Loaded);
            if (UsesNativeMobileFileList)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateNativeMobileFileListGeometry(vm);
                    UpdateNativeMobileFileListVisibility();
                    _nativeMobileFileListHost?.RefreshNativePresentation();
                }, DispatcherPriority.Loaded);
            }
        }
    }


    private async void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (UsesNativeMobileFileList &&
            e.PropertyName is nameof(MainViewModel.IsAuthenticated) or
                nameof(MainViewModel.IsBusy) or
                nameof(MainViewModel.IsPromptVisible) or
                nameof(MainViewModel.IsLogoutConfirmVisible) or
                nameof(MainViewModel.IsCloseConfirmVisible) or
                nameof(MainViewModel.IsPreviewVisible) or
                nameof(MainViewModel.IsSettingsPanelVisible) or
                nameof(MainViewModel.IsTransferPanelVisible))
        {
            Dispatcher.UIThread.Post(UpdateNativeMobileFileListVisibility, DispatcherPriority.Loaded);
        }

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
            if (UsesNativeMobileFileList)
                _nativeMobileFileListHost?.RefreshNativePresentation();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.TransparentFileItemBackground))
        {
            ApplyFileItemBackground(vm);
            if (UsesNativeMobileFileList)
                _nativeMobileFileListHost?.RefreshNativePresentation();
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

        if (e.PropertyName == nameof(MainViewModel.ShowToolbar) && IsMobilePlatform)
        {
            FileActionToolbar.IsVisible = vm.ShowToolbar;

            if (UsesNativeMobileFileList)
            {
                // NativeControlHost does not participate in Avalonia render transforms. Keep the
                // mobile chrome fixed and let the platform-native list own the scrolling hot path.
                _mobileActionToolbarExpandedHeight = 0;
                _mobileChromeCollapseOffset = 0;
                MobileTopToolbar.RenderTransform = null;
                FileActionToolbar.RenderTransform = null;
                FileArea.RenderTransform = null;
                FileArea.Margin = new Thickness(0);
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateNativeMobileFileListGeometry(vm);
                    UpdateNativeMobileFileListVisibility();
                }, DispatcherPriority.Loaded);
                return;
            }

            if (vm.ShowToolbar)
            {
                _mobileActionToolbarExpandedHeight = 0;
                FileActionToolbar.Height = double.NaN;
                Dispatcher.UIThread.Post(() => ResetMobileChrome(vm, recaptureHeights: true), DispatcherPriority.Loaded);
            }
            else
            {
                _mobileActionToolbarExpandedHeight = 0;
                _mobileChromeCollapseOffset = Math.Min(_mobileChromeCollapseOffset, _mobileTopToolbarExpandedHeight);
                ApplyMobileChromeCollapse(vm);
            }
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.ViewMode))
        {
            if (!IsMobilePlatform)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    DesktopVirtualScrollViewer.Offset = new Vector(0, 0);
                    DesktopFileSurface.InvalidateMeasure();
                    SyncDesktopVirtualSurfaceViewport(DesktopVirtualScrollViewer);
                    if (!vm.IsDesktopListScrolling)
                        QueueRealizedDesktopThumbnails(DesktopVirtualScrollViewer, vm, allowNetwork: true);
                }, DispatcherPriority.Loaded);
                return;
            }

            if (UsesNativeMobileFileList)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateNativeMobileFileListGeometry(vm);
                    _nativeMobileFileListHost?.RefreshNativePresentation();
                }, DispatcherPriority.Loaded);
                return;
            }

            _lastMobileScrollViewer = null;
            Dispatcher.UIThread.Post(() =>
            {
                var scroll = GetActiveScrollViewer(vm);
                if (scroll is null)
                    return;
                _lastMobileScrollViewer = scroll;
                QueueVisibleMobileThumbnails(scroll, vm);
            }, DispatcherPriority.Loaded);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.PreviewKind))
        {
            CancelPreviewZoomAnimation();
            _previewAutoFit = vm.IsImagePreview;
            _previewLastPinchScale = 1.0;
            _previewPinching = false;
            if (IsMobilePlatform)
            {
                _mobilePreviewZoomMode = vm.IsImagePreview && IsCurrentPreviewGif(vm);
                Dispatcher.UIThread.Post(() =>
                {
                    SyncMobileImageCarousel(vm);
                    ApplyMobileImagePreviewMode(vm);
                }, DispatcherPriority.Loaded);
            }

            if (_previewAutoFit)
                Dispatcher.UIThread.Post(() =>
                {
                    FitPreviewImageToViewport(vm);
                    UpdatePreviewTouchGestureMode(vm);
                });
            else
                UpdatePreviewTouchGestureMode(vm);
            Dispatcher.UIThread.Post(() => SyncEmbeddedMediaPlayer(vm), DispatcherPriority.Loaded);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.PreviewItem) && IsMobilePlatform)
        {
            CancelPreviewZoomAnimation();
            if (vm.IsImagePreview)
            {
                _previewAutoFit = true;
                _mobilePreviewZoomMode = IsCurrentPreviewGif(vm);
                Dispatcher.UIThread.Post(() =>
                {
                    SyncMobileImageCarousel(vm);
                    ApplyMobileImagePreviewMode(vm);
                }, DispatcherPriority.Loaded);
            }
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.PreviewImage) && vm.IsImagePreview)
        {
            // Image-to-image carousel navigation keeps PreviewKind == Image, so the PreviewKind
            // handler does not run again. Re-fit when the newly decoded bitmap arrives; otherwise
            // LoadPreviewAsync's hand-off value can leave the visible zoom badge at 100%.
            if (_previewAutoFit && vm.PreviewImage is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_previewAutoFit || !vm.IsImagePreview || vm.PreviewImage is null)
                        return;
                    FitPreviewImageToViewport(vm);
                    UpdatePreviewTouchGestureMode(vm);
                }, DispatcherPriority.Loaded);
            }
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
                    PreviewOverlay.Opacity = 1;
                    PreviewCard.Opacity = 1;
                    PreviewOverlay.Focus();
                    SyncEmbeddedMediaPlayer(vm);
                }, DispatcherPriority.Loaded);
            }
            else
            {
                CancelPreviewZoomAnimation();
                CloseMobilePreviewActions();
                // Reset the entrance state while hidden; the next file open starts from 0 and
                // the XAML transitions fade the whole preview page in for every file type.
                PreviewOverlay.Opacity = 0;
                PreviewCard.Opacity = 0;
                _previewAutoFit = false;
                _previewPanning = false;
                _mobilePreviewZoomMode = false;
                if (IsMobilePlatform)
                {
                    MobileImageCarousel.IsSwipeEnabled = true;
                    _mobileCarouselImageIds = [];
                }
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
        _mobileScrollIdleTimer.Stop();
        _desktopScrollIdleTimer.Stop();
        _mobileLongPressTimer.Stop();
        _previewLongPressTimer.Stop();
        CloseMobileSortActions();
        CloseMobilePreviewActions();
        if (UsesNativeMobileFileList)
            DestroyNativeMobileFileListHost();
        if (DataContext is MainViewModel mobileVm)
        {
            mobileVm.SetMobileListScrolling(false);
            mobileVm.SetDesktopListScrolling(false);
        }

        _backgroundUrlApplyCts?.Cancel();
        _backgroundUrlApplyCts = null;
        _backgroundScrimBinding?.Dispose();
        _backgroundScrimBinding = null;
        _mobileProfileScrimBinding?.Dispose();
        _mobileProfileScrimBinding = null;
        _mobileTransferScrimBinding?.Dispose();
        _mobileTransferScrimBinding = null;
        _mobileSettingsScrimBinding?.Dispose();
        _mobileSettingsScrimBinding = null;
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
            if (SuppressTransientNetworkError(vm, ex))
                return;

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

        if (UsesNativeMobileFileList)
        {
            _nativeFolderScrollPositions[e.FolderKey] = _nativeMobileFileListHost?.LastFirstVisibleIndex ?? 0;
            ClearListSelections();
            vm.SetMobileListScrolling(false);
            return;
        }

        var scroll = GetActiveScrollViewer(vm);
        if (scroll is not null)
            _folderScrollPositions[e.FolderKey] = scroll.Offset;

        ClearListSelections();

        if (!IsMobilePlatform)
        {
            _desktopScrollIdleTimer.Stop();
            unchecked { _desktopThumbnailIdleRecoveryVersion++; }
            vm.SetDesktopListScrolling(false);
            return;
        }

        // The three mobile views intentionally reuse the same ScrollViewer instances when the
        // folder changes. Pulse inertia off for one dispatcher turn so momentum from the folder
        // we are leaving cannot be carried into the next data set. We don't disable hit testing
        // while a remote folder is loading, so a failed request can never leave the list inert.
        _mobileScrollIdleTimer.Stop();
        vm.SetMobileListScrolling(false);
        _lastMobileScrollViewer = null;
        CancelMobileLongPress();
        _mobileLongPressSelectionActivated = false;
        _mobileScrollGestureActive = false;
        _mobileScrollGestureId = -1;
        unchecked { _mobileScrollCompletionVersion++; }

        if (scroll is not null)
        {
            ScrollViewer.SetIsScrollInertiaEnabled(scroll, false);
            Dispatcher.UIThread.Post(
                () => ScrollViewer.SetIsScrollInertiaEnabled(scroll, true),
                DispatcherPriority.Background);
        }
    }

    private void Vm_FolderLoaded(object? sender, FolderNavigationEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            HookListScrollViewers();

            if (UsesNativeMobileFileList)
            {
                var targetPosition = e.ShouldRestoreScroll &&
                                     _nativeFolderScrollPositions.TryGetValue(e.FolderKey, out var savedPosition)
                    ? savedPosition
                    : 0;

                _nativeMobileFileListHost?.ScrollToPosition(targetPosition);
                _nativeMobileFileListHost?.RefreshNativePresentation();

                var maxBreadcrumbX = Math.Max(0, MobileBreadcrumbScrollViewer.Extent.Width - MobileBreadcrumbScrollViewer.Viewport.Width);
                MobileBreadcrumbScrollViewer.Offset = new Vector(maxBreadcrumbX, 0);
                UpdateNativeMobileFileListGeometry(vm);
                UpdateNativeMobileFileListVisibility();
                return;
            }

            if (IsMobilePlatform)
            {
                _lastMobileScrollOffset = 0;
                ResetMobileChrome(vm);
            }
            var scroll = GetActiveScrollViewer(vm);
            if (scroll is null)
                return;

            var targetOffset = e.ShouldRestoreScroll && _folderScrollPositions.TryGetValue(e.FolderKey, out var offset)
                ? offset
                : new Vector(scroll.Offset.X, 0);

            if (IsMobilePlatform)
            {
                _mobileFolderNavigationInProgress = true;
                ScrollViewer.SetIsScrollInertiaEnabled(scroll, false);
            }

            scroll.Offset = targetOffset;

            if (!IsMobilePlatform)
            {
                // Desktop no longer creates a thumbnail task for every loaded item. Wait for the
                // destination virtual surface to receive its first viewport, then queue only those
                // controls. This keeps the first scrollbar drag free of a thousands-task backlog.
                var recoveryVersion = unchecked(++_desktopThumbnailIdleRecoveryVersion);
                Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is MainViewModel currentVm &&
                        ReferenceEquals(GetActiveScrollViewer(currentVm), scroll) &&
                        !currentVm.IsDesktopListScrolling)
                    {
                        QueueRealizedDesktopThumbnails(scroll, currentVm, allowNetwork: true);
                        _ = RecoverVisibleDesktopThumbnailsAfterIdleAsync(scroll, currentVm, recoveryVersion);
                    }
                }, DispatcherPriority.Background);
            }

            if (IsMobilePlatform)
            {
                _lastMobileScrollViewer = scroll;

                // Re-apply the destination offset once after layout. This absorbs any already
                // queued ScrollChanged/inertia frame from the folder we just left. Only after
                // that fence is in place do we let the new folder accept inertial scrolling.
                Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is MainViewModel currentVm && ReferenceEquals(GetActiveScrollViewer(currentVm), scroll))
                    {
                        scroll.Offset = targetOffset;
                        ScrollViewer.SetIsScrollInertiaEnabled(scroll, true);
                        _mobileFolderNavigationInProgress = false;
                        _lastMobileScrollOffset = Math.Max(0, targetOffset.Y);
                    }
                    else
                    {
                        // Never leave a stale guard behind if another navigation/view-mode change won.
                        ScrollViewer.SetIsScrollInertiaEnabled(scroll, true);
                        _mobileFolderNavigationInProgress = false;
                    }

                    var maxBreadcrumbX = Math.Max(0, MobileBreadcrumbScrollViewer.Extent.Width - MobileBreadcrumbScrollViewer.Viewport.Width);
                    MobileBreadcrumbScrollViewer.Offset = new Vector(maxBreadcrumbX, 0);

                    // Thumbnail decode/network work is cosmetic. Let the destination layout/input
                    // fence complete first so an immediate finger drag gets the first free frame.
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (DataContext is MainViewModel latestVm &&
                            ReferenceEquals(GetActiveScrollViewer(latestVm), scroll) &&
                            !latestVm.IsMobileListScrolling)
                        {
                            QueueVisibleMobileThumbnails(scroll, latestVm);
                            QueueRealizedMobileThumbnails(scroll, latestVm);
                        }
                    }, DispatcherPriority.Background);
                }, DispatcherPriority.Loaded);
            }
        }, DispatcherPriority.Loaded);
    }

    private ScrollViewer? GetActiveScrollViewer(MainViewModel vm)
    {
        if (UsesNativeMobileFileList)
            return null;

        if (!IsMobilePlatform)
            return DesktopVirtualScrollViewer;

        return vm.ViewMode switch
        {
            FileViewMode.LargeIcons => MobileLargeIconScrollViewer,
            FileViewMode.ExtraLargeIcons => MobileExtraLargeIconScrollViewer,
            _ => MobileDetailsScrollViewer
        };
    }

    private void HookListScrollViewers()
    {
        // Intentionally empty. Let ScrollViewer handle mouse-wheel / precision-touchpad input
        // through Avalonia's normal scrolling path. Manually assigning Offset for every wheel
        // event forced synchronous realization/layout and made the desktop list feel stepped.
    }

    private void MobileFileList_ScrollGesture(object? sender, ScrollGestureEventArgs e)
    {
        if (!IsMobilePlatform || sender is not ScrollViewer scroll || !scroll.IsVisible || DataContext is not MainViewModel vm)
            return;

        _mobileScrollGestureActive = true;
        _mobileScrollGestureId = e.Id;
        _lastMobileScrollViewer = scroll;
        _mobileScrollLastActivityUtc = DateTime.UtcNow;
        vm.SetMobileListScrolling(true);

        // This event represents actual scroll intent and normally arrives before the first visible
        // offset change. Cancel the hold candidate here so a slow drag can never turn into selection.
        CancelMobileLongPress();
        if (!_mobileScrollIdleTimer.IsEnabled)
            _mobileScrollIdleTimer.Start();
    }

    private void MobileFileList_ScrollGestureInertiaStarting(object? sender, ScrollGestureInertiaStartingEventArgs e)
    {
        if (!IsMobilePlatform || sender is not ScrollViewer scroll || !scroll.IsVisible || DataContext is not MainViewModel vm)
            return;

        _mobileScrollGestureActive = true;
        _mobileScrollGestureId = e.Id;
        _lastMobileScrollViewer = scroll;
        _mobileScrollLastActivityUtc = DateTime.UtcNow;
        vm.SetMobileListScrolling(true);
        CancelMobileLongPress();
        if (!_mobileScrollIdleTimer.IsEnabled)
            _mobileScrollIdleTimer.Start();
    }

    private void MobileFileList_ScrollGestureEnded(object? sender, ScrollGestureEndedEventArgs e)
    {
        if (!IsMobilePlatform || sender is not ScrollViewer scroll || !scroll.IsVisible || DataContext is not MainViewModel vm)
            return;

        // ScrollGestureEnded means the whole gesture has stopped, including inertial movement.
        // Ignore an obsolete terminal event if a newer gesture already owns the scroll viewer.
        if (_mobileScrollGestureActive && _mobileScrollGestureId >= 0 && e.Id != _mobileScrollGestureId)
        {
            return;
        }

        _mobileScrollGestureActive = false;
        _mobileScrollGestureId = -1;
        _mobileScrollLastActivityUtc = DateTime.UtcNow;
        var completionVersion = unchecked(++_mobileScrollCompletionVersion);

        // Let the final inertia frame/layout commit first; then run thumbnail recovery and chrome
        // changes. Those operations can alter layout, so they must never occur inside the glide.
        Dispatcher.UIThread.Post(() =>
        {
            if (completionVersion != _mobileScrollCompletionVersion || _mobileScrollGestureActive ||
                DataContext is not MainViewModel currentVm || !ReferenceEquals(currentVm, vm) || !scroll.IsVisible)
            {
                return;
            }

            CompleteMobileScroll(scroll, currentVm);
        }, DispatcherPriority.Background);
    }

    private void SyncDesktopVirtualSurfaceViewport(ScrollViewer scroll)
    {
        if (IsMobilePlatform || !ReferenceEquals(scroll, DesktopVirtualScrollViewer))
            return;

        var viewportWidth = scroll.Viewport.Width > 1 ? scroll.Viewport.Width : scroll.Bounds.Width;
        var viewportHeight = scroll.Viewport.Height > 1 ? scroll.Viewport.Height : scroll.Bounds.Height;
        DesktopFileSurface.SetViewport(scroll.Offset.Y, viewportHeight, viewportWidth);
    }

    private void DesktopVirtualScrollViewer_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (IsMobilePlatform || sender is not ScrollViewer scroll)
            return;

        SyncDesktopVirtualSurfaceViewport(scroll);
        if (DataContext is MainViewModel vm && !vm.IsDesktopListScrolling)
            Dispatcher.UIThread.Post(() => QueueRealizedDesktopThumbnails(scroll, vm, allowNetwork: true), DispatcherPriority.Background);
    }

    private void DesktopFileSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsMobilePlatform || sender is not DesktopVirtualFileSurface surface || DataContext is not MainViewModel vm)
            return;

        var item = surface.GetItemAt(e.GetPosition(surface));
        if (item is null)
            return;

        _contextItem = item;
        var point = e.GetCurrentPoint(surface);
        if (point.Properties.IsLeftButtonPressed)
            ApplyDesktopPointerSelection(vm, item, e.KeyModifiers);
        else if (point.Properties.IsRightButtonPressed)
            SelectContextItem(item);
    }

    private async void DesktopFileSurface_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsMobilePlatform || sender is not DesktopVirtualFileSurface surface || DataContext is not MainViewModel vm)
            return;

        var item = surface.GetItemAt(e.GetPosition(surface));
        if (item is null)
            return;

        _contextItem = item;
        _desktopSelectionAnchorId = item.Id;
        _desktopSelectedIds.Clear();
        _desktopSelectedIds.Add(item.Id);
        ApplyDesktopSelection(vm);
        e.Handled = true;
        await OpenDriveItemAsync(vm, item);
    }

    private void DesktopFileSurface_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (IsMobilePlatform || sender is not DesktopVirtualFileSurface surface || _contextItem is not { } item)
            return;

        SelectContextItem(item);
        var menu = GetOrCreateDesktopFileItemContextMenu();
        if (_desktopOpenWebMenuItem is not null)
            _desktopOpenWebMenuItem.IsVisible = item.HasWebUrl;
        menu.Open(surface);
        e.Handled = true;
    }

    private void FileListScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || !scroll.IsVisible || DataContext is not MainViewModel vm)
            return;

        // Ignore stale frames from the folder we just left. Folder metadata pagination is no
        // longer tied to scrolling at all; this handler only maintains the mobile chrome and
        // viewport-prioritized thumbnail window.
        if (IsMobilePlatform && _mobileFolderNavigationInProgress)
            return;

        if (IsMobilePlatform)
        {
            HandleMobileChromeScroll(scroll, vm);
            return;
        }

        HandleDesktopFileScroll(scroll, vm);
    }

    private void HandleDesktopFileScroll(ScrollViewer scroll, MainViewModel vm)
    {
        // This is the complete desktop scroll hot path. SetViewport normally does not invalidate
        // the surface while the visible viewport remains inside its retained +/-1 viewport scene.
        SyncDesktopVirtualSurfaceViewport(scroll);
        _desktopScrollLastActivityUtc = DateTime.UtcNow;
        unchecked { _desktopThumbnailIdleRecoveryVersion++; }
        vm.SetDesktopListScrolling(true);

        if (!_desktopScrollIdleTimer.IsEnabled)
            _desktopScrollIdleTimer.Start();
    }

    private void DesktopScrollIdleTimer_Tick(object? sender, EventArgs e)
    {
        if (IsMobilePlatform || DataContext is not MainViewModel vm)
        {
            _desktopScrollIdleTimer.Stop();
            return;
        }

        if ((DateTime.UtcNow - _desktopScrollLastActivityUtc).TotalMilliseconds < 150)
            return;

        _desktopScrollIdleTimer.Stop();
        vm.SetDesktopListScrolling(false);
        var scroll = GetActiveScrollViewer(vm);
        if (scroll is null || !scroll.IsVisible)
            return;

        var recoveryVersion = unchecked(++_desktopThumbnailIdleRecoveryVersion);
        Dispatcher.UIThread.Post(() =>
        {
            if (recoveryVersion != _desktopThumbnailIdleRecoveryVersion ||
                DataContext is not MainViewModel currentVm ||
                currentVm.IsDesktopListScrolling ||
                !ReferenceEquals(GetActiveScrollViewer(currentVm), scroll))
            {
                return;
            }

            QueueRealizedDesktopThumbnails(scroll, currentVm, allowNetwork: true);
            _ = RecoverVisibleDesktopThumbnailsAfterIdleAsync(scroll, currentVm, recoveryVersion);
        }, DispatcherPriority.Background);
    }

    private async void MobileRefreshContainer_RefreshRequested(object? sender, RefreshRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        if (!IsMobilePlatform || UsesNativeMobileFileList || _mobileRefreshInProgress || DataContext is not MainViewModel vm || !vm.IsAuthenticated)
        {
            deferral.Complete();
            return;
        }

        _mobileRefreshInProgress = true;
        try
        {
            await vm.RefreshCurrentFolderAsync();
        }
        catch (OperationCanceledException)
        {
            // A newer navigation/refresh superseded this refresh.
        }
        finally
        {
            _mobileRefreshInProgress = false;
            deferral.Complete();
        }
    }

    private async Task NativeMobileRefreshAsync()
    {
        if (!UsesNativeMobileFileList || _mobileRefreshInProgress ||
            DataContext is not MainViewModel vm || !vm.IsAuthenticated)
            return;

        _mobileRefreshInProgress = true;
        try
        {
            await vm.RefreshCurrentFolderAsync();
        }
        catch (OperationCanceledException)
        {
            // A newer navigation/refresh superseded this native pull-to-refresh.
        }
        finally
        {
            _mobileRefreshInProgress = false;
        }
    }

    private void HandleMobileChromeScroll(ScrollViewer scroll, MainViewModel vm)
    {
        if (UsesNativeMobileFileList)
            return;
        // Android can deliver ScrollChanged once per rendered frame. Keep the work bounded:
        // thumbnail bookkeeping is throttled, while chrome collapse only adjusts two clipped
        // toolbar heights/transforms from the current scroll delta.
        var now = DateTime.UtcNow;
        _lastMobileScrollViewer = scroll;
        _mobileScrollLastActivityUtc = now;
        unchecked { _mobileThumbnailIdleRecoveryVersion++; }

vm.SetMobileListScrolling(true);
        CancelMobileLongPress();
        if (!_mobileScrollIdleTimer.IsEnabled)
            _mobileScrollIdleTimer.Start();

        // Re-evaluate the near-viewport thumbnail window at a modest cadence while scrolling.
        // UpdateMobileThumbnailWindow only permits persistent disk-cache hits during the fling,
        // so this gives recycled items their cached image without starting network work.
        if ((now - _mobileThumbnailLastQueueUtc).TotalMilliseconds >= 90)
        {
            _mobileThumbnailLastQueueUtc = now;
            QueueVisibleMobileThumbnails(scroll, vm);
            // The arithmetic window is only a cheap prefetch hint. The realized controls are the
            // source of truth for what is actually on screen, especially hundreds of rows into a
            // UniformGridLayout where MinItemHeight and arranged height can drift apart slightly.
            QueueRealizedMobileThumbnails(scroll, vm);
        }

        // Collapse/reveal the mobile chrome continuously with the content delta. Do not wait
        // for the fling to finish and do not toggle IsVisible: that caused an Auto-row height
        // jump. The toolbar heights are clipped a pixel at a time so down-scroll hides them and
        // up-scroll reveals them with the finger/inertia.
        var offset = Math.Max(0, scroll.Offset.Y);
        var delta = offset - _lastMobileScrollOffset;
        _lastMobileScrollOffset = offset;

        if (offset <= 1)
        {
            if (_mobileChromeCollapseOffset > 0.01)
            {
                _mobileChromeCollapseOffset = 0;
                ApplyMobileChromeCollapse(vm);
            }
            return;
        }

        if (Math.Abs(delta) < 0.10)
            return;

        EnsureMobileChromeMetrics(vm);
        var totalHeight = GetMobileChromeExpandedHeight(vm);
        if (totalHeight <= 1)
            return;

        var nextCollapse = Math.Clamp(_mobileChromeCollapseOffset + delta, 0, totalHeight);
        if (Math.Abs(nextCollapse - _mobileChromeCollapseOffset) < 0.10)
            return;

        _mobileChromeCollapseOffset = nextCollapse;
        ApplyMobileChromeCollapse(vm);
    }

    private void MobileScrollIdleTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var quietMilliseconds = (now - _mobileScrollLastActivityUtc).TotalMilliseconds;

        // Normal touch scrolling finishes through ScrollGestureEnded. While Avalonia still reports
        // an active gesture, never let an arbitrary 155 ms gap declare it idle: low-speed inertia
        // can legitimately have sparse offset updates near the tail. A long timeout remains only
        // as a defensive recovery if a platform loses the terminal gesture event.
        if (_mobileScrollGestureActive)
        {
            if (quietMilliseconds < 900)
                return;

            _mobileScrollGestureActive = false;
            _mobileScrollGestureId = -1;
        }
        else if (quietMilliseconds < 360)
        {
            return;
        }
        else
        {
        }

        _mobileScrollIdleTimer.Stop();
        if (DataContext is not MainViewModel vm)
            return;

        var scroll = _lastMobileScrollViewer ?? GetActiveScrollViewer(vm);
        if (scroll is not null)
            CompleteMobileScroll(scroll, vm);
    }

    private void CompleteMobileScroll(ScrollViewer scroll, MainViewModel vm)
    {
        _mobileScrollIdleTimer.Stop();
        vm.SetMobileListScrolling(false);
        QueueVisibleMobileThumbnails(scroll, vm, forceRescan: true);
        QueueRealizedMobileThumbnails(scroll, vm);
        var recoveryVersion = unchecked(++_mobileThumbnailIdleRecoveryVersion);
        _ = RecoverVisibleMobileThumbnailsAfterIdleAsync(scroll, vm, recoveryVersion);

        // Header collapse/reveal is already applied continuously from ScrollChanged. Ending the
        // gesture must not snap visibility or row height; only thumbnail recovery belongs here.
    }

    private void MobileIconScrollViewer_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!IsMobilePlatform)
            return;

        UpdateResponsiveMobileIconLayouts();
    }

    private void UpdateResponsiveMobileIconLayouts()
    {
        ConfigureResponsiveMobileGrid(
            MobileLargeIconRepeater,
            MobileLargeIconScrollViewer.Viewport.Width > 1
                ? MobileLargeIconScrollViewer.Viewport.Width
                : MobileLargeIconScrollViewer.Bounds.Width,
            targetCellWidth: 104,
            heightRatio: 1.34);

        ConfigureResponsiveMobileGrid(
            MobileExtraLargeIconRepeater,
            MobileExtraLargeIconScrollViewer.Viewport.Width > 1
                ? MobileExtraLargeIconScrollViewer.Viewport.Width
                : MobileExtraLargeIconScrollViewer.Bounds.Width,
            targetCellWidth: 154,
            heightRatio: 1.18);
    }

    private static void ConfigureResponsiveMobileGrid(
        ItemsRepeater repeater,
        double availableWidth,
        double targetCellWidth,
        double heightRatio)
    {
        if (availableWidth <= 1 || repeater.Layout is not UniformGridLayout layout)
            return;

        const double spacing = 4;
        // ScrollViewer content can otherwise keep its desired width instead of consuming the
        // whole viewport. Pin the repeater to the current viewport so rotation / split-screen
        // immediately reflows the grid.
        if (double.IsNaN(repeater.Width) || Math.Abs(repeater.Width - availableWidth) > 0.5)
            repeater.Width = availableWidth;

        var columns = Math.Max(1, (int)Math.Floor((availableWidth + spacing) / (targetCellWidth + spacing)));
        var itemWidth = Math.Max(72, (availableWidth - spacing * (columns - 1)) / columns);
        var itemHeight = Math.Max(96, itemWidth * heightRatio);

        // UniformGridLayout chooses the column count from MinItemWidth. Feeding it the exact
        // width of one equal column makes the final row consume the entire phone width instead
        // of leaving unused pixels at the right edge. The item template itself is stretch-based,
        // so folder/file artwork grows and shrinks with this cell rather than staying at 68/96 dp.
        if (Math.Abs(layout.MinItemWidth - itemWidth) > 0.5 || Math.Abs(layout.MinItemHeight - itemHeight) > 0.5)
        {
            layout.MinItemWidth = itemWidth;
            layout.MinItemHeight = itemHeight;
            repeater.InvalidateMeasure();
        }
    }

    private static int CalculateGridColumns(double viewportWidth, double minItemWidth, double spacing)
    {
        if (viewportWidth <= 1)
            return 1;
        return Math.Max(1, (int)Math.Floor((viewportWidth + spacing) / (minItemWidth + spacing)));
    }

    private void QueueRealizedDesktopThumbnails(
        ScrollViewer scroll,
        MainViewModel vm,
        bool allowNetwork)
    {
        if (IsMobilePlatform || !scroll.IsVisible || !ReferenceEquals(scroll, DesktopVirtualScrollViewer))
            return;

        SyncDesktopVirtualSurfaceViewport(scroll);
        var (visibleFirst, visibleLast) = DesktopFileSurface.GetVisibleRange();
        if (visibleFirst < 0 || visibleLast < visibleFirst || vm.VirtualItems.Count == 0)
            return;

        var visibleCount = Math.Max(1, visibleLast - visibleFirst + 1);
        var windowFrom = Math.Max(0, visibleFirst - visibleCount);
        var windowTo = Math.Min(vm.VirtualItems.Count - 1, visibleLast + visibleCount);
        var indices = new List<int>(windowTo - windowFrom + 1);
        var items = new List<DriveItemModel>(windowTo - windowFrom + 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddRange(int from, int to)
        {
            if (from > to)
                return;
            for (var index = from; index <= to; index++)
            {
                indices.Add(index);
                if (vm.VirtualItems[index].Item is { } item &&
                    !string.IsNullOrWhiteSpace(item.Id) && seen.Add(item.Id))
                {
                    items.Add(item);
                }
            }
        }

        // Current viewport first, then previous viewport, then next viewport. The VM preserves this
        // ordering when it enters the two-worker thumbnail gate, so what the user sees always wins.
        AddRange(visibleFirst, visibleLast);
        AddRange(windowFrom, visibleFirst - 1);
        AddRange(visibleLast + 1, windowTo);
        vm.UpdateDesktopRealizedThumbnails(indices, items, allowNetwork);
    }

    private async Task RecoverVisibleDesktopThumbnailsAfterIdleAsync(
        ScrollViewer scroll,
        MainViewModel vm,
        int version)
    {
        // A scrollbar thumb can land on slots whose metadata page has not reached the UI yet.
        // Re-scan a few times after idle so the moment those fixed slots receive real items their
        // thumbnails become eligible without requiring another wheel/touch event.
        foreach (var delayMs in new[] { 90, 240, 520, 1000 })
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            if (version != _desktopThumbnailIdleRecoveryVersion || vm.IsDesktopListScrolling)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _desktopThumbnailIdleRecoveryVersion ||
                    vm.IsDesktopListScrolling ||
                    !scroll.IsVisible ||
                    !ReferenceEquals(GetActiveScrollViewer(vm), scroll))
                {
                    return;
                }

                QueueRealizedDesktopThumbnails(scroll, vm, allowNetwork: true);
            }, DispatcherPriority.Background);
        }
    }

    private void QueueVisibleMobileThumbnails(ScrollViewer scroll, MainViewModel vm, bool forceRescan = false)
    {
        if (!IsMobilePlatform || !scroll.IsVisible || vm.MobileItems.Count == 0)
            return;

        var offset = Math.Max(0, scroll.Offset.Y);
        var viewportHeight = Math.Max(1, scroll.Viewport.Height);
        var viewportWidth = Math.Max(1, scroll.Viewport.Width);
        int startIndex;
        int visibleCount;

        switch (vm.ViewMode)
        {
            case FileViewMode.LargeIcons:
            {
                const double spacing = 4;
                var layout = MobileLargeIconRepeater.Layout as UniformGridLayout;
                var itemWidth = layout?.MinItemWidth ?? 108;
                var rowPitch = (layout?.MinItemHeight ?? 146) + spacing;
                var columns = CalculateGridColumns(viewportWidth, itemWidth, spacing);
                var startRow = Math.Max(0, (int)Math.Floor(offset / rowPitch) - 1);
                var rows = Math.Max(1, (int)Math.Ceiling(viewportHeight / rowPitch) + 3);
                startIndex = startRow * columns;
                visibleCount = rows * columns;
                break;
            }
            case FileViewMode.ExtraLargeIcons:
            {
                const double spacing = 4;
                var layout = MobileExtraLargeIconRepeater.Layout as UniformGridLayout;
                var itemWidth = layout?.MinItemWidth ?? 150;
                var rowPitch = (layout?.MinItemHeight ?? 188) + spacing;
                var columns = CalculateGridColumns(viewportWidth, itemWidth, spacing);
                var startRow = Math.Max(0, (int)Math.Floor(offset / rowPitch) - 1);
                var rows = Math.Max(1, (int)Math.Ceiling(viewportHeight / rowPitch) + 3);
                startIndex = startRow * columns;
                visibleCount = rows * columns;
                break;
            }
            default:
            {
                const double rowHeight = 46;
                startIndex = Math.Max(0, (int)Math.Floor(offset / rowHeight) - 6);
                visibleCount = Math.Max(16, (int)Math.Ceiling(viewportHeight / rowHeight) + 16);
                break;
            }
        }

        vm.UpdateMobileThumbnailWindow(startIndex, visibleCount, forceRescan);
    }

    private void QueueRealizedMobileThumbnails(ScrollViewer scroll, MainViewModel vm)
    {
        if (!IsMobilePlatform || !scroll.IsVisible)
            return;

        var repeater = vm.ViewMode switch
        {
            FileViewMode.LargeIcons => MobileLargeIconRepeater,
            FileViewMode.ExtraLargeIcons => MobileExtraLargeIconRepeater,
            _ => MobileDetailsRepeater
        };

        var viewportHeight = Math.Max(1, scroll.Viewport.Height);
        var visibleItems = new List<DriveItemModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var realizedElements = repeater.GetVisualChildren().OfType<Control>().ToArray();

        // Direct visual children are the currently realized repeater elements. Use their actual
        // arranged positions instead of deriving an index from ScrollViewer.Offset / MinItemHeight.
        foreach (var element in realizedElements)
        {
            var slot = element.DataContext as VirtualDriveItemSlot
                ?? element.GetVisualDescendants().OfType<Control>()
                    .Select(static control => control.DataContext as VirtualDriveItemSlot)
                    .FirstOrDefault(static candidate => candidate is not null);
            if (slot?.Item is not { } item || string.IsNullOrWhiteSpace(item.Id) || !seen.Add(item.Id))
                continue;

            var origin = element.TranslatePoint(new Point(0, 0), scroll);
            if (origin is null)
                continue;

            var top = origin.Value.Y;
            var bottom = top + Math.Max(1, element.Bounds.Height);
            if (bottom < -2 || top > viewportHeight + 2)
                continue;

            visibleItems.Add(item);
        }


if (visibleItems.Count > 0)
            vm.UpdateMobileRealizedThumbnails(visibleItems);
    }

    private async Task RecoverVisibleMobileThumbnailsAfterIdleAsync(ScrollViewer scroll, MainViewModel vm, int version)
    {
        // Large jumps have two asynchronous producers: inertial scrolling and placeholder metadata
        // filling. A few cheap forced rescans after idle close the race where the viewport was still
        // empty when the first idle scan ran. They also retry a transient thumbnail failure without
        // coupling image loading back to ScrollChanged.
        foreach (var delayMs in new[] { 140, 360, 760 })
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            if (version != _mobileThumbnailIdleRecoveryVersion || vm.IsMobileListScrolling)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _mobileThumbnailIdleRecoveryVersion || vm.IsMobileListScrolling || !scroll.IsVisible)
                {
                    return;
                }

                QueueVisibleMobileThumbnails(scroll, vm, forceRescan: true);
                QueueRealizedMobileThumbnails(scroll, vm);
            }, DispatcherPriority.Background);
        }
    }

    private void SetMobileChromeVisible(bool visible, MainViewModel vm)
    {
        if (!IsMobilePlatform || UsesNativeMobileFileList)
            return;

        EnsureMobileChromeMetrics(vm);
        _mobileChromeCollapseOffset = visible ? 0 : GetMobileChromeExpandedHeight(vm);
        ApplyMobileChromeCollapse(vm);
    }

    private void ResetMobileChrome(MainViewModel vm, bool recaptureHeights = false)
    {
        if (!IsMobilePlatform || UsesNativeMobileFileList || MobileTopToolbar is null || FileActionToolbar is null)
            return;

        _mobileChromeCollapseOffset = 0;

        if (recaptureHeights)
        {
            _mobileTopToolbarExpandedHeight = 0;
            _mobileActionToolbarExpandedHeight = 0;
            // Keep the two Auto rows at their natural expanded heights. Collapsing is now
            // compositor-only (RenderTransform) so touch scrolling never forces a Grid remeasure.
            MobileTopToolbar.Height = double.NaN;
            FileActionToolbar.Height = double.NaN;
            SetMobileChromeVisualOffset(MobileTopToolbar, 0);
            SetMobileChromeVisualOffset(FileActionToolbar, 0);
            SetMobileChromeVisualOffset(FileArea, 0);
            FileArea.Margin = new Thickness(0);

            // Wait for the natural Auto rows to be measured once. Subsequent scroll frames keep
            // those layout heights untouched and only change RenderTransform values.
            Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is not MainViewModel currentVm || !ReferenceEquals(currentVm, vm))
                    return;

                EnsureMobileChromeMetrics(currentVm);
                ApplyMobileChromeCollapse(currentVm);
            }, DispatcherPriority.Loaded);
            return;
        }

        EnsureMobileChromeMetrics(vm);
        ApplyMobileChromeCollapse(vm);
    }

    private static void SetMobileChromeVisualOffset(Visual? visual, double offsetY)
    {
        if (visual is not null)
            visual.RenderTransform = new TranslateTransform(0, offsetY);
    }

    private void EnsureMobileChromeMetrics(MainViewModel vm)
    {
        if (!IsMobilePlatform || MobileTopToolbar is null || FileActionToolbar is null)
            return;

        if (_mobileTopToolbarExpandedHeight <= 1 && MobileTopToolbar.Bounds.Height > 1)
            _mobileTopToolbarExpandedHeight = MobileTopToolbar.Bounds.Height;

        if (vm.ShowToolbar && _mobileActionToolbarExpandedHeight <= 1 && FileActionToolbar.Bounds.Height > 1)
            _mobileActionToolbarExpandedHeight = FileActionToolbar.Bounds.Height;
    }

    private double GetMobileChromeExpandedHeight(MainViewModel vm)
    {
        var top = Math.Max(0, _mobileTopToolbarExpandedHeight);
        var action = vm.ShowToolbar ? Math.Max(0, _mobileActionToolbarExpandedHeight) : 0;
        return top + action;
    }

    private void ApplyMobileChromeCollapse(MainViewModel vm)
    {
        if (!IsMobilePlatform || UsesNativeMobileFileList || MobileTopToolbar is null || FileActionToolbar is null || FileArea is null)
            return;

        EnsureMobileChromeMetrics(vm);

        var topExpanded = Math.Max(0, _mobileTopToolbarExpandedHeight);
        var actionExpanded = vm.ShowToolbar ? Math.Max(0, _mobileActionToolbarExpandedHeight) : 0;
        var totalExpanded = topExpanded + actionExpanded;
        if (totalExpanded <= 1)
            return;

        _mobileChromeCollapseOffset = Math.Clamp(_mobileChromeCollapseOffset, 0, totalExpanded);

        // IMPORTANT: never animate Height while a finger/inertial scroll is active. Changing the
        // two Auto-row heights every ScrollChanged forces the whole file grid / ItemsRepeater to
        // measure and arrange again, which is most visible immediately after entering a folder.
        //
        // Instead, keep the rows at their natural expanded height and move the three visuals with
        // RenderTransform only. FileArea gets one fixed negative bottom margin equal to the maximum
        // travel distance, so translating it upward never exposes an empty strip at the bottom.
        // The per-frame path is therefore compositor/transform work instead of layout work.
        MobileTopToolbar.Height = double.NaN;
        FileActionToolbar.Height = double.NaN;

        var topHidden = Math.Min(_mobileChromeCollapseOffset, topExpanded);
        var actionHidden = Math.Clamp(_mobileChromeCollapseOffset - topExpanded, 0, actionExpanded);
        var totalHidden = topHidden + actionHidden;

        SetMobileChromeVisualOffset(MobileTopToolbar, -topHidden);
        SetMobileChromeVisualOffset(FileActionToolbar, -(topHidden + actionHidden));
        SetMobileChromeVisualOffset(FileArea, -totalHidden);

        var desiredBottomMargin = -totalExpanded;
        if (Math.Abs(FileArea.Margin.Bottom - desiredBottomMargin) > 0.25 ||
            Math.Abs(FileArea.Margin.Left) > 0.01 ||
            Math.Abs(FileArea.Margin.Top) > 0.01 ||
            Math.Abs(FileArea.Margin.Right) > 0.01)
        {
            // This changes layout only when the measured expanded toolbar height changes (normally
            // once on load or when ShowToolbar changes), never once per scroll frame.
            FileArea.Margin = new Thickness(0, 0, 0, desiredBottomMargin);
        }

        MobileTopToolbar.IsHitTestVisible = topExpanded - topHidden > 4;

        FileActionToolbar.IsVisible = vm.ShowToolbar;
        if (vm.ShowToolbar)
            FileActionToolbar.IsHitTestVisible = actionExpanded - actionHidden > 4;
        else
            SetMobileChromeVisualOffset(FileActionToolbar, 0);
    }

    private void UpdateNativeMobileFileListGeometry(MainViewModel vm)
    {
        if (!UsesNativeMobileFileList)
            return;

        // The native host fills FileArea. Details mode reserves only the Avalonia column header;
        // icon modes use the complete area. The app chrome itself stays fixed on native mobile lists.
        var detailsHeaderHeight = vm.IsDetailsView
            ? Math.Max(36, DetailsHeaderBorder.Bounds.Height)
            : 0;

        NativeMobileFileListContainer.Margin = new Thickness(0, detailsHeaderHeight, 0, 0);
    }

    private NativeMobileFileListHost? EnsureNativeMobileFileListHost(MainViewModel vm)
    {
        if (!UsesNativeMobileFileList || !vm.IsAuthenticated)
            return null;

        if (_nativeMobileFileListHost is not null)
        {
            if (!ReferenceEquals(_nativeMobileFileListHost.DataContext, vm))
                _nativeMobileFileListHost.DataContext = vm;
            return _nativeMobileFileListHost;
        }

        var host = new NativeMobileFileListHost
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            DataContext = vm,
            RefreshRequestedAsync = NativeMobileRefreshAsync
        };
        host.ItemTapped += NativeMobileFileListHost_ItemTapped;
        host.ItemLongPressed += NativeMobileFileListHost_ItemLongPressed;
        host.ScrollStateChanged += NativeMobileFileListHost_ScrollStateChanged;
        host.FloatingUploadRequested += NativeMobileFileListHost_FloatingUploadRequested;
        host.FloatingUploadPositionChanged += NativeMobileFileListHost_FloatingUploadPositionChanged;

        _nativeMobileFileListHost = host;
        NativeMobileFileListContainer.Children.Add(host);
        return host;
    }

    private void DestroyNativeMobileFileListHost()
    {
        var host = _nativeMobileFileListHost;
        if (host is null)
            return;

        host.RefreshRequestedAsync = null;
        host.ItemTapped -= NativeMobileFileListHost_ItemTapped;
        host.ItemLongPressed -= NativeMobileFileListHost_ItemLongPressed;
        host.ScrollStateChanged -= NativeMobileFileListHost_ScrollStateChanged;
        host.FloatingUploadRequested -= NativeMobileFileListHost_FloatingUploadRequested;
        host.FloatingUploadPositionChanged -= NativeMobileFileListHost_FloatingUploadPositionChanged;
        NativeMobileFileListContainer.Children.Remove(host);
        host.DataContext = null;
        _nativeMobileFileListHost = null;
    }

    private void UpdateNativeMobileFileListVisibility()
    {
        if (!UsesNativeMobileFileList)
            return;

        if (DataContext is not MainViewModel vm || !vm.IsAuthenticated)
        {
            NativeMobileFileListContainer.IsVisible = false;
            // Do not keep a native Android/iOS list alive behind the signed-out page. Besides
            // saving resources, this makes a completely clean first launch independent from
            // native list creation timing.
            DestroyNativeMobileFileListHost();
            return;
        }

        // Native mobile views occupy their own platform surface and render above Avalonia.
        // Hide the native list whenever an Avalonia overlay/page must own the screen.
        var blocked =
            StartupSplashOverlay.IsVisible ||
            MobileDestinationOverlay.IsVisible ||
            MobileProfileOverlay.IsVisible ||
            vm.IsTransferPanelVisible ||
            vm.IsPreviewVisible ||
            MobileViewModeActionsOverlay.IsVisible ||
            MobileSortActionsOverlay.IsVisible ||
            MobilePreviewActionsOverlay.IsVisible ||
            vm.IsSettingsPanelVisible ||
            DownloadAllConfirmOverlay.IsVisible ||
            vm.IsBusy ||
            vm.IsPromptVisible ||
            vm.IsLogoutConfirmVisible ||
            vm.IsCloseConfirmVisible;

        if (blocked)
        {
            NativeMobileFileListContainer.IsVisible = false;
            return;
        }

        var host = EnsureNativeMobileFileListHost(vm);
        NativeMobileFileListContainer.IsVisible = host is not null;
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

        if (DownloadAllConfirmOverlay.IsVisible)
        {
            DownloadAllConfirmOverlay.IsVisible = false;
            UpdateNativeMobileFileListVisibility();
            e.Handled = true;
            return;
        }

        // Transient mobile action surfaces are dismissed before page navigation.
        if (MobileViewModeActionsOverlay.IsVisible)
        {
            CloseMobileViewModeActions();
            e.Handled = true;
            return;
        }

        if (MobileSortActionsOverlay.IsVisible)
        {
            CloseMobileSortActions();
            e.Handled = true;
            return;
        }

        // The mobile preview menu is a transient surface. Back must dismiss it
        // before closing the underlying preview.
        if (MobilePreviewActionsOverlay.IsVisible)
        {
            CloseMobilePreviewActions();
            e.Handled = true;
            return;
        }

        if (MobileDestinationOverlay.IsVisible)
        {
            if (!await GoBackMobileDestinationAsync())
                CloseMobileDestinationPicker();
            e.Handled = true;
            return;
        }

        if (MobileProfileOverlay.IsVisible)
        {
            MobileProfileOverlay.IsVisible = false;
            UpdateNativeMobileFileListVisibility();
            e.Handled = true;
            return;
        }

        if (vm.IsPromptVisible)
        {
            vm.CancelPromptCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (vm.IsLogoutConfirmVisible)
        {
            vm.CancelLogoutCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (vm.IsCloseConfirmVisible)
        {
            vm.CancelCloseConfirmation();
            e.Handled = true;
            return;
        }

        if (vm.IsPreviewVisible)
        {
            vm.ClosePreviewCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (vm.IsSettingsPanelVisible)
        {
            var returnToProfile = IsMobilePlatform && _settingsOpenedFromMobileProfile;
            await CloseSettingsPanelAsync();
            if (returnToProfile)
            {
                _settingsOpenedFromMobileProfile = false;
                MobileProfileOverlay.IsVisible = true;
                UpdateNativeMobileFileListVisibility();
            }
            e.Handled = true;
            return;
        }

        if (vm.IsTransferPanelVisible)
        {
            vm.IsTransferPanelVisible = false;
            e.Handled = true;
            return;
        }

        // Android/iOS convention: when the file list is in selection mode, Back exits
        // selection mode first. A second Back then navigates to the parent folder.
        if (IsMobilePlatform && (vm.HasSelection || _mobileSelectionMode || _mobileSelectedIds.Count > 0))
        {
            ClearListSelections();
            SetMobileChromeVisible(true, vm);
            e.Handled = true;
            return;
        }

        if (vm.Breadcrumbs.Count > 1)
        {
            vm.GoBackCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (IsMobilePlatform && vm.IsAuthenticated)
        {
            vm.RequestCloseConfirmation();
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
        {
            window.ConfirmClose();
            return;
        }

        // Android has an activity lifetime rather than an Avalonia Window. Let the platform
        // head close its own native activity instead of terminating the process from Core.
        AppServices.PlatformAppLifecycleService?.ExitApp();
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

        if (IsMobilePlatform)
        {
            vm.IsSettingsPanelVisible = true;
            if (SettingsPanelHost.RenderTransform is TranslateTransform mobileTransform)
                mobileTransform.X = 0;
            return;
        }

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

        if (IsMobilePlatform)
        {
            vm.IsSettingsPanelVisible = false;
            return;
        }

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

    private async void SettingsMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (IsMobilePlatform)
            _settingsOpenedFromMobileProfile = false;
        await ToggleSettingsPanelAsync();
    }

    private void MobileAvatarButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsMobilePlatform)
            return;
        _settingsOpenedFromMobileProfile = false;
        MobileProfileOverlay.IsVisible = true;
        UpdateNativeMobileFileListVisibility();
    }

    private async void MobileProfileOpenWeb_Click(object? sender, RoutedEventArgs e)
    {
        MobileProfileOverlay.IsVisible = false;
        UpdateNativeMobileFileListVisibility();
        await OpenOneDriveWebAsync();
    }

    private async void MobileProfileSettings_Click(object? sender, RoutedEventArgs e)
    {
        _settingsOpenedFromMobileProfile = true;
        MobileProfileOverlay.IsVisible = false;
        UpdateNativeMobileFileListVisibility();
        await OpenSettingsPanelAsync();
    }

    private void MobileProfileLogout_Click(object? sender, RoutedEventArgs e)
    {
        _settingsOpenedFromMobileProfile = false;
        MobileProfileOverlay.IsVisible = false;
        UpdateNativeMobileFileListVisibility();
        if (DataContext is MainViewModel vm)
            vm.RequestLogoutCommand.Execute(null);
    }

    private async Task OpenOneDriveWebAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
            await topLevel.Launcher.LaunchUriAsync(new Uri("https://onedrive.live.com/"));
    }

    private async void OpenWebMenuItem_Click(object? sender, RoutedEventArgs e) => await OpenOneDriveWebAsync();

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

    private async Task OpenDriveItemAsync(MainViewModel vm, DriveItemModel item)
    {
        if (item.IsFile && !vm.UseBuiltInViewer)
        {
            await OpenWithSystemDefaultAsync(vm, item);
            return;
        }

        var result = await vm.OpenItemAsync(item);
        if (result != DriveItemOpenResult.RequiresOfficialOneDriveHandoff)
            return;

        await OpenInOfficialOneDriveAsync(vm, item);
    }

    private async Task OpenInOfficialOneDriveAsync(MainViewModel vm, DriveItemModel item)
    {
        vm.ErrorMessage = null;
        var isVault = item.IsPersonalVault;
        vm.StatusText = isVault
            ? "正在打开 OneDrive 个人保险库…"
            : "正在通过 OneDrive 官方界面打开…";

        // Personal Vault requires Microsoft's own additional-verification experience. Prefer
        // the Graph-provided webUrl because it points at the exact item and survives localized
        // display names. Fall back to OneDrive web root only when Graph omitted that URL.
        var uri = Uri.TryCreate(item.WebUrl, UriKind.Absolute, out var itemUri)
            ? itemUri
            : new Uri("https://onedrive.live.com/");

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || !await topLevel.Launcher.LaunchUriAsync(uri))
        {
            vm.ErrorMessage = isVault
                ? "无法打开 OneDrive。请在 OneDrive 官方应用或 onedrive.com 中打开并验证个人保险库。"
                : "无法使用 OneDrive 官方界面打开此项目。";
            vm.StatusText = isVault
                ? "个人保险库需要 OneDrive 官方验证"
                : "无法打开此特殊项目";
            return;
        }

        vm.StatusText = isVault
            ? "请在 OneDrive 中完成身份验证后访问个人保险库"
            : "已交给 OneDrive 官方界面处理";
    }

    private static DriveItemModel? GetDriveItemFromDataContext(object? dataContext) => dataContext switch
    {
        DriveItemModel item => item,
        VirtualDriveItemSlot { Item: { } item } => item,
        _ => null
    };

    private static bool SuppressTransientNetworkError(MainViewModel vm, Exception ex)
    {
        if (!MainViewModel.IsTransientNetworkFailure(ex))
            return false;

        // Browsing/preview reads retry in OneDriveService. If a transient TLS/DNS/socket failure
        // escapes an operation boundary, never turn it into a red low-level exception banner.
        vm.ErrorMessage = null;
        return true;
    }

    private void FileItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || GetDriveItemFromDataContext(control.DataContext) is not { } item)
            return;

        _contextItem = item;
        var point = e.GetCurrentPoint(control);

        if (IsMobilePlatform && e.Pointer.Type == PointerType.Touch)
        {
            BeginMobileLongPress(item, e.GetPosition(FileArea), e.Timestamp);
            return;
        }

        if (!IsMobilePlatform && point.Properties.IsLeftButtonPressed && DataContext is MainViewModel vm)
        {
            ApplyDesktopPointerSelection(vm, item, e.KeyModifiers);
            return;
        }

        if (point.Properties.IsRightButtonPressed)
            SelectContextItem(item);
    }

    private void ApplyDesktopPointerSelection(MainViewModel vm, DriveItemModel item, KeyModifiers modifiers)
    {
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);

        if (shift && !string.IsNullOrWhiteSpace(_desktopSelectionAnchorId))
        {
            var loaded = vm.GetVisibleLoadedItemsSnapshot();
            var anchorIndex = Array.FindIndex(loaded, x => string.Equals(x.Id, _desktopSelectionAnchorId, StringComparison.Ordinal));
            var targetIndex = Array.FindIndex(loaded, x => string.Equals(x.Id, item.Id, StringComparison.Ordinal));
            if (anchorIndex >= 0 && targetIndex >= 0)
            {
                if (!ctrl)
                    _desktopSelectedIds.Clear();
                var from = Math.Min(anchorIndex, targetIndex);
                var to = Math.Max(anchorIndex, targetIndex);
                for (var i = from; i <= to; i++)
                    _desktopSelectedIds.Add(loaded[i].Id);
                ApplyDesktopSelection(vm);
                return;
            }
        }

        if (ctrl)
        {
            if (!_desktopSelectedIds.Add(item.Id))
                _desktopSelectedIds.Remove(item.Id);
        }
        else
        {
            _desktopSelectedIds.Clear();
            _desktopSelectedIds.Add(item.Id);
        }

        _desktopSelectionAnchorId = item.Id;
        ApplyDesktopSelection(vm);
    }

    private void ApplyDesktopSelection(MainViewModel vm)
    {
        var selected = new List<DriveItemModel>();
        foreach (var candidate in vm.LoadedItems)
        {
            var isSelected = _desktopSelectedIds.Contains(candidate.Id);
            candidate.IsMobileSelected = isSelected;
            if (isSelected)
                selected.Add(candidate);
        }
        vm.SetSelectedItems(selected);
    }

    private async void FileItem_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsMobilePlatform || sender is not Control control ||
            GetDriveItemFromDataContext(control.DataContext) is not { } item ||
            DataContext is not MainViewModel vm)
            return;

        _desktopSelectionAnchorId = item.Id;
        _desktopSelectedIds.Clear();
        _desktopSelectedIds.Add(item.Id);
        ApplyDesktopSelection(vm);
        e.Handled = true;
        await OpenDriveItemAsync(vm, item);
    }

    private void FileItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!IsMobilePlatform || e.Pointer.Type != PointerType.Touch)
            return;

        // Cancel a pending hold at the item itself as well as at FileArea. This makes a normal
        // tap deterministic even if a repeater/gesture recognizer handles the routed release.
        CancelMobileLongPress();
    }

    private async void FileItem_Tapped(object? sender, TappedEventArgs e)
    {
        // Mobile file-manager semantics: tap opens; selection is reserved for a deliberate
        // stationary long-press. Desktop keeps single-click-select / double-click-open.
        if (!IsMobilePlatform ||
            sender is not Control control ||
            GetDriveItemFromDataContext(control.DataContext) is not { } item ||
            DataContext is not MainViewModel vm)
            return;

        var longPressStartTimestamp = _mobileLongPressStartTimestamp;
        var longPressStartedWhileSelectionMode = _mobileLongPressStartedWhileSelectionMode;
        var suppressedByLongPress = string.Equals(_suppressNextMobileTapItemId, item.Id, StringComparison.Ordinal);

        CancelMobileLongPress();
        _contextItem = item;
        e.Handled = true;

        // DispatcherTimer runs on the UI thread. On a busy frame it is possible for a due timer
        // callback to be dequeued before the already-physical PointerReleased/Tapped events. Use
        // Avalonia input timestamps to undo that false long-press, so a real short folder tap
        // always opens the folder instead of unexpectedly entering selection mode.
        if (suppressedByLongPress)
        {
            _suppressNextMobileTapItemId = null;
            var inputDuration = e.Timestamp >= longPressStartTimestamp
                ? e.Timestamp - longPressStartTimestamp
                : ulong.MaxValue;

            if (!longPressStartedWhileSelectionMode && inputDuration < MobileLongPressThresholdMilliseconds)
            {
                ClearListSelections();
                await OpenDriveItemAsync(vm, item);
            }
            return;
        }

        if (_mobileSelectionMode)
        {
            ToggleMobileSelection(item, vm);
            return;
        }

        ClearListSelections();
        await OpenDriveItemAsync(vm, item);
    }

    private async void NativeMobileFileListHost_ItemTapped(object? sender, NativeMobileFileItemEventArgs e)
    {
        if (!UsesNativeMobileFileList || DataContext is not MainViewModel vm)
            return;

        var item = e.Item;
        _contextItem = item;

        if (_mobileSelectionMode)
        {
            ToggleMobileSelection(item, vm);
            return;
        }

        ClearListSelections();
        await OpenDriveItemAsync(vm, item);
    }

    private void NativeMobileFileListHost_ItemLongPressed(object? sender, NativeMobileFileItemEventArgs e)
    {
        if (!UsesNativeMobileFileList || DataContext is not MainViewModel vm || vm.IsMobileListScrolling)
            return;

        var item = e.Item;
        _contextItem = item;

        if (!_mobileSelectionMode)
        {
            _mobileSelectedIds.Clear();
            _mobileSelectedItemsById.Clear();
            _mobileSelectionMode = true;
        }

        _mobileSelectedIds.Add(item.Id);
        _mobileSelectedItemsById[item.Id] = item;
        item.IsMobileSelected = true;
        item.IsMobileSelectionMode = true;
        SyncMobileSelection(vm);
        ShowMobileSelectionActions(vm);
    }

    private void NativeMobileFileListHost_ScrollStateChanged(object? sender, NativeMobileFileScrollEventArgs e)
    {
        if (!UsesNativeMobileFileList || DataContext is not MainViewModel vm)
            return;

        vm.SetMobileListScrolling(e.IsScrolling);
    }

    private void NativeMobileFileListHost_FloatingUploadRequested(object? sender, EventArgs e)
    {
        if (!UsesNativeMobileFileList)
            return;

        Dispatcher.UIThread.Post(
            () => UploadButton_Click(FloatingUploadButton, new RoutedEventArgs()),
            DispatcherPriority.Input);
    }

    private async void NativeMobileFileListHost_FloatingUploadPositionChanged(
        object? sender,
        NativeFloatingUploadPositionEventArgs e)
    {
        if (!UsesNativeMobileFileList || DataContext is not MainViewModel vm)
            return;

        await vm.SaveFloatingUploadPositionAsync(e.NormalizedX, e.NormalizedY);
    }

    private void BeginMobileLongPress(DriveItemModel item, Point start, ulong timestamp)
    {
        if (UsesNativeMobileFileList)
            return;

        CancelMobileLongPress();
        _mobileLongPressSelectionActivated = false;

        if (DataContext is not MainViewModel vm || vm.IsMobileListScrolling || _mobileScrollGestureActive)
            return;

        var scroll = GetActiveScrollViewer(vm);
        if (scroll is null || !scroll.IsVisible)
            return;

        _mobileLongPressItem = item;
        _mobileLongPressStart = start;
        _mobileLongPressStartTimestamp = timestamp;
        _mobileLongPressStartedWhileSelectionMode = _mobileSelectionMode;
        _mobileLongPressMoved = false;
        _mobileLongPressScrollViewer = scroll;
        _mobileLongPressStartScrollOffsetY = scroll.Offset.Y;
        _mobileLongPressTimer.Start();
    }

    private void CancelMobileLongPress()
    {
        if (_mobileLongPressItem is null && !_mobileLongPressTimer.IsEnabled && !_mobileLongPressMoved)
            return;

        _mobileLongPressTimer.Stop();
        _mobileLongPressItem = null;
        _mobileLongPressMoved = false;
        _mobileLongPressScrollViewer = null;
        _mobileLongPressStartScrollOffsetY = 0;
    }

    private void MobileLongPressTimer_Tick(object? sender, EventArgs e)
    {
        _mobileLongPressTimer.Stop();
        if (!IsMobilePlatform || _mobileLongPressMoved || _mobileLongPressItem is not { } item ||
            GetMobileItemAt(_mobileLongPressStart) is not { } hitItem ||
            !string.Equals(hitItem.Id, item.Id, StringComparison.Ordinal) ||
            DataContext is not MainViewModel vm || vm.IsMobileListScrolling || _mobileScrollGestureActive ||
            _mobileLongPressScrollViewer is not { } scroll ||
            !ReferenceEquals(GetActiveScrollViewer(vm), scroll) ||
            Math.Abs(scroll.Offset.Y - _mobileLongPressStartScrollOffsetY) > 1.0)
        {
            _mobileLongPressItem = null;
            _mobileLongPressScrollViewer = null;
            return;
        }

        // Phone file managers normally use long-press only to enter selection mode. Do not turn
        // subsequent finger movement into drag-range multi-selection: that gesture is too easy to
        // confuse with a slow scroll. Additional items are selected explicitly by tapping them.
        if (!_mobileSelectionMode)
        {
            _mobileSelectedIds.Clear();
            _mobileSelectionMode = true;
        }

        MobileSelectionActionBar.IsVisible = false;
        _mobileSelectedIds.Add(item.Id);
        _mobileSelectedItemsById[item.Id] = item;
        item.IsMobileSelected = true;
        _suppressNextMobileTapItemId = item.Id;
        _mobileLongPressSelectionActivated = true;
        SyncMobileSelection(vm);
        SetMobileChromeVisible(true, vm);
        _mobileLongPressItem = null;
        _mobileLongPressScrollViewer = null;
    }

    private void ClearListSelections()
    {
        CancelMobileLongPress();
        _suppressNextMobileTapItemId = null;
        _mobileLongPressSelectionActivated = false;
        MobileSelectionActionBar.IsVisible = false;

        var vm = DataContext as MainViewModel;
        if (vm is not null)
        {
            if (UsesNativeMobileFileList)
            {
                // Selection updates on Android must remain O(selected), never O(folder-size).
                foreach (var item in _mobileSelectedItemsById.Values)
                {
                    item.IsMobileSelected = false;
                    item.IsMobileSelectionMode = false;
                }
            }
            else
            {
                foreach (var item in vm.LoadedItems)
                {
                    item.IsMobileSelected = false;
                    item.IsMobileSelectionMode = false;
                }
            }
        }

        _mobileSelectedIds.Clear();
        _mobileSelectedItemsById.Clear();
        _desktopSelectedIds.Clear();
        _desktopSelectionAnchorId = null;
        _mobileSelectionMode = false;
        if (vm is not null)
            vm.MobileSelectionModeActive = false;

        if (UsesNativeMobileFileList)
            _nativeMobileFileListHost?.UpdateSelectionState([], false);
        vm?.SetSelectedItems([]);
    }

    private void ToggleMobileSelection(DriveItemModel item, MainViewModel vm)
    {
        if (_mobileSelectedIds.Add(item.Id))
        {
            _mobileSelectedItemsById[item.Id] = item;
            item.IsMobileSelected = true;
        }
        else
        {
            _mobileSelectedIds.Remove(item.Id);
            _mobileSelectedItemsById.Remove(item.Id);
            item.IsMobileSelected = false;
        }

        SyncMobileSelection(vm);
    }

    private void SyncMobileSelection(MainViewModel vm)
    {
        if (!IsMobilePlatform)
            return;

        var selectionMode = _mobileSelectedIds.Count > 0;
        vm.MobileSelectionModeActive = selectionMode;

        if (UsesNativeMobileFileList)
        {
            // RecyclerView redraws only realized cells. Do not walk thousands of loaded items for
            // every selection tap just to update hidden Avalonia template properties.
            var selected = _mobileSelectedItemsById.Values
                .Where(item => _mobileSelectedIds.Contains(item.Id))
                .ToArray();

            foreach (var item in selected)
            {
                item.IsMobileSelected = true;
                item.IsMobileSelectionMode = selectionMode;
            }

            vm.SetSelectedItems(selected);
            _mobileSelectionMode = selectionMode;
            _nativeMobileFileListHost?.UpdateSelectionState(_mobileSelectedIds, selectionMode);

            if (MobileSelectionActionBar.IsVisible)
            {
                if (selected.Length == 0)
                    MobileSelectionActionBar.IsVisible = false;
                else
                    MobileSelectionCountText.Text = $"已选择 {selected.Length} 项";
            }
            return;
        }

        foreach (var candidate in vm.LoadedItems)
        {
            candidate.IsMobileSelected = _mobileSelectedIds.Contains(candidate.Id);
            candidate.IsMobileSelectionMode = selectionMode;
        }

        var fallbackSelected = vm.LoadedItems.Where(x => x.IsMobileSelected).ToArray();
        vm.SetSelectedItems(fallbackSelected);
        _mobileSelectionMode = selectionMode;
        if (MobileSelectionActionBar.IsVisible)
        {
            if (fallbackSelected.Length == 0)
                MobileSelectionActionBar.IsVisible = false;
            else
                MobileSelectionCountText.Text = $"已选择 {fallbackSelected.Length} 项";
        }
    }

    private void ShowMobileSelectionActions(MainViewModel vm)
    {
        if (!IsMobilePlatform || vm.SelectionCount <= 0)
        {
            MobileSelectionActionBar.IsVisible = false;
            return;
        }

        MobileSelectionCountText.Text = $"已选择 {vm.SelectionCount} 项";
        MobileSelectionActionBar.IsVisible = true;
    }

    private DriveItemModel? GetMobileItemAt(Point point)
    {
        var visual = FileArea.InputHitTest(point) as Visual;
        while (visual is not null)
        {
            if (visual is StyledElement styled && GetDriveItemFromDataContext(styled.DataContext) is { } item)
                return item;
            if (ReferenceEquals(visual, FileArea))
                break;
            visual = visual.GetVisualParent();
        }
        return null;
    }

    private void FileItem_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Mobile uses custom long-press selection; never allow the platform context-menu gesture
        // to compete with scrolling.
        if (IsMobilePlatform)
        {
            e.Handled = true;
            return;
        }

        if (sender is not Control control || GetDriveItemFromDataContext(control.DataContext) is not { } item)
            return;

        _contextItem = item;
        SelectContextItem(item);
        var menu = GetOrCreateDesktopFileItemContextMenu();
        if (_desktopOpenWebMenuItem is not null)
            _desktopOpenWebMenuItem.IsVisible = item.HasWebUrl;
        menu.Open(control);
        e.Handled = true;
    }

    private ContextMenu GetOrCreateDesktopFileItemContextMenu()
    {
        if (_desktopFileItemContextMenu is not null)
            return _desktopFileItemContextMenu;

        var open = new MenuItem { Header = "打开" };
        open.Click += FileContext_Open_Click;
        var download = new MenuItem { Header = "下载" };
        download.Click += FileContext_Download_Click;
        var cache = new MenuItem { Header = "缓存" };
        cache.Click += FileContext_Cache_Click;
        var rename = new MenuItem { Header = "重命名" };
        rename.Click += FileContext_Rename_Click;
        var delete = new MenuItem { Header = "删除" };
        delete.Click += FileContext_Delete_Click;
        _desktopOpenWebMenuItem = new MenuItem { Header = "在 OneDrive 网页中打开" };
        _desktopOpenWebMenuItem.Click += FileContext_OpenWeb_Click;

        _desktopFileItemContextMenu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                open,
                new Separator(),
                download,
                cache,
                rename,
                delete,
                new Separator(),
                _desktopOpenWebMenuItem
            }
        };
        return _desktopFileItemContextMenu;
    }

    private void FileItem_Holding(object? sender, HoldingRoutedEventArgs e)
    {
        // Mobile templates no longer attach Holding at all. Keep this handler only for desktop
        // templates where a long-press/right-click style context action is still useful.
        if (IsMobilePlatform || e.HoldingState != HoldingState.Started ||
            sender is not Control { DataContext: DriveItemModel item } control)
            return;

        _contextItem = item;
        SelectContextItem(item);
        control.ContextMenu?.Open(control);
        e.Handled = true;
    }

    private async void FileContext_Open_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && GetContextItem(sender) is { } item)
            await OpenDriveItemAsync(vm, item);
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

        if (IsMobilePlatform)
        {
            if (forceSingle || !_mobileSelectedIds.Contains(item.Id))
            {
                _mobileSelectedIds.Clear();
                _mobileSelectedItemsById.Clear();
                _mobileSelectedIds.Add(item.Id);
                _mobileSelectedItemsById[item.Id] = item;
                SyncMobileSelection(vm);
            }
            return;
        }

        var alreadySelected = _desktopSelectedIds.Contains(item.Id);
        if (forceSingle || !alreadySelected)
        {
            _desktopSelectedIds.Clear();
            _desktopSelectedIds.Add(item.Id);
            _desktopSelectionAnchorId = item.Id;
            ApplyDesktopSelection(vm);
        }
    }

    private async void ViewModeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || DataContext is not MainViewModel vm)
            return;

        if (Enum.TryParse<FileViewMode>(tag, out var mode))
        {
            ClearListSelections();
            await vm.SetViewModeAsync(mode);
            Dispatcher.UIThread.Post(UpdateIconPanelSizing, DispatcherPriority.Loaded);
            if (IsMobilePlatform && !UsesNativeMobileFileList)
                Dispatcher.UIThread.Post(UpdateResponsiveMobileIconLayouts, DispatcherPriority.Loaded);
            if (UsesNativeMobileFileList)
                Dispatcher.UIThread.Post(() => UpdateNativeMobileFileListGeometry(vm), DispatcherPriority.Loaded);
        }
    }

    private async void SortHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && DataContext is MainViewModel vm &&
            Enum.TryParse<FileSortColumn>(button.Tag?.ToString(), true, out var column))
        {
            await vm.CycleSortAsync(column);
            e.Handled = true;
        }
    }

    private async void SortMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
            return;

        await ApplySortTagAsync(menuItem.Tag?.ToString());
    }

    private void MobileViewModeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsMobilePlatform)
            return;

        CancelMobileLongPress();
        CloseMobileSortActions();
        CloseMobilePreviewActions();
        MobileViewModeActionsOverlay.IsVisible = true;
        UpdateNativeMobileFileListVisibility();
        e.Handled = true;
    }

    private void CloseMobileViewModeActions()
    {
        MobileViewModeActionsOverlay.IsVisible = false;
        UpdateNativeMobileFileListVisibility();
    }

    private void MobileViewModeActionsBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseMobileViewModeActions();
        e.Handled = true;
    }

    private async void MobileViewModeAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || DataContext is not MainViewModel vm ||
            !Enum.TryParse<FileViewMode>(tag, out var mode))
            return;

        CloseMobileViewModeActions();
        ClearListSelections();
        await vm.SetViewModeAsync(mode);
        Dispatcher.UIThread.Post(UpdateIconPanelSizing, DispatcherPriority.Loaded);
        if (IsMobilePlatform && !UsesNativeMobileFileList)
            Dispatcher.UIThread.Post(UpdateResponsiveMobileIconLayouts, DispatcherPriority.Loaded);
        if (UsesNativeMobileFileList)
            Dispatcher.UIThread.Post(() => UpdateNativeMobileFileListGeometry(vm), DispatcherPriority.Loaded);
        e.Handled = true;
    }

    private void MobileSortButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsMobilePlatform)
            return;

        CancelMobileLongPress();
        CloseMobileViewModeActions();
        CloseMobilePreviewActions();
        MobileSortActionsOverlay.IsVisible = true;
        UpdateNativeMobileFileListVisibility();
        e.Handled = true;
    }

    private void CloseMobileSortActions()
    {
        MobileSortActionsOverlay.IsVisible = false;
        UpdateNativeMobileFileListVisibility();
    }

    private void MobileSortActionsBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseMobileSortActions();
        e.Handled = true;
    }

    private async void MobileSortAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        var tag = button.Tag?.ToString();
        CloseMobileSortActions();
        await ApplySortTagAsync(tag);
        e.Handled = true;
    }

    private async Task ApplySortTagAsync(string? tag)
    {
        if (DataContext is not MainViewModel vm || string.IsNullOrWhiteSpace(tag))
            return;

        if (string.Equals(tag, "Inherit:Default", StringComparison.Ordinal))
        {
            await vm.UseDefaultSortForCurrentFolderAsync();
            return;
        }

        var parts = tag.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Enum.TryParse<FileSortColumn>(parts[0], true, out var column) ||
            !Enum.TryParse<SortCycleState>(parts[1], true, out var state))
            return;

        await vm.SetSortAsync(column, state);
    }

    private async void ViewContextMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || DataContext is not MainViewModel vm ||
            !Enum.TryParse<FileViewMode>(tag, out var mode))
            return;

        ClearListSelections();
        await vm.SetViewModeAsync(mode);
        Dispatcher.UIThread.Post(HookListScrollViewers, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(UpdateIconPanelSizing, DispatcherPriority.Loaded);
            if (IsMobilePlatform)
                Dispatcher.UIThread.Post(UpdateResponsiveMobileIconLayouts, DispatcherPriority.Loaded);
    }

    private void FileArea_NewFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.BeginCreateFolderCommand.Execute(null);
    }

    private void FileArea_Upload_Click(object? sender, RoutedEventArgs e) => UploadButton_Click(sender, e);


    private static bool ShouldSuppressMarqueeStart(object? source)
    {
        if (source is StyledElement styled && GetDriveItemFromDataContext(styled.DataContext) is not null)
            return true;

        if (source is not Visual visual)
            return false;

        static bool IsInteractive(Visual candidate) => candidate is Button or TextBox or ScrollBar or Thumb;
        if (IsInteractive(visual))
            return true;

        foreach (var ancestor in visual.GetVisualAncestors())
        {
            if (ancestor is StyledElement ancestorStyled &&
                GetDriveItemFromDataContext(ancestorStyled.DataContext) is not null)
                return true;
            if (IsInteractive(ancestor))
                return true;
        }

        return false;
    }

    private void FileArea_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateIconPanelSizing();
        if (UsesNativeMobileFileList && DataContext is MainViewModel vm)
            UpdateNativeMobileFileListGeometry(vm);
    }

    private void UpdateIconPanelSizing()
    {
        if (IsMobilePlatform)
            return;

        DesktopFileSurface.InvalidateMeasure();
        SyncDesktopVirtualSurfaceViewport(DesktopVirtualScrollViewer);
    }

    private void FileArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsMobilePlatform || DataContext is not MainViewModel vm)
            return;

        var point = e.GetCurrentPoint(FileArea);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        // The right-most strip belongs to the active ListBox scrollbar. Never begin a
        // marquee there even if a platform reports the event source as a template Border.
        var pointerPosition = e.GetPosition(FileArea);
        if (pointerPosition.X >= Math.Max(0, FileArea.Bounds.Width - 22))
            return;

        // The desktop file area is one control, so DataContext alone can no longer tell whether
        // the pointer is over an item or an empty gap. Ask the virtual surface directly.
        if (e.Source is DesktopVirtualFileSurface desktopSurface &&
            desktopSurface.GetItemAt(e.GetPosition(desktopSurface)) is not null)
            return;

        // Do not steal pointer capture from real controls. In particular the scrollbar thumb and
        // header buttons must keep their own input gestures.
        if (ShouldSuppressMarqueeStart(e.Source))
            return;

        _marqueeSelecting = true;
        _marqueeStart = e.GetPosition(FileArea);
        _marqueeBaseSelection.Clear();

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            foreach (var id in _desktopSelectedIds)
                _marqueeBaseSelection.Add(id);
        }
        else
        {
            _desktopSelectedIds.Clear();
            ApplyDesktopSelection(vm);
        }

        SelectionMarquee.IsVisible = false;
        e.Pointer.Capture(FileArea);
    }

    private void FileArea_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (IsMobilePlatform && _mobileLongPressItem is not null)
        {
            var touchPoint = e.GetPosition(FileArea);
            var dx = touchPoint.X - _mobileLongPressStart.X;
            var dy = touchPoint.Y - _mobileLongPressStart.Y;
            var scrollOffsetChanged = _mobileLongPressScrollViewer is { } longPressScroll &&
                                      Math.Abs(longPressScroll.Offset.Y - _mobileLongPressStartScrollOffsetY) > 1.0;

            // Six logical pixels is enough to establish scroll intent while still tolerating normal
            // finger jitter during a deliberate hold. The actual ScrollGesture event also cancels
            // the timer, so selection cannot win a race against a slow drag/fling.
            if (dx * dx + dy * dy >= 6 * 6 || scrollOffsetChanged)
            {
                _mobileLongPressMoved = true;
                CancelMobileLongPress();
            }
            return;
        }

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
        var selectedIds = new HashSet<string>(_marqueeBaseSelection, StringComparer.Ordinal);
        var topLeft = FileArea.TranslatePoint(new Point(selectionRect.Left, selectionRect.Top), DesktopFileSurface);
        var bottomRight = FileArea.TranslatePoint(new Point(selectionRect.Right, selectionRect.Bottom), DesktopFileSurface);
        if (topLeft is { } a && bottomRight is { } b)
        {
            var surfaceRect = new Rect(
                Math.Min(a.X, b.X),
                Math.Min(a.Y, b.Y),
                Math.Abs(b.X - a.X),
                Math.Abs(b.Y - a.Y));
            foreach (var item in DesktopFileSurface.GetItemsIntersecting(surfaceRect))
                selectedIds.Add(item.Id);
        }

        _desktopSelectedIds.Clear();
        foreach (var id in selectedIds)
            _desktopSelectedIds.Add(id);
        ApplyDesktopSelection(vm);
        e.Handled = true;
    }

    private void FileArea_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (IsMobilePlatform)
        {
            var selectionActivated = _mobileLongPressSelectionActivated;
            _mobileLongPressSelectionActivated = false;
            CancelMobileLongPress();

            if (selectionActivated && DataContext is MainViewModel vm)
            {
                ShowMobileSelectionActions(vm);
                e.Handled = true;
            }
            return;
        }

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

        var buttonWidth = FloatingUploadDragHost.Bounds.Width > 1 ? FloatingUploadDragHost.Bounds.Width : 48;
        var buttonHeight = FloatingUploadDragHost.Bounds.Height > 1 ? FloatingUploadDragHost.Bounds.Height : 48;
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

        var buttonWidth = FloatingUploadDragHost.Bounds.Width > 1 ? FloatingUploadDragHost.Bounds.Width : 48;
        var buttonHeight = FloatingUploadDragHost.Bounds.Height > 1 ? FloatingUploadDragHost.Bounds.Height : 48;
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

        var buttonWidth = FloatingUploadDragHost.Bounds.Width > 1 ? FloatingUploadDragHost.Bounds.Width : 48;
        var buttonHeight = FloatingUploadDragHost.Bounds.Height > 1 ? FloatingUploadDragHost.Bounds.Height : 48;
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
            if (SuppressTransientNetworkError(vm, ex))
                return;

            transfer.State = TransferState.Failed;
            transfer.Message = ex.Message;
            vm.ErrorMessage = ex.Message;
        }
    }

    private async void MobileSelectionDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await DownloadSelectedAsync(vm);
    }

    private async void MobileSelectionCache_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var selected = vm.SelectedItemsSnapshot.ToArray();
        if (selected.Length > 0)
            await vm.CacheItemsAsync(selected);
    }

    private async void MobileSelectionShare_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var selected = vm.SelectedItemsSnapshot.ToArray();
        if (selected.Length == 0)
            return;

        vm.IsBusy = true;
        try
        {
            var lines = new List<string>(selected.Length * 2);
            foreach (var item in selected)
            {
                var link = await AppServices.OneDrive.CreateShareLinkAsync(item.Id);
                lines.Add(item.Name);
                lines.Add(link);
            }

            var text = string.Join(Environment.NewLine, lines);
            if (AppServices.PlatformShareService is { } shareService)
            {
                await shareService.ShareTextAsync(selected.Length == 1 ? selected[0].Name : $"分享 {selected.Length} 个 OneDrive 项目", text);
                vm.StatusText = "已打开系统分享面板";
            }
            else if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                var clipboardData = new DataTransfer();
                clipboardData.Add(DataTransferItem.CreateText(text));
                await clipboard.SetDataAsync(clipboardData);
                vm.StatusText = "分享链接已复制到剪贴板";
            }
        }
        catch (Exception ex)
        {
            if (SuppressTransientNetworkError(vm, ex))
                return;

            vm.ErrorMessage = ex.Message;
            vm.StatusText = "创建分享链接失败";
        }
        finally
        {
            vm.IsBusy = false;
        }
    }

    private async void MobileSelectionMove_Click(object? sender, RoutedEventArgs e) =>
        await OpenMobileDestinationPickerAsync(MobileDestinationOperation.Move);

    private async void MobileSelectionCopy_Click(object? sender, RoutedEventArgs e) =>
        await OpenMobileDestinationPickerAsync(MobileDestinationOperation.Copy);

    private void MobileSelectionDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.BeginDeleteCommand.Execute(null);
    }

    private async Task OpenMobileDestinationPickerAsync(MobileDestinationOperation operation)
    {
        if (!IsMobilePlatform || operation == MobileDestinationOperation.None || DataContext is not MainViewModel vm)
            return;

        var selected = vm.SelectedItemsSnapshot.ToArray();
        if (selected.Length == 0)
            return;

        _mobileDestinationOperation = operation;
        _mobileDestinationPendingItems = selected;
        MobileSelectionActionBar.IsVisible = false;
        MobileDestinationTitle.Text = operation == MobileDestinationOperation.Move ? "移动到" : "复制到";
        MobileDestinationConfirmButton.Content = operation == MobileDestinationOperation.Move ? "移动到这里" : "复制到这里";
        MobileDestinationOverlay.IsVisible = true;
        UpdateNativeMobileFileListVisibility();
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
            // A newer destination navigation or closing the picker superseded this request.
        }
        catch (Exception ex)
        {
            if (SuppressTransientNetworkError(vm, ex))
                return;

            vm.ErrorMessage = ex.Message;
            CloseMobileDestinationPicker();
        }
    }

    private CancellationToken BeginMobileDestinationNavigation()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _mobileDestinationNavigationCts, next);
        previous?.Cancel();
        Interlocked.Increment(ref _mobileDestinationNavigationVersion);
        return next.Token;
    }

    private async Task NavigateMobileDestinationAsync(string folderId)
    {
        var token = BeginMobileDestinationNavigation();
        var version = _mobileDestinationNavigationVersion;
        var vm = DataContext as MainViewModel;
        if (vm is not null)
            vm.IsBusy = true;

        try
        {
            await LoadMobileDestinationFoldersAsync(folderId, token);
            if (token.IsCancellationRequested || version != _mobileDestinationNavigationVersion || !MobileDestinationOverlay.IsVisible)
                return;

            _mobileDestinationFolderId = folderId;
        }
        finally
        {
            if (vm is not null && version == _mobileDestinationNavigationVersion)
                vm.IsBusy = false;
        }
    }

    private async Task LoadMobileDestinationFoldersAsync(string folderId, CancellationToken cancellationToken)
    {
        var selectedFolderIds = _mobileDestinationPendingItems
            .Where(static x => x.IsFolder)
            .Select(static x => x.Id)
            .ToHashSet(StringComparer.Ordinal);

        // Use the destination-specific Graph query: it requests only folder metadata, no
        // thumbnails/files payload. The service still follows @odata.nextLink, so a folder
        // containing more than 200 children is not truncated to the first Graph page.
        var folders = (await AppServices.OneDrive.GetChildFoldersAsync(folderId, cancellationToken))
            .Where(x => !selectedFolderIds.Contains(x.Id))
            .OrderBy(static x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        _mobileDestinationFolders.Clear();
        _mobileDestinationFolders.AddRange(folders);
    }

    private async void MobileDestinationFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DriveItemModel item } || !item.IsFolder ||
            DataContext is not MainViewModel vm || string.IsNullOrWhiteSpace(item.Id))
            return;

        e.Handled = true;
        var oldCount = _mobileDestinationBreadcrumbItems.Count;
        _mobileDestinationBreadcrumbItems.Add(new BreadcrumbItem(item.Name, item.Id));
        try
        {
            await NavigateMobileDestinationAsync(item.Id);
        }
        catch (OperationCanceledException)
        {
            // Superseded by another tap/back/close.
        }
        catch (Exception ex)
        {
            // Only remove the breadcrumb we optimistically appended if it still belongs to
            // this failed navigation; a newer navigation may already have replaced the path.
            while (_mobileDestinationBreadcrumbItems.Count > oldCount)
                _mobileDestinationBreadcrumbItems.RemoveAt(_mobileDestinationBreadcrumbItems.Count - 1);
            if (!SuppressTransientNetworkError(vm, ex))
                vm.ErrorMessage = ex.Message;
        }
    }

    private async void MobileDestinationBreadcrumb_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BreadcrumbItem crumb } || string.IsNullOrWhiteSpace(crumb.ItemId) ||
            DataContext is not MainViewModel vm)
            return;

        var index = _mobileDestinationBreadcrumbItems.IndexOf(crumb);
        if (index < 0 || index == _mobileDestinationBreadcrumbItems.Count - 1)
            return;

        var oldPath = _mobileDestinationBreadcrumbItems.ToArray();
        while (_mobileDestinationBreadcrumbItems.Count > index + 1)
            _mobileDestinationBreadcrumbItems.RemoveAt(_mobileDestinationBreadcrumbItems.Count - 1);

        try
        {
            await NavigateMobileDestinationAsync(crumb.ItemId);
        }
        catch (OperationCanceledException)
        {
            // Superseded by another navigation.
        }
        catch (Exception ex)
        {
            _mobileDestinationBreadcrumbItems.Clear();
            _mobileDestinationBreadcrumbItems.AddRange(oldPath);
            if (!SuppressTransientNetworkError(vm, ex))
                vm.ErrorMessage = ex.Message;
        }
    }

    private async Task<bool> GoBackMobileDestinationAsync()
    {
        if (!MobileDestinationOverlay.IsVisible || _mobileDestinationBreadcrumbItems.Count <= 1 ||
            DataContext is not MainViewModel vm)
            return false;

        var oldPath = _mobileDestinationBreadcrumbItems.ToArray();
        _mobileDestinationBreadcrumbItems.RemoveAt(_mobileDestinationBreadcrumbItems.Count - 1);
        var parent = _mobileDestinationBreadcrumbItems[^1];
        if (string.IsNullOrWhiteSpace(parent.ItemId))
            return false;

        try
        {
            await NavigateMobileDestinationAsync(parent.ItemId);
            return true;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            _mobileDestinationBreadcrumbItems.Clear();
            _mobileDestinationBreadcrumbItems.AddRange(oldPath);
            if (!SuppressTransientNetworkError(vm, ex))
                vm.ErrorMessage = ex.Message;
            return true;
        }
    }

    private async void MobileDestinationConfirm_Click(object? sender, RoutedEventArgs e)
    {
        if (_mobileDestinationOperation == MobileDestinationOperation.None ||
            string.IsNullOrWhiteSpace(_mobileDestinationFolderId) ||
            DataContext is not MainViewModel vm)
            return;

        var operation = _mobileDestinationOperation;
        var targetId = _mobileDestinationFolderId;
        var pending = _mobileDestinationPendingItems.ToArray();
        vm.IsBusy = true;
        try
        {
            foreach (var item in pending)
            {
                if (operation == MobileDestinationOperation.Move)
                    await AppServices.OneDrive.MoveAsync(item.Id, targetId);
                else
                    await AppServices.OneDrive.CopyAsync(item.Id, targetId);
            }

            vm.InvalidateFolderCacheForId(targetId);
            vm.InvalidateFolderCacheForId(vm.CurrentFolderId);
            CloseMobileDestinationPicker();
            await vm.RefreshCurrentFolderAsync();
            ClearListSelections();
            vm.StatusText = operation == MobileDestinationOperation.Move
                ? $"已移动 {pending.Length} 项"
                : $"已提交 {pending.Length} 项复制任务";
        }
        catch (Exception ex)
        {
            if (SuppressTransientNetworkError(vm, ex))
                return;

            vm.ErrorMessage = ex.Message;
            vm.StatusText = operation == MobileDestinationOperation.Move ? "移动失败" : "复制失败";
        }
        finally
        {
            vm.IsBusy = false;
        }
    }

    private void MobileDestinationCancel_Click(object? sender, RoutedEventArgs e) => CloseMobileDestinationPicker();

    private void CloseMobileDestinationPicker()
    {
        Interlocked.Increment(ref _mobileDestinationNavigationVersion);
        var navigationCts = Interlocked.Exchange(ref _mobileDestinationNavigationCts, null);
        navigationCts?.Cancel();
        navigationCts?.Dispose();

        MobileDestinationOverlay.IsVisible = false;
        UpdateNativeMobileFileListVisibility();
        _mobileDestinationOperation = MobileDestinationOperation.None;
        _mobileDestinationFolderId = null;
        _mobileDestinationPendingItems = Array.Empty<DriveItemModel>();
        _mobileDestinationFolders.Clear();
        _mobileDestinationBreadcrumbItems.Clear();
        if (DataContext is MainViewModel vm)
        {
            vm.IsBusy = false;
            if (vm.SelectionCount > 0)
                ShowMobileSelectionActions(vm);
        }
    }

    private async void OpenSelectedButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { SelectedItem: { } item } vm)
            await OpenDriveItemAsync(vm, item);
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
            if (SuppressTransientNetworkError(vm, ex))
                return;

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
            if (SuppressTransientNetworkError(vm, ex))
                return;

            transfer.State = TransferState.Failed;
            transfer.Message = ex.Message;
            vm.ErrorMessage = ex.Message;
        }
    }

    private void DownloadAllFilesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel)
            return;

        DownloadAllConfirmOverlay.IsVisible = true;
        UpdateNativeMobileFileListVisibility();
    }

    private void CancelDownloadAllButton_Click(object? sender, RoutedEventArgs e)
    {
        DownloadAllConfirmOverlay.IsVisible = false;
        UpdateNativeMobileFileListVisibility();
    }

    private async void ConfirmDownloadAllButton_Click(object? sender, RoutedEventArgs e)
    {
        DownloadAllConfirmOverlay.IsVisible = false;
        UpdateNativeMobileFileListVisibility();
        if (DataContext is MainViewModel vm)
            await DownloadAllOneDriveAsync(vm);
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

        var plans = new List<DownloadPlan>();
        vm.IsBusy = true;
        try
        {
            var rootItems = await AppServices.OneDrive.GetChildrenAsync(null);
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
        }
        finally
        {
            vm.IsBusy = false;
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
        CancelPreviewZoomAnimation();
        if (DataContext is not MainViewModel vm || !vm.IsImagePreview)
            return;

        // Mobile touch is handled by Avalonia's pinch/swipe/scroll recognizers below.
        // Capturing the first touch pointer here would prevent a second finger from
        // joining a pinch gesture. Keep this low-level path for desktop mouse panning.
        if (IsMobilePlatform && e.Pointer.Type == PointerType.Touch)
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

    private async void PreviewImage_DoubleTapped(object? sender, TappedEventArgs e)
    {
        CancelPreviewLongPress();
        CancelPreviewZoomAnimation();
        if (DataContext is not MainViewModel vm || !vm.IsImagePreview || vm.PreviewImage is null)
            return;

        e.Handled = true;
        var viewportWidth = PreviewImageViewport.Bounds.Width;
        var viewportHeight = PreviewImageViewport.Bounds.Height;
        if (viewportWidth <= 1 || viewportHeight <= 1)
            return;

        var tapPoint = e.GetPosition(PreviewImageViewport);

        if (_previewAutoFit)
        {
            // On mobile the fitted image normally lives in the Carousel. Switch to the zoom canvas
            // at exactly the same fitted scale first, then animate from that visual state.
            if (IsMobilePlatform)
                EnterMobilePreviewZoomMode(vm);
            else
                FitPreviewImageToViewport(vm);

            var startZoom = Math.Max(0.01, vm.PreviewZoom);
            var startLeft = GetCanvasCoordinate(PreviewImageElement, true,
                (viewportWidth - vm.PreviewImageWidth) / 2);
            var startTop = GetCanvasCoordinate(PreviewImageElement, false,
                (viewportHeight - vm.PreviewImageHeight) / 2);

            // Preserve the old "actual size" behaviour for normal photos. If an unusually small
            // image is already >= 100% while fitted, still make a double-tap an actual zoom-in.
            var targetZoom = Math.Clamp(Math.Max(1.0, startZoom * 2.0), 0.01, 8.0);
            var sourceWidth = vm.PreviewImageWidth / startZoom;
            var sourceHeight = vm.PreviewImageHeight / startZoom;
            var imageX = Math.Clamp((tapPoint.X - startLeft) / startZoom, 0, sourceWidth);
            var imageY = Math.Clamp((tapPoint.Y - startTop) / startZoom, 0, sourceHeight);

            _previewAutoFit = false;
            await AnimatePreviewZoomAsync(
                vm,
                startZoom,
                targetZoom,
                startLeft,
                startTop,
                tapPoint,
                imageX,
                imageY,
                zoomAroundTapPoint: true,
                exitMobileZoomModeAfter: false);
        }
        else
        {
            var startZoom = Math.Max(0.01, vm.PreviewZoom);
            var startLeft = GetCanvasCoordinate(PreviewImageElement, true,
                (viewportWidth - vm.PreviewImageWidth) / 2);
            var startTop = GetCanvasCoordinate(PreviewImageElement, false,
                (viewportHeight - vm.PreviewImageHeight) / 2);

            var sourceWidth = vm.PreviewImageWidth / startZoom;
            var sourceHeight = vm.PreviewImageHeight / startZoom;
            if (sourceWidth <= 0 || sourceHeight <= 0)
                return;

            var targetZoom = CalculatePreviewFitZoom(sourceWidth, sourceHeight, viewportWidth, viewportHeight);
            var targetWidth = sourceWidth * targetZoom;
            var targetHeight = sourceHeight * targetZoom;
            var targetLeft = (viewportWidth - targetWidth) / 2;
            var targetTop = (viewportHeight - targetHeight) / 2;

            // Keep the zoom canvas visible until the shrink animation is completely finished.
            // On mobile we then hand the identical fitted frame back to the Carousel, so there is
            // no final pop or layout jump.
            _previewAutoFit = true;
            await AnimatePreviewZoomAsync(
                vm,
                startZoom,
                targetZoom,
                startLeft,
                startTop,
                new Point(targetLeft, targetTop),
                0,
                0,
                zoomAroundTapPoint: false,
                exitMobileZoomModeAfter: IsMobilePlatform && !IsCurrentPreviewGif(vm));
        }
    }

    private async Task AnimatePreviewZoomAsync(
        MainViewModel vm,
        double startZoom,
        double targetZoom,
        double startLeft,
        double startTop,
        Point targetOrAnchor,
        double anchorImageX,
        double anchorImageY,
        bool zoomAroundTapPoint,
        bool exitMobileZoomModeAfter)
    {
        CancelPreviewZoomAnimation();
        var cts = new CancellationTokenSource();
        _previewZoomAnimationCts = cts;
        var token = cts.Token;
        var started = DateTime.UtcNow;

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(DataContext, vm) || !vm.IsImagePreview || vm.PreviewImage is null)
                    return;

                var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
                var progress = Math.Clamp(elapsed / PreviewDoubleTapAnimationMilliseconds, 0.0, 1.0);
                // SmoothStep has zero velocity at both ends, so the image does not "jump" into
                // the zoom or stop abruptly when it reaches the target scale.
                var eased = progress * progress * (3.0 - 2.0 * progress);
                var zoom = Lerp(startZoom, targetZoom, eased);

                vm.SetPreviewZoomAbsolute(zoom);

                double left;
                double top;
                if (zoomAroundTapPoint)
                {
                    // The image coordinate under the user's finger remains under that same screen
                    // coordinate throughout the zoom. Only edge clamping is allowed to move it.
                    left = targetOrAnchor.X - anchorImageX * zoom;
                    top = targetOrAnchor.Y - anchorImageY * zoom;
                }
                else
                {
                    left = Lerp(startLeft, targetOrAnchor.X, eased);
                    top = Lerp(startTop, targetOrAnchor.Y, eased);
                }

                (left, top) = ConstrainPreviewPosition(
                    left,
                    top,
                    vm.PreviewImageWidth,
                    vm.PreviewImageHeight,
                    PreviewImageViewport.Bounds.Width,
                    PreviewImageViewport.Bounds.Height);
                Canvas.SetLeft(PreviewImageElement, left);
                Canvas.SetTop(PreviewImageElement, top);

                if (progress >= 1.0)
                    break;

                await Task.Delay(16, token);
            }

            if (exitMobileZoomModeAfter && IsMobilePlatform && !IsCurrentPreviewGif(vm))
                ExitMobilePreviewZoomMode(vm);

            UpdatePreviewTouchGestureMode(vm);
        }
        catch (OperationCanceledException)
        {
            // A pinch, pan, wheel gesture, image change, close, or another double-tap takes over
            // immediately from the current interpolated frame.
        }
        finally
        {
            if (ReferenceEquals(_previewZoomAnimationCts, cts))
                _previewZoomAnimationCts = null;
            cts.Dispose();
        }
    }

    private void CancelPreviewZoomAnimation()
    {
        var cts = _previewZoomAnimationCts;
        if (cts is null)
            return;

        _previewZoomAnimationCts = null;
        cts.Cancel();
    }

    private static double CalculatePreviewFitZoom(
        double sourceWidth,
        double sourceHeight,
        double viewportWidth,
        double viewportHeight)
    {
        var horizontalScale = Math.Max(0.01, (viewportWidth - 20) / sourceWidth);
        var verticalScale = Math.Max(0.01, (viewportHeight - 20) / sourceHeight);
        return Math.Clamp(Math.Min(horizontalScale, verticalScale), 0.01, 8.0);
    }

    private static (double Left, double Top) ConstrainPreviewPosition(
        double left,
        double top,
        double width,
        double height,
        double viewportWidth,
        double viewportHeight)
    {
        if (viewportWidth <= 1 || viewportHeight <= 1)
            return (left, top);

        left = width <= viewportWidth
            ? (viewportWidth - width) / 2
            : Math.Clamp(left, viewportWidth - width, 0);
        top = height <= viewportHeight
            ? (viewportHeight - height) / 2
            : Math.Clamp(top, viewportHeight - height, 0);
        return (left, top);
    }

    private static double Lerp(double from, double to, double progress) => from + (to - from) * progress;

    private void PreviewImage_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        CancelPreviewZoomAnimation();
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
        UpdatePreviewTouchGestureMode(vm);
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
        UpdatePreviewTouchGestureMode(vm);
    }

    private void PreviewImage_Pinch(object? sender, PinchEventArgs e)
    {
        CancelPreviewZoomAnimation();
        CancelPreviewLongPress();
        if (!IsMobilePlatform || DataContext is not MainViewModel vm || !vm.IsImagePreview)
            return;

        _previewPinching = true;
        if (!_mobilePreviewZoomMode)
            EnterMobilePreviewZoomMode(vm);
        _previewAutoFit = false;

        var oldZoom = Math.Max(0.01, vm.PreviewZoom);
        var oldLeft = GetCanvasCoordinate(PreviewImageElement, true,
            (PreviewImageViewport.Bounds.Width - vm.PreviewImageWidth) / 2);
        var oldTop = GetCanvasCoordinate(PreviewImageElement, false,
            (PreviewImageViewport.Bounds.Height - vm.PreviewImageHeight) / 2);

        var origin = e.ScaleOrigin;
        var imageX = (origin.X - oldLeft) / oldZoom;
        var imageY = (origin.Y - oldTop) / oldZoom;

        var currentScale = Math.Max(0.01, e.Scale);
        var factor = currentScale / Math.Max(0.01, _previewLastPinchScale);
        _previewLastPinchScale = currentScale;
        vm.AdjustPreviewZoom(factor);

        var newZoom = Math.Max(0.01, vm.PreviewZoom);
        Canvas.SetLeft(PreviewImageElement, origin.X - imageX * newZoom);
        Canvas.SetTop(PreviewImageElement, origin.Y - imageY * newZoom);
        ConstrainPreviewImageToViewport(vm);
        UpdatePreviewTouchGestureMode(vm);
        e.Handled = true;
    }

    private void PreviewImage_PinchEnded(object? sender, PinchEndedEventArgs e)
    {
        _previewLastPinchScale = 1.0;
        _previewPinching = false;
        if (DataContext is MainViewModel vm && vm.IsImagePreview)
        {
            ConstrainPreviewImageToViewport(vm);
            if (IsMobilePlatform && IsPreviewImageFitted(vm) && !IsCurrentPreviewGif(vm))
            {
                _previewAutoFit = true;
                ExitMobilePreviewZoomMode(vm);
            }
            UpdatePreviewTouchGestureMode(vm);
        }
        e.Handled = true;
    }

    private void PreviewImage_ScrollGesture(object? sender, ScrollGestureEventArgs e)
    {
        CancelPreviewZoomAnimation();
        CancelPreviewLongPress();
        if (!IsMobilePlatform || _previewPinching || DataContext is not MainViewModel vm ||
            !vm.IsImagePreview || IsPreviewImageFitted(vm))
            return;

        var left = GetCanvasCoordinate(PreviewImageElement, true,
            (PreviewImageViewport.Bounds.Width - vm.PreviewImageWidth) / 2);
        var top = GetCanvasCoordinate(PreviewImageElement, false,
            (PreviewImageViewport.Bounds.Height - vm.PreviewImageHeight) / 2);

        // Avalonia's ScrollGesture delta follows scroll-content semantics (positive delta means
        // the content viewport scrolls in the opposite direction). For a gallery image we want
        // direct manipulation: drag your finger right/down and the enlarged image follows it.
        Canvas.SetLeft(PreviewImageElement, left - e.Delta.X);
        Canvas.SetTop(PreviewImageElement, top - e.Delta.Y);
        ConstrainPreviewImageToViewport(vm);
        e.Handled = true;
    }

    private bool IsPreviewImageFitted(MainViewModel vm)
    {
        var viewportWidth = PreviewImageViewport.Bounds.Width;
        var viewportHeight = PreviewImageViewport.Bounds.Height;
        return vm.PreviewImageWidth <= viewportWidth + 2 && vm.PreviewImageHeight <= viewportHeight + 2;
    }

    private void UpdatePreviewTouchGestureMode(MainViewModel vm)
    {
        if (!IsMobilePlatform)
            return;

        var fitted = !vm.IsImagePreview || IsPreviewImageFitted(vm);
        var useCarousel = vm.IsImagePreview && !_mobilePreviewZoomMode && !IsCurrentPreviewGif(vm);

        // Carousel pages render their images with Stretch=Uniform, so the page is always
        // viewport-fitted regardless of the hidden zoom canvas' PreviewImageWidth/Height.
        // Using IsPreviewImageFitted here caused the first carousel navigation to disable
        // IsSwipeEnabled as soon as the newly loaded full-resolution bitmap updated those
        // hidden canvas dimensions. Keep carousel paging and zoom-canvas panning independent.
        MobileImageCarousel.IsSwipeEnabled = useCarousel;
        _previewScrollGestureRecognizer.CanHorizontallyScroll = vm.IsImagePreview && _mobilePreviewZoomMode && !fitted;
        _previewScrollGestureRecognizer.CanVerticallyScroll = vm.IsImagePreview && _mobilePreviewZoomMode && !fitted;
    }

    private static bool IsCurrentPreviewGif(MainViewModel vm) =>
        string.Equals(Path.GetExtension(vm.PreviewItem?.Name), ".gif", StringComparison.OrdinalIgnoreCase);

    private void SyncMobileImageCarousel(MainViewModel vm)
    {
        if (!IsMobilePlatform || !vm.IsImagePreview)
            return;

        var images = vm.GetVisibleLoadedItemsSnapshot().Where(static x => x.IsFile && x.IsImage).ToArray();
        var ids = images.Select(static x => x.Id).ToArray();
        var itemsChanged = ids.Length != _mobileCarouselImageIds.Length ||
            !ids.SequenceEqual(_mobileCarouselImageIds, StringComparer.Ordinal);

        _syncingMobileImageCarousel = true;
        try
        {
            if (itemsChanged)
            {
                _mobileCarouselImageIds = ids;
                MobileImageCarousel.ItemsSource = images;
            }

            if (vm.PreviewItem is not null)
            {
                var index = Array.FindIndex(images, x => x.Id == vm.PreviewItem.Id);
                if (index >= 0 && MobileImageCarousel.SelectedIndex != index)
                    MobileImageCarousel.SelectedIndex = index;
            }
        }
        finally
        {
            _syncingMobileImageCarousel = false;
        }
    }

    private void ApplyMobileImagePreviewMode(MainViewModel vm)
    {
        if (!IsMobilePlatform)
            return;

        var showCarousel = vm.IsImagePreview && !_mobilePreviewZoomMode && !IsCurrentPreviewGif(vm);
        MobileImageCarousel.IsVisible = showCarousel;
        PreviewZoomCanvas.IsVisible = vm.IsImagePreview && !showCarousel;
        UpdatePreviewTouchGestureMode(vm);
    }

    private void EnterMobilePreviewZoomMode(MainViewModel vm)
    {
        if (!IsMobilePlatform || !vm.IsImagePreview)
            return;

        _mobilePreviewZoomMode = true;
        MobileImageCarousel.IsSwipeEnabled = false;
        MobileImageCarousel.IsVisible = false;
        PreviewZoomCanvas.IsVisible = true;
        if (_previewAutoFit)
            FitPreviewImageToViewport(vm);
    }

    private void ExitMobilePreviewZoomMode(MainViewModel vm)
    {
        if (!IsMobilePlatform || !vm.IsImagePreview || IsCurrentPreviewGif(vm))
            return;

        _mobilePreviewZoomMode = false;
        PreviewZoomCanvas.IsVisible = false;
        MobileImageCarousel.IsVisible = true;
        SyncMobileImageCarousel(vm);
        MobileImageCarousel.IsSwipeEnabled = true;
    }

    private async void MobileImageCarousel_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        CancelPreviewLongPress();
        if (!IsMobilePlatform || _syncingMobileImageCarousel || _mobilePreviewZoomMode ||
            DataContext is not MainViewModel vm || !vm.IsImagePreview ||
            MobileImageCarousel.SelectedItem is not DriveItemModel item ||
            string.Equals(item.Id, vm.PreviewItem?.Id, StringComparison.Ordinal))
            return;

        // Carousel owns the direct-manipulation animation. Only after it commits the new page do
        // we switch the preview model and upgrade that page from thumbnail/preload to full preview.
        _previewAutoFit = true;
        try
        {
            await vm.LoadPreviewAsync(item, preserveSlideshow: vm.IsSlideshowPlaying);
        }
        catch
        {
            // LoadPreviewAsync reports its own error state; keep the carousel responsive.
        }
    }

    private void ConstrainPreviewImageToViewport(MainViewModel vm)
    {
        var viewportWidth = PreviewImageViewport.Bounds.Width;
        var viewportHeight = PreviewImageViewport.Bounds.Height;
        if (viewportWidth <= 1 || viewportHeight <= 1)
            return;

        var width = vm.PreviewImageWidth;
        var height = vm.PreviewImageHeight;
        var left = GetCanvasCoordinate(PreviewImageElement, true, (viewportWidth - width) / 2);
        var top = GetCanvasCoordinate(PreviewImageElement, false, (viewportHeight - height) / 2);

        left = width <= viewportWidth
            ? (viewportWidth - width) / 2
            : Math.Clamp(left, viewportWidth - width, 0);
        top = height <= viewportHeight
            ? (viewportHeight - height) / 2
            : Math.Clamp(top, viewportHeight - height, 0);

        Canvas.SetLeft(PreviewImageElement, left);
        Canvas.SetTop(PreviewImageElement, top);
    }

    private static double GetCanvasCoordinate(Control control, bool isLeft, double fallback)
    {
        var value = isLeft ? Canvas.GetLeft(control) : Canvas.GetTop(control);
        return double.IsNaN(value) ? fallback : value;
    }

    private void PreviewMore_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsMobilePlatform)
            return;

        CancelPreviewLongPress();
        OpenMobilePreviewActions();
        e.Handled = true;
    }

    private void OpenMobilePreviewActions()
    {
        if (!IsMobilePlatform || MobilePreviewActionsOverlay.IsVisible ||
            DataContext is not MainViewModel { IsPreviewVisible: true })
            return;

        // Native video surfaces are composed above Avalonia. Hide them while the
        // Avalonia action panel is visible, just as the old ContextMenu path did.
        _mobilePreviewActionsOverlayController = _embeddedMediaSession as IEmbeddedMediaOverlayController;
        _mobilePreviewActionsOverlayController?.SetNativeOverlayVisible(false);
        MobilePreviewActionsOverlay.IsVisible = true;
        UpdateNativeMobileFileListVisibility();
    }

    private void CloseMobilePreviewActions()
    {
        CancelPreviewLongPress();

        if (!MobilePreviewActionsOverlay.IsVisible && _mobilePreviewActionsOverlayController is null)
            return;

        MobilePreviewActionsOverlay.IsVisible = false;
        UpdateNativeMobileFileListVisibility();
        var overlayController = _mobilePreviewActionsOverlayController;
        _mobilePreviewActionsOverlayController = null;

        if (overlayController is not null)
        {
            Dispatcher.UIThread.Post(
                () => overlayController.SetNativeOverlayVisible(true),
                DispatcherPriority.Background);
        }
    }

    private void MobilePreviewActionsBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseMobilePreviewActions();
        e.Handled = true;
    }

    private void PreviewLongPress_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsMobilePlatform || e.Pointer.Type != PointerType.Touch ||
            DataContext is not MainViewModel { IsPreviewVisible: true, IsImagePreview: true } ||
            MobilePreviewActionsOverlay.IsVisible)
            return;

        _previewLongPressPending = true;
        _previewLongPressStart = e.GetPosition(PreviewImageViewport);
        _previewLongPressTimer.Stop();
        _previewLongPressTimer.Start();
    }

    private void PreviewLongPress_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_previewLongPressPending)
            return;

        var current = e.GetPosition(PreviewImageViewport);
        var dx = current.X - _previewLongPressStart.X;
        var dy = current.Y - _previewLongPressStart.Y;

        // 12 dp is well below a deliberate swipe/pan but large enough to tolerate
        // small finger jitter during a hold.
        if ((dx * dx) + (dy * dy) > 12 * 12 ||
            _previewPinching)
        {
            CancelPreviewLongPress();
        }
    }

    private void PreviewLongPress_PointerReleased(object? sender, PointerReleasedEventArgs e) =>
        CancelPreviewLongPress();

    private void PreviewLongPressTimer_Tick(object? sender, EventArgs e)
    {
        _previewLongPressTimer.Stop();
        if (!_previewLongPressPending ||
            _previewPinching ||
            DataContext is not MainViewModel { IsPreviewVisible: true, IsImagePreview: true })
        {
            CancelPreviewLongPress();
            return;
        }

        _previewLongPressPending = false;
        OpenMobilePreviewActions();
    }

    private void CancelPreviewLongPress()
    {
        _previewLongPressTimer.Stop();
        _previewLongPressPending = false;
    }

    private void PreviewImage_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // On phones, image long-press is reserved for direct manipulation and must not open a
        // desktop-style context menu. The mobile ⋮ button and a stationary image long-press
        // both open the in-page action panel. Desktop right-click still works normally.
        if (IsMobilePlatform)
            e.Handled = true;
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
        CloseMobilePreviewActions();
        if (DataContext is MainViewModel vm)
            vm.ToggleSlideshowCommand.Execute(null);
    }

    private void PreviewDetails_Click(object? sender, RoutedEventArgs e)
    {
        CloseMobilePreviewActions();
        if (DataContext is MainViewModel vm)
            vm.TogglePreviewDetailsCommand.Execute(null);
    }

    private async void PreviewCache_Click(object? sender, RoutedEventArgs e)
    {
        CloseMobilePreviewActions();
        if (DataContext is not MainViewModel { PreviewItem: { IsFile: true } item } vm)
            return;

        try
        {
            await vm.CacheItemsAsync(new[] { item });
        }
        catch (Exception ex)
        {
            if (SuppressTransientNetworkError(vm, ex))
                return;

            vm.ErrorMessage = ex.Message;
            vm.StatusText = "缓存失败";
        }
    }

    private async void PreviewShare_Click(object? sender, RoutedEventArgs e)
    {
        CloseMobilePreviewActions();

        if (DataContext is not MainViewModel { PreviewItem: { IsFile: true } item } vm)
            return;

        vm.IsBusy = true;
        try
        {
            var link = await AppServices.OneDrive.CreateShareLinkAsync(item.Id);
            if (AppServices.PlatformShareService is { } shareService)
            {
                await shareService.ShareTextAsync(item.Name, $"{item.Name}{Environment.NewLine}{link}");
                vm.StatusText = "已打开系统分享面板";
            }
            else if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                var clipboardData = new DataTransfer();
                clipboardData.Add(DataTransferItem.CreateText($"{item.Name}{Environment.NewLine}{link}"));
                await clipboard.SetDataAsync(clipboardData);
                vm.StatusText = "分享链接已复制到剪贴板";
            }
        }
        catch (Exception ex)
        {
            if (SuppressTransientNetworkError(vm, ex))
                return;

            vm.ErrorMessage = ex.Message;
            vm.StatusText = "创建分享链接失败";
        }
        finally
        {
            vm.IsBusy = false;
        }
    }

    private async void PreviewDownload_Click(object? sender, RoutedEventArgs e)
    {
        CloseMobilePreviewActions();
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
        await OpenCurrentPreviewWithSystemAppAsync();
    }

    private async void OpenPreviewWithSystemApp_Click(object? sender, RoutedEventArgs e)
    {
        await OpenCurrentPreviewWithSystemAppAsync();
    }

    private async Task OpenCurrentPreviewWithSystemAppAsync()
    {
        if (DataContext is not MainViewModel vm || vm.PreviewItem is not { IsFile: true } item)
            return;

        var path = vm.PreviewCachedFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            path = await vm.PrepareSystemOpenAsync(item);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        if (await TryLaunchSystemFileAsync(path))
        {
            vm.StatusText = $"已使用系统应用打开 {item.Name}";
            vm.ClosePreviewCommand.Execute(null);
            return;
        }

        vm.MarkSystemOpenUnsupported();
    }

    private async Task OpenWithSystemDefaultAsync(MainViewModel vm, DriveItemModel item)
    {
        var path = await vm.PrepareSystemOpenAsync(item);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        if (await TryLaunchSystemFileAsync(path))
        {
            vm.StatusText = $"已使用系统应用打开 {item.Name}";
            vm.ClosePreviewCommand.Execute(null);
            return;
        }

        // Keep the already-cached file attached to the generic preview. The user can retry
        // after installing/changing a default app without downloading the file again.
        vm.MarkSystemOpenUnsupported();
    }

    private async Task<bool> TryLaunchSystemFileAsync(string localPath)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return false;

        try
        {
            if (topLevel.StorageProvider is { } provider)
            {
                using var storageFile = await provider.TryGetFileFromPathAsync(localPath);
                if (storageFile is not null && await topLevel.Launcher.LaunchFileAsync(storageFile))
                    return true;
            }
        }
        catch
        {
            // Continue to the non-sandboxed desktop fallback below. Android/iOS use
            // LaunchFileAsync because the FileInfo extension is intentionally unsupported there.
        }

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsBrowser())
            return false;

        try
        {
            return await topLevel.Launcher.LaunchFileInfoAsync(new FileInfo(localPath));
        }
        catch
        {
            return false;
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
        if (IsMobilePlatform)
            return;

        var normalized = Math.Clamp(double.IsFinite(percent) ? percent : 50, 0, 100);
        // Desktop settings keeps the stronger slide-over acrylic treatment.
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
        MobileProfileBackgroundImage.Effect = radius <= 0.01 ? null : new BlurEffect { Radius = radius };
        MobileTransferBackgroundImage.Effect = radius <= 0.01 ? null : new BlurEffect { Radius = radius };
        MobileSettingsBackgroundImage.Effect = radius <= 0.01 ? null : new BlurEffect { Radius = radius };
        BackgroundScrimLayer.Opacity = 0.08 + (0.39 * ratio);
        MobileProfileBackgroundScrim.Opacity = BackgroundScrimLayer.Opacity;
        MobileTransferBackgroundScrim.Opacity = BackgroundScrimLayer.Opacity;
        MobileSettingsBackgroundScrim.Opacity = BackgroundScrimLayer.Opacity;
    }

    private void ApplyStartupBackgroundShell(MainViewModel vm)
    {
        if (vm.Settings.BackgroundMode == WindowBackgroundMode.Color)
        {
            try
            {
                var color = Color.Parse(vm.Settings.BackgroundColor);
                SetBackgroundBitmap(null);
                SetBackgroundColor(new SolidColorBrush(color), preserveSolidColorAcrossTheme: true);
                return;
            }
            catch
            {
                // Fall back to the theme background when a persisted color is invalid.
            }
        }

        SetBackgroundBitmap(null);
        ApplyDefaultBackgroundColor();
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
        MobileProfileBackgroundColor.Background = brush;
        MobileTransferBackgroundColor.Background = brush;
        MobileSettingsBackgroundColor.Background = brush;
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
            _mobileProfileScrimBinding?.Dispose();
            _mobileProfileScrimBinding = null;
            _mobileTransferScrimBinding?.Dispose();
            _mobileTransferScrimBinding = null;
            _mobileSettingsScrimBinding?.Dispose();
            _mobileSettingsScrimBinding = null;
            BackgroundScrimLayer.Background = brush;
            MobileProfileBackgroundScrim.Background = brush;
            MobileTransferBackgroundScrim.Background = brush;
            MobileSettingsBackgroundScrim.Background = brush;
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
        _mobileProfileScrimBinding?.Dispose();
        _mobileTransferScrimBinding?.Dispose();
        _mobileSettingsScrimBinding?.Dispose();
        var backgroundResource = this.GetResourceObservable("SystemControlBackgroundAltHighBrush");
        _backgroundScrimBinding = BackgroundScrimLayer.Bind(Border.BackgroundProperty, backgroundResource);
        _mobileProfileScrimBinding = MobileProfileBackgroundScrim.Bind(Border.BackgroundProperty, backgroundResource);
        _mobileTransferScrimBinding = MobileTransferBackgroundScrim.Bind(Border.BackgroundProperty, backgroundResource);
        _mobileSettingsScrimBinding = MobileSettingsBackgroundScrim.Bind(Border.BackgroundProperty, backgroundResource);
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
        MobileSettingsBackgroundImage.Source = bitmap;
        MobileSettingsBackgroundImage.IsVisible = bitmap is not null && IsMobilePlatform;
        MobileProfileBackgroundImage.Source = bitmap;
        MobileProfileBackgroundImage.IsVisible = bitmap is not null;
        MobileTransferBackgroundImage.Source = bitmap;
        MobileTransferBackgroundImage.IsVisible = bitmap is not null && IsMobilePlatform;

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
