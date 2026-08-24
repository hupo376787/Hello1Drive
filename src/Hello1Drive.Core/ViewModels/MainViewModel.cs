using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hello1Drive.Models;
using Hello1Drive.Services;

namespace Hello1Drive.ViewModels;

public enum DriveItemOpenResult
{
    Handled,
    RequiresOfficialOneDriveHandoff
}

public partial class MainViewModel : ViewModelBase
{
    private const long TextPreviewLimit = 8L * 1024 * 1024;
    private static readonly TimeSpan FolderCacheValidationInterval = TimeSpan.FromSeconds(30);
    private static bool IsMobilePlatform => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
    private static bool UsesNativeMobileFileList => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
    // Both desktop and mobile use Graph's full 200-item page. The visual surfaces are virtualized
    // and their logical slot count is established independently from page arrival, so a larger page
    // reduces Graph/request churn without forcing 200 controls to be realized at once.
    private const int CurrentFolderPageSize = 200;
    private volatile bool _mobileListScrolling;
    private volatile bool _desktopListScrolling;
    private volatile HashSet<int> _desktopThumbnailVisibleSlotIndices = new();
    private volatile HashSet<string> _desktopThumbnailWantedIds = new(StringComparer.Ordinal);
    private int _mobileThumbnailWindowFrom = -1;
    private int _mobileThumbnailWindowToExclusive = -1;
    private int _mobileThumbnailVisibleFrom = -1;
    private int _mobileThumbnailVisibleToExclusive = -1;
    private bool _mobileThumbnailWindowWasScrolling;
    // Immutable-by-convention snapshots replaced as a whole on the UI thread. Background thumbnail
    // workers only read them, so stale off-screen work can cheaply self-cancel after a long fling.
    private volatile HashSet<string> _mobileThumbnailWantedIds = new(StringComparer.Ordinal);
    private volatile HashSet<string> _mobileThumbnailVisibleIds = new(StringComparer.Ordinal);

    private readonly IOneDriveService _oneDrive;
    private readonly IAuthenticationService _authentication;
    private readonly AppSettingsService _settingsService;
    private readonly FileCacheService _fileCache;
    private readonly ThumbnailCacheService _thumbnailCache;
    private readonly TransferPersistenceService _transferPersistence;
    private readonly StartupSnapshotService _startupSnapshot;
    private readonly LocalDriveIndexService _localDriveIndex;
    private readonly IStartupRegistrationService? _startupRegistrationService;
    private readonly ITransferBackgroundService? _transferBackgroundService;
    private readonly List<DriveItemModel> _allItems = [];
    private readonly HashSet<string> _currentItemIds = new(StringComparer.Ordinal);

    // Keep recently decoded mobile thumbnails alive instead of disposing them as soon as they
    // leave the viewport. This removes the placeholder flash when the user scrolls back while
    // keeping the decoded bitmap memory bounded.
    private const int MobileDecodedThumbnailCacheLimit = 360;
    private readonly LinkedList<DriveItemModel> _mobileThumbnailLru = [];
    private readonly Dictionary<string, LinkedListNode<DriveItemModel>> _mobileThumbnailLruNodes = new(StringComparer.Ordinal);

    private readonly Dictionary<string, FolderCacheEntry> _folderCache = new(StringComparer.Ordinal);
    private long _folderNavigationVersion;
    private long _folderNavigationBusyVersion;
    private CancellationTokenSource? _folderNavigationCts;
    private CancellationTokenSource? _folderMetadataSyncCts;
    private CancellationTokenSource? _driveIndexSyncCts;
    private readonly List<DriveItemModel> _selectedItems = [];
    private Func<string?, Task>? _promptAction;
    private bool _promptUseBusy = true;
    private bool _initialized;
    private CancellationTokenSource? _thumbnailLoadCts;
    private long _thumbnailLoadGeneration;
    private readonly ConcurrentDictionary<string, long> _thumbnailLoadsInFlight = new(StringComparer.Ordinal);
    // These gates are shared by every thumbnail batch. A per-batch SemaphoreSlim lets several
    // 90ms viewport batches overlap and silently defeats the intended concurrency limit.
    private readonly SemaphoreSlim _mobileThumbnailWorkGate = new(2, 2);
    private readonly SemaphoreSlim _mobileFlingThumbnailGate = new(1, 1);
    // Four desktop workers are enough to fill a three-viewport look-ahead window without
    // letting cached bitmap decode compete too aggressively with Avalonia layout/rendering.
    private readonly SemaphoreSlim _desktopThumbnailWorkGate = new(4, 4);
    private int _previewImagePixelWidth;
    private int _previewImagePixelHeight;
    private AnimatedGifData? _gifAnimation;
    private int _gifFrameIndex;
    private readonly DispatcherTimer _gifTimer = new();
    private readonly DispatcherTimer _slideshowTimer = new();
    private readonly DispatcherTimer _driveIndexRefreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private CancellationTokenSource? _previewLoadCts;
    private CancellationTokenSource? _previewPrefetchCts;
    private string? _nextChildrenLink;
    private CancellationTokenSource? _transferPersistenceCts;
    private CancellationTokenSource? _cacheStatusRefreshCts;
    private CancellationTokenSource? _startupSnapshotSaveCts;
    private readonly SemaphoreSlim _cacheTransferGate = new(2, 2);
    private FolderNavigationReason _nextNavigationReason = FolderNavigationReason.Initial;
    private bool _syncingBackgroundColor;
    private bool _syncingDefaultSortSetting;
    private bool _syncingStartWithWindowsSetting;
    private int? _currentFolderTotalItemCount;
    private bool _startupSnapshotRestored;
    private string _startupSnapshotAccountId = string.Empty;

    public AvaloniaList<DriveItemModel> Items { get; } = [];
    // Shared fixed-slot collection used by every file surface. The historic MobileItems name is
    // retained for source compatibility; desktop repeaters bind through VirtualItems.
    public AvaloniaList<VirtualDriveItemSlot> MobileItems { get; } = [];
    public AvaloniaList<VirtualDriveItemSlot> VirtualItems => MobileItems;
    public bool UseNativeAndroidFileList => OperatingSystem.IsAndroid();
    public bool UseAvaloniaMobileFileList => IsMobilePlatform && !UsesNativeMobileFileList;
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];
    public ObservableCollection<TransferItemModel> Transfers { get; } = [];

    public event EventHandler<FolderNavigationEventArgs>? FolderNavigating;
    public event EventHandler<FolderNavigationEventArgs>? FolderLoaded;

    public IReadOnlyList<string> ThemeOptions { get; } = ["跟随系统", "浅色", "深色"];
    public IReadOnlyList<string> BackgroundModeOptions { get; } = ["默认", "纯色", "本地图片", "图片 URL", "本地文件夹", "OneDrive 文件夹"];
    public IReadOnlyList<string> DefaultSortOptions { get; } =
        ["系统默认", "日期 · 升序", "日期 · 降序", "名称 · 升序", "名称 · 降序", "大小 · 升序", "大小 · 降序"];

    [ObservableProperty] private DriveItemModel? selectedItem;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isAuthenticated;
    [ObservableProperty] private string userDisplayName = string.Empty;
    [ObservableProperty] private string userEmail = string.Empty;
    [ObservableProperty] private Bitmap? userAvatar;
    [ObservableProperty] private string quotaText = string.Empty;
    [ObservableProperty] private long quotaUsedBytes;
    [ObservableProperty] private long quotaTotalBytes;
    [ObservableProperty] private string statusText = "未登录";
    [ObservableProperty] private string currentLocation = "OneDrive";
    [ObservableProperty] private string? errorMessage;

    [ObservableProperty] private bool isPromptVisible;
    [ObservableProperty] private bool isPromptInputVisible = true;
    [ObservableProperty] private string promptTitle = string.Empty;
    [ObservableProperty] private string promptMessage = string.Empty;
    [ObservableProperty] private string promptText = string.Empty;

    [ObservableProperty] private bool isLogoutConfirmVisible;
    [ObservableProperty] private bool isSettingsPanelVisible;
    [ObservableProperty] private bool isTransferPanelVisible;

    [ObservableProperty] private FileViewMode viewMode = FileViewMode.Details;
    [ObservableProperty] private FileSortColumn sortColumn = FileSortColumn.None;
    [ObservableProperty] private SortCycleState sortState = SortCycleState.Original;
    [ObservableProperty] private int selectionCount;

    [ObservableProperty] private string selectedThemeText = "跟随系统";
    [ObservableProperty] private string selectedBackgroundModeText = "默认";
    [ObservableProperty] private string backgroundColorText = "#F7F7F8";
    [ObservableProperty] private Color backgroundPickerColor = Color.Parse("#F7F7F8");
    [ObservableProperty] private string backgroundUrl = string.Empty;
    [ObservableProperty] private double backgroundIntervalMinutes = 5;
    [ObservableProperty] private double acrylicBlurPercent = 50;
    [ObservableProperty] private string localImageDisplayName = string.Empty;
    [ObservableProperty] private string localFolderDisplayName = string.Empty;
    [ObservableProperty] private string oneDriveBackgroundFolderName = string.Empty;
    [ObservableProperty] private bool rememberLastFolder = true;
    [ObservableProperty] private bool showFloatingUploadButton = true;
    [ObservableProperty] private bool showToolbar = true;
    [ObservableProperty] private bool transparentFileItemBackground;
    [ObservableProperty] private string cacheStatusText = string.Empty;
    [ObservableProperty] private bool confirmBeforeDelete = true;
    [ObservableProperty] private bool useBuiltInViewer = true;
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private string selectedDefaultSortText = "系统默认";
    [ObservableProperty] private double slideshowIntervalSeconds = 5;
    [ObservableProperty] private bool limitDownloadSpeed;
    [ObservableProperty] private double downloadSpeedLimitKBps = 1024;
    [ObservableProperty] private bool limitUploadSpeed;
    [ObservableProperty] private double uploadSpeedLimitKBps = 1024;
    [ObservableProperty] private bool hasMoreItems;

    [ObservableProperty] private bool isPreviewVisible;
    [ObservableProperty] private PreviewKind previewKind;
    [ObservableProperty] private DriveItemModel? previewItem;
    [ObservableProperty] private string previewText = string.Empty;
    [ObservableProperty] private Bitmap? previewImage;
    [ObservableProperty] private double previewZoom = 1.0;
    [ObservableProperty] private double previewImageWidth;
    [ObservableProperty] private double previewImageHeight;
    [ObservableProperty] private string previewMediaUrl = string.Empty;
    [ObservableProperty] private string previewCachedFilePath = string.Empty;
    [ObservableProperty] private string previewStatus = string.Empty;
    [ObservableProperty] private bool isPreviewLoading;
    [ObservableProperty] private bool isPreviewDetailsVisible;
    [ObservableProperty] private bool isSlideshowPlaying;
    [ObservableProperty] private bool isCloseConfirmVisible;

    public bool IsNotAuthenticated => !IsAuthenticated;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasSelection => SelectionCount > 0;
    public bool HasSingleSelection => SelectionCount == 1;
    public bool HasFileSelection => _selectedItems.Any(x => x.IsFile);
    public bool HasDownloadableSelection => _selectedItems.Count > 0;
    public bool HasUserAvatar => UserAvatar is not null;
    public bool HasQuota => QuotaTotalBytes > 0;
    public double QuotaUsedPercent => QuotaTotalBytes > 0
        ? Math.Clamp(QuotaUsedBytes * 100d / QuotaTotalBytes, 0d, 100d)
        : 0d;
    public string QuotaPercentText => HasQuota ? $"{QuotaUsedPercent:0.#}%" : "--";
    public string AppVersionText
    {
        get
        {
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return version is null ? "Hello1Drive" : $"Version {version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }
    public bool IsStartWithWindowsSupported => _startupRegistrationService?.IsSupported == true;
    public string UserInitial => string.IsNullOrWhiteSpace(UserDisplayName) ? "M" : UserDisplayName.Trim()[0].ToString().ToUpperInvariant();

    public bool IsDetailsView => ViewMode == FileViewMode.Details;
    public bool IsLargeIconView => ViewMode == FileViewMode.LargeIcons;
    public bool IsExtraLargeIconView => ViewMode == FileViewMode.ExtraLargeIcons;

    public string NameSortIndicator => SortIndicator(FileSortColumn.Name);
    public string SizeSortIndicator => SortIndicator(FileSortColumn.Size);
    public string ModifiedSortIndicator => SortIndicator(FileSortColumn.Modified);

    public bool IsBackgroundColorMode => CurrentBackgroundMode == WindowBackgroundMode.Color;
    public bool IsBackgroundUrlMode => CurrentBackgroundMode == WindowBackgroundMode.Url;
    public bool IsBackgroundLocalImageMode => CurrentBackgroundMode == WindowBackgroundMode.LocalImage;
    public bool IsBackgroundLocalFolderMode => CurrentBackgroundMode == WindowBackgroundMode.LocalFolder;
    public bool IsBackgroundOneDriveFolderMode => CurrentBackgroundMode == WindowBackgroundMode.OneDriveFolder;
    public bool IsBackgroundFolderMode => IsBackgroundLocalFolderMode || IsBackgroundOneDriveFolderMode;

    public bool IsTextPreview => PreviewKind == PreviewKind.Text;
    public bool IsImagePreview => PreviewKind == PreviewKind.Image;
    public bool IsMediaPreview => PreviewKind == PreviewKind.Media;
    public bool IsGenericPreview => PreviewKind == PreviewKind.Generic;
    public bool ShowGenericPreview => IsGenericPreview && !IsPreviewLoading;
    public string PreviewDetails => PreviewItem is null
        ? string.Empty
        : $"{PreviewItem.TypeDisplay}  ·  {PreviewItem.SizeDisplay}  ·  {PreviewItem.ModifiedDisplay}";
    public string PreviewExtendedDetails => PreviewItem is null
        ? string.Empty
        : $"名称：{PreviewItem.Name}\n类型：{PreviewItem.TypeDisplay}\n大小：{PreviewItem.SizeDisplay}\n修改时间：{PreviewItem.ModifiedDisplay}\n创建时间：{PreviewItem.CreatedDateTime?.ToLocalTime():yyyy-MM-dd HH:mm:ss}\nMIME：{PreviewItem.MimeType}\nID：{PreviewItem.Id}";
    public string PreviewZoomText => $"{PreviewZoom:P0}";
    public string SlideshowText => IsSlideshowPlaying ? "停止幻灯片" : "幻灯片播放";
    public string AcrylicBlurText => $"{AcrylicBlurPercent:0}%";
    public IBrush BackgroundColorPreviewBrush => new SolidColorBrush(BackgroundPickerColor);

    public int ActiveTransferCount => Transfers.Count(x => x.State is TransferState.Waiting or TransferState.Running);
    public bool HasActiveTransfers => ActiveTransferCount > 0;
    public int UploadTransferCount => Transfers.Count(x => x.Direction == TransferDirection.Upload);
    public int DownloadTransferCount => Transfers.Count(x => x.Direction == TransferDirection.Download);
    public int CacheTransferCount => Transfers.Count(x => x.Direction == TransferDirection.Cache);
    public string TransferSummaryText => $"上传 {UploadTransferCount} · 下载 {DownloadTransferCount} · 缓存 {CacheTransferCount}" +
                                         (ActiveTransferCount > 0 ? $" · {ActiveTransferCount} 个进行中" : string.Empty);
    public string ItemCountText => $"{(_currentFolderTotalItemCount ?? _allItems.Count)} 项";

    private void SetCurrentFolderTotalItemCount(int? total)
    {
        var normalized = total is >= 0 ? total : null;
        if (_currentFolderTotalItemCount == normalized)
            return;

        _currentFolderTotalItemCount = normalized;
        OnPropertyChanged(nameof(ItemCountText));
    }

    public bool IsMobileListScrolling => _mobileListScrolling;
    public bool IsDesktopListScrolling => _desktopListScrolling;

    public void SetMobileListScrolling(bool value)
    {
        if (!IsMobilePlatform || _mobileListScrolling == value)
            return;

        _mobileListScrolling = value;
        if (value)
        {
            // The moment a new fling begins, abort thumbnail work for the old resting viewport.
            // Otherwise two slow cloud thumbnail downloads can occupy the shared mobile gate while
            // the user has already jumped hundreds of rows away. Idle recovery will immediately
            // restart only the thumbnails that belong to the final resting viewport.
            CancelThumbnailLoading();
        }
    }

    public void SetDesktopListScrolling(bool value)
    {
        if (IsMobilePlatform || _desktopListScrolling == value)
            return;

        _desktopListScrolling = value;
        if (value)
        {
            // Desktop used to enqueue thumbnails for every loaded item/page. Dragging the scroll
            // thumb hundreds of rows then had to wait behind that obsolete queue. A new scrollbar
            // gesture cancels those workers; the final realized viewport will restart only what is
            // actually visible.
            CancelThumbnailLoading();
        }
    }

    public int GetMobileItemIndex(DriveItemModel item)
    {
        if (!IsMobilePlatform || item is null)
            return -1;

        for (var i = 0; i < MobileItems.Count; i++)
        {
            var candidate = MobileItems[i].Item;
            if (candidate is not null &&
                (ReferenceEquals(candidate, item) || string.Equals(candidate.Id, item.Id, StringComparison.Ordinal)))
                return i;
        }
        return -1;
    }

    public DriveItemModel? GetMobileItemAtIndex(int index) =>
        index >= 0 && index < MobileItems.Count ? MobileItems[index].Item : null;

    public bool MobileSelectionModeActive { get; set; }
    public string CloseConfirmationMessage => ActiveTransferCount > 0
        ? $"当前还有 {ActiveTransferCount} 个传输任务正在等待或进行中。关闭后任务列表会保存，可恢复的任务会在下次打开 Hello1Drive 时自动继续。确定关闭吗？"
        : "确定关闭软件吗？";

    public string? CurrentFolderId => Breadcrumbs.LastOrDefault()?.ItemId;
    public string CurrentAccountId { get; private set; } = string.Empty;
    public IReadOnlyList<DriveItemModel> SelectedItemsSnapshot => _selectedItems.ToArray();
    public IReadOnlyList<DriveItemModel> LoadedItems => _allItems;

    public DriveItemModel[] GetVisibleLoadedItemsSnapshot()
    {
        var keyword = SearchText.Trim();
        return string.IsNullOrWhiteSpace(keyword)
            ? _allItems.ToArray()
            : _allItems.Where(item => item.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    public AppSettings Settings => _settingsService.Current;

    private WindowBackgroundMode CurrentBackgroundMode => SelectedBackgroundModeText switch
    {
        "纯色" => WindowBackgroundMode.Color,
        "本地图片" => WindowBackgroundMode.LocalImage,
        "图片 URL" => WindowBackgroundMode.Url,
        "本地文件夹" => WindowBackgroundMode.LocalFolder,
        "OneDrive 文件夹" => WindowBackgroundMode.OneDriveFolder,
        _ => WindowBackgroundMode.Default
    };

    public MainViewModel(
        IOneDriveService oneDrive,
        IAuthenticationService authentication,
        AppSettingsService settingsService,
        FileCacheService fileCache,
        ThumbnailCacheService thumbnailCache,
        TransferPersistenceService transferPersistence,
        StartupSnapshotService startupSnapshot,
        LocalDriveIndexService localDriveIndex,
        IStartupRegistrationService? startupRegistrationService = null,
        ITransferBackgroundService? transferBackgroundService = null)
    {
        _oneDrive = oneDrive;
        _authentication = authentication;
        _settingsService = settingsService;
        _fileCache = fileCache;
        _thumbnailCache = thumbnailCache;
        _transferPersistence = transferPersistence;
        _startupSnapshot = startupSnapshot;
        _localDriveIndex = localDriveIndex;
        _startupRegistrationService = startupRegistrationService;
        _transferBackgroundService = transferBackgroundService;
        _gifTimer.Tick += GifTimer_Tick;
        _slideshowTimer.Tick += SlideshowTimer_Tick;
        _driveIndexRefreshTimer.Tick += DriveIndexRefreshTimer_Tick;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ItemCountText));
        MobileItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ItemCountText));
        Breadcrumbs.Add(new BreadcrumbItem("OneDrive", null));
        LoadSettingsIntoProperties();
        ApplyTransferRateLimits();
        UpdateCacheStatus();
        RestorePersistedTransfers();
        TryRestoreStartupSnapshot();
    }

    partial void OnIsAuthenticatedChanged(bool value) => OnPropertyChanged(nameof(IsNotAuthenticated));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnUserDisplayNameChanged(string value) => OnPropertyChanged(nameof(UserInitial));
    partial void OnUserAvatarChanged(Bitmap? value) => OnPropertyChanged(nameof(HasUserAvatar));

    partial void OnQuotaUsedBytesChanged(long value)
    {
        OnPropertyChanged(nameof(QuotaUsedPercent));
        OnPropertyChanged(nameof(QuotaPercentText));
        OnPropertyChanged(nameof(HasQuota));
    }

    partial void OnQuotaTotalBytesChanged(long value)
    {
        OnPropertyChanged(nameof(QuotaUsedPercent));
        OnPropertyChanged(nameof(QuotaPercentText));
        OnPropertyChanged(nameof(HasQuota));
    }
    partial void OnHasMoreItemsChanged(bool value) => OnPropertyChanged(nameof(ItemCountText));

    partial void OnViewModeChanged(FileViewMode value)
    {
        OnPropertyChanged(nameof(IsDetailsView));
        OnPropertyChanged(nameof(IsLargeIconView));
        OnPropertyChanged(nameof(IsExtraLargeIconView));
    }

    partial void OnSelectedThemeTextChanged(string value)
    {
        Settings.ThemeMode = value switch
        {
            "浅色" => AppThemeMode.Light,
            "深色" => AppThemeMode.Dark,
            _ => AppThemeMode.System
        };
        _ = _settingsService.SaveAsync();
    }

    partial void OnSortColumnChanged(FileSortColumn value) => RaiseSortIndicators();
    partial void OnSortStateChanged(SortCycleState value) => RaiseSortIndicators();

    partial void OnSelectedBackgroundModeTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsBackgroundColorMode));
        OnPropertyChanged(nameof(IsBackgroundUrlMode));
        OnPropertyChanged(nameof(IsBackgroundLocalImageMode));
        OnPropertyChanged(nameof(IsBackgroundLocalFolderMode));
        OnPropertyChanged(nameof(IsBackgroundOneDriveFolderMode));
        OnPropertyChanged(nameof(IsBackgroundFolderMode));
        Settings.BackgroundMode = CurrentBackgroundMode;
        _ = _settingsService.SaveAsync();
    }

    partial void OnBackgroundColorTextChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            Settings.BackgroundColor = value.Trim();

        if (!_syncingBackgroundColor && TryParseOpaqueColor(value, out var color) && !BackgroundPickerColor.Equals(color))
        {
            _syncingBackgroundColor = true;
            BackgroundPickerColor = color;
            _syncingBackgroundColor = false;
        }

        _ = _settingsService.SaveAsync();
    }

    partial void OnBackgroundPickerColorChanged(Color value)
    {
        OnPropertyChanged(nameof(BackgroundColorPreviewBrush));
        if (_syncingBackgroundColor)
            return;

        var normalized = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
        if (string.Equals(BackgroundColorText, normalized, StringComparison.OrdinalIgnoreCase))
            return;

        _syncingBackgroundColor = true;
        BackgroundColorText = normalized;
        _syncingBackgroundColor = false;

        Settings.BackgroundColor = normalized;
        _ = _settingsService.SaveAsync();
    }

    private static bool TryParseOpaqueColor(string? text, out Color color)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                color = default;
                return false;
            }

            var parsed = Color.Parse(text.Trim());
            color = Color.FromArgb(255, parsed.R, parsed.G, parsed.B);
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }

    partial void OnBackgroundUrlChanged(string value)
    {
        Settings.BackgroundUrl = value.Trim();
        _ = _settingsService.SaveAsync();
    }

    partial void OnBackgroundIntervalMinutesChanged(double value)
    {
        var normalized = double.IsFinite(value) ? Math.Max(0.1, value) : 5;
        if (!double.IsFinite(value) || Math.Abs(value - normalized) > 0.001)
        {
            BackgroundIntervalMinutes = normalized;
            return;
        }
        Settings.BackgroundIntervalMinutes = normalized;
        _ = _settingsService.SaveAsync();
    }

    partial void OnPreviewKindChanged(PreviewKind value)
    {
        OnPropertyChanged(nameof(IsTextPreview));
        OnPropertyChanged(nameof(IsImagePreview));
        OnPropertyChanged(nameof(IsMediaPreview));
        OnPropertyChanged(nameof(IsGenericPreview));
        OnPropertyChanged(nameof(ShowGenericPreview));
    }

    partial void OnPreviewItemChanged(DriveItemModel? value)
    {
        OnPropertyChanged(nameof(PreviewDetails));
        OnPropertyChanged(nameof(PreviewExtendedDetails));
    }
    partial void OnPreviewZoomChanged(double value) => OnPropertyChanged(nameof(PreviewZoomText));
    partial void OnIsPreviewLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowGenericPreview));
    partial void OnIsSlideshowPlayingChanged(bool value) => OnPropertyChanged(nameof(SlideshowText));

    partial void OnRememberLastFolderChanged(bool value)
    {
        Settings.RememberLastFolder = value;
        if (!value)
        {
            Settings.LastFolderBreadcrumbs.Clear();
            _startupSnapshot.Clear();
            if (CurrentFolderId is null)
                _ = SaveStartupSnapshotAsync();
        }
        else
        {
            CaptureCurrentFolderMemory();
            _ = SaveStartupSnapshotAsync();
        }
        _ = _settingsService.SaveAsync();
    }

    partial void OnShowFloatingUploadButtonChanged(bool value)
    {
        Settings.ShowFloatingUploadButton = value;
        _ = _settingsService.SaveAsync();
    }

    partial void OnShowToolbarChanged(bool value)
    {
        Settings.ShowToolbar = value;
        _ = _settingsService.SaveAsync();
    }

    partial void OnTransparentFileItemBackgroundChanged(bool value)
    {
        Settings.TransparentFileItemBackground = value;
        _ = _settingsService.SaveAsync();
    }

    partial void OnConfirmBeforeDeleteChanged(bool value)
    {
        Settings.ConfirmBeforeDelete = value;
        _ = _settingsService.SaveAsync();
    }

    partial void OnUseBuiltInViewerChanged(bool value)
    {
        Settings.UseBuiltInViewer = value;
        _ = _settingsService.SaveAsync();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_syncingStartWithWindowsSetting || _startupRegistrationService?.IsSupported != true)
            return;

        try
        {
            _startupRegistrationService.SetEnabled(value);
            Settings.StartWithWindows = value;
            StatusText = value ? "已启用开机启动 · 登录 Windows 后在托盘运行" : "已关闭开机启动";
            _ = _settingsService.SaveAsync();
        }
        catch (Exception ex)
        {
            var actual = _startupRegistrationService.IsEnabled;
            _syncingStartWithWindowsSetting = true;
            StartWithWindows = actual;
            _syncingStartWithWindowsSetting = false;
            Settings.StartWithWindows = actual;
            ErrorMessage = $"设置开机启动失败：{ex.Message}";
        }
    }

    partial void OnSelectedDefaultSortTextChanged(string value)
    {
        if (_syncingDefaultSortSetting)
            return;

        _ = ApplyGlobalDefaultSortAsync(value);
    }

    partial void OnAcrylicBlurPercentChanged(double value)
    {
        var normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 50;
        if (!double.IsFinite(value) || Math.Abs(value - normalized) > 0.001)
        {
            AcrylicBlurPercent = normalized;
            return;
        }
        Settings.AcrylicBlurPercent = normalized;
        OnPropertyChanged(nameof(AcrylicBlurText));
        _ = _settingsService.SaveAsync();
    }

    partial void OnSlideshowIntervalSecondsChanged(double value)
    {
        Settings.SlideshowIntervalSeconds = Math.Clamp(double.IsFinite(value) ? value : 5, 1, 3600);
        if (IsSlideshowPlaying)
            _slideshowTimer.Interval = TimeSpan.FromSeconds(Settings.SlideshowIntervalSeconds);
        _ = _settingsService.SaveAsync();
    }

    partial void OnLimitDownloadSpeedChanged(bool value) { Settings.LimitDownloadSpeed = value; ApplyTransferRateLimits(); _ = _settingsService.SaveAsync(); }
    partial void OnDownloadSpeedLimitKBpsChanged(double value) { Settings.DownloadSpeedLimitKBps = Math.Max(1, value); ApplyTransferRateLimits(); _ = _settingsService.SaveAsync(); }
    partial void OnLimitUploadSpeedChanged(bool value) { Settings.LimitUploadSpeed = value; ApplyTransferRateLimits(); _ = _settingsService.SaveAsync(); }
    partial void OnUploadSpeedLimitKBpsChanged(double value) { Settings.UploadSpeedLimitKBps = Math.Max(1, value); ApplyTransferRateLimits(); _ = _settingsService.SaveAsync(); }
    partial void OnSearchTextChanged(string value) => ApplyFilterAndSort();

    private void ClearSearchForFolderChange()
    {
        if (!string.IsNullOrEmpty(SearchText))
            SearchText = string.Empty;
    }

    private async Task<string?> GetSilentAccessTokenWithRetryAsync()
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await _authentication.GetAccessTokenAsync(interactive: false).ConfigureAwait(true);
            }
            catch (Exception ex) when (IsTransientNetworkFailure(ex))
            {
                ErrorMessage = null;
                var delay = TimeSpan.FromSeconds(Math.Min(8.0, 0.5 * Math.Pow(2, Math.Min(attempt++, 4))));
                await Task.Delay(delay).ConfigureAwait(true);
            }
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;

        // If the previous folder snapshot was restored in the constructor, the user can already
        // see the last directory. Do not cover that useful cached UI with the global busy overlay
        // while MSAL/Graph are warming up in the background.
        if (_startupSnapshotRestored)
        {
            ErrorMessage = null;
            StatusText = "已显示本地缓存 · 正在同步 OneDrive";
            try
            {
                var token = await GetSilentAccessTokenWithRetryAsync();
                if (string.IsNullOrWhiteSpace(token))
                {
                    ClearRestoredStartupState();
                    StatusText = "请登录 Microsoft 账户";
                    return;
                }

                await LoadSignedInStateAsync(preferRestoredSnapshot: true);
            }
            catch (Exception ex)
            {
                // A valid local snapshot remains the UI while the read-side transport retries.
                // Never surface raw TLS/DNS/socket wording to end users.
                if (IsTransientNetworkFailure(ex))
                {
                    ErrorMessage = null;
                    StatusText = "已显示本地缓存";
                }
                else
                {
                    ErrorMessage = $"OneDrive 同步失败：{ex.Message}";
                    StatusText = "已显示本地缓存 · 暂时无法同步";
                }
            }

            return;
        }

        await RunBusyAsync(async () =>
        {
            var token = await GetSilentAccessTokenWithRetryAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                IsAuthenticated = false;
                StatusText = "请登录 Microsoft 账户";
                return;
            }

            await LoadSignedInStateAsync();
        });
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        await RunBusyAsync(async () =>
        {
            var token = await _authentication.GetAccessTokenAsync(interactive: true);
            if (string.IsNullOrWhiteSpace(token))
                return;
            await LoadSignedInStateAsync();
        });
    }

    [RelayCommand]
    private void RequestLogout() => IsLogoutConfirmVisible = true;

    [RelayCommand]
    private void CancelLogout() => IsLogoutConfirmVisible = false;

    [RelayCommand]
    private async Task ConfirmLogoutAsync()
    {
        IsLogoutConfirmVisible = false;
        await RunBusyAsync(LogoutCoreAsync);
    }

    private async Task LogoutCoreAsync()
    {
        await _authentication.SignOutAsync();
        IsAuthenticated = false;
        UserDisplayName = string.Empty;
        UserEmail = string.Empty;
        CurrentAccountId = string.Empty;
        UserAvatar?.Dispose();
        UserAvatar = null;
        QuotaText = string.Empty;
        QuotaUsedBytes = 0;
        QuotaTotalBytes = 0;
        CancelThumbnailLoading();
        ResetMobileThumbnailWindow();
        ResetDesktopThumbnailViewport();
        Interlocked.Exchange(ref _folderMetadataSyncCts, null)?.Cancel();
        Interlocked.Exchange(ref _driveIndexSyncCts, null)?.Cancel();
        _driveIndexRefreshTimer.Stop();
        Items.Clear();
        ClearMobileSlots();
        ClearFolderCache();
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbItem("OneDrive", null));
        SetCurrentFolderTotalItemCount(null);
        CurrentLocation = "OneDrive";
        SetSelectedItems([]);
        ClosePreview();
        _startupSnapshot.Clear();
        _startupSnapshotRestored = false;
        _startupSnapshotAccountId = string.Empty;
        StatusText = "已退出登录";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!IsAuthenticated)
            return;
        var navigation = BeginFolderNavigation(FolderNavigationReason.Refresh);
        await RunFolderNavigationAsync(
            token => LoadCurrentFolderAsync(forceRemote: true, token),
            navigation,
            showBusy: true);
    }

    public async Task RefreshCurrentFolderAsync()
    {
        var navigation = BeginFolderNavigation(FolderNavigationReason.Refresh);
        await RunFolderNavigationAsync(
            token => LoadCurrentFolderAsync(forceRemote: true, token),
            navigation,
            showBusy: false);
    }

    public void InvalidateFolderCacheForId(string? folderId) => InvalidateFolderCache(folderId);

    [RelayCommand]
    private async Task GoRootAsync()
    {
        if (!IsAuthenticated)
            return;
        var navigation = BeginFolderNavigation(FolderNavigationReason.Root);
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbItem("OneDrive", null));
        SetCurrentFolderTotalItemCount(null);
        await RunFolderNavigationAsync(
            token => LoadCurrentFolderAsync(cancellationToken: token),
            navigation,
            showBusy: !HasFolderCache(null));
    }

    // Back must remain invokable while a previous folder load is still pending. The new
    // navigation cancels that HTTP request and immediately restores the parent folder.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task GoBackAsync()
    {
        if (Breadcrumbs.Count <= 1)
            return;
        ClearSearchForFolderChange();
        var navigation = BeginFolderNavigation(FolderNavigationReason.Back);
        Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
        SetCurrentFolderTotalItemCount(null);
        await RunFolderNavigationAsync(
            token => LoadCurrentFolderAsync(cancellationToken: token),
            navigation,
            showBusy: !HasFolderCache(CurrentFolderId));
    }

    public async Task<DriveItemOpenResult> OpenItemAsync(DriveItemModel item)
    {
        // Personal Vault is not a normal Graph folder. Hand it to Microsoft's own OneDrive
        // experience so the additional verification flow can run.
        if (item.IsPersonalVault)
        {
            ErrorMessage = null;
            StatusText = "个人保险库需要通过 OneDrive 官方界面验证后访问";
            return DriveItemOpenResult.RequiresOfficialOneDriveHandoff;
        }

        if (item.IsFolder)
        {
            ClearSearchForFolderChange();
            var navigation = BeginFolderNavigation(FolderNavigationReason.EnterChild);
            Breadcrumbs.Add(new BreadcrumbItem(item.Name, item.Id));
            SetCurrentFolderTotalItemCount(item.ChildCount);
            var navigationError = await RunFolderNavigationAsync(
                token => LoadCurrentFolderAsync(cancellationToken: token),
                navigation,
                showBusy: !HasFolderCache(item.Id),
                suppressChildrenOnNonFolderError: true);

            if (navigationError is GraphChildrenOnNonFolderException)
            {
                // A few OneDrive Personal Vault payloads still arrive without a reliable
                // specialFolder facet. Roll the speculative breadcrumb back and let the UI
                // open the item's OneDrive web URL instead of showing the raw Graph 422.
                if (Breadcrumbs.Count > 1 &&
                    string.Equals(Breadcrumbs[^1].ItemId, item.Id, StringComparison.Ordinal))
                {
                    Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
                }

                var parentCacheKey = FolderCacheKey(CurrentFolderId);
                if (_folderCache.TryGetValue(parentCacheKey, out var parentCache))
                    SetCurrentFolderTotalItemCount(parentCache.TotalItemCount);
                else
                    SetCurrentFolderTotalItemCount(_allItems.Count);

                ErrorMessage = null;
                StatusText = "该项目需要通过 OneDrive 官方界面打开";
                return DriveItemOpenResult.RequiresOfficialOneDriveHandoff;
            }

            return DriveItemOpenResult.Handled;
        }

        // Preview has its own cancellable loading state. Do not cover it with the global
        // busy overlay, otherwise Close/Back cannot cancel a large download.
        await LoadPreviewAsync(item);
        return DriveItemOpenResult.Handled;
    }

    public async Task NavigateToBreadcrumbAsync(BreadcrumbItem item)
    {
        var index = Breadcrumbs.IndexOf(item);
        if (index < 0)
            return;
        if (index == Breadcrumbs.Count - 1)
            return;
        ClearSearchForFolderChange();
        var navigation = BeginFolderNavigation(FolderNavigationReason.Breadcrumb);
        while (Breadcrumbs.Count > index + 1)
            Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
        SetCurrentFolderTotalItemCount(null);
        await RunFolderNavigationAsync(
            token => LoadCurrentFolderAsync(cancellationToken: token),
            navigation,
            showBusy: !HasFolderCache(CurrentFolderId));
    }

    public void SetSelectedItems(IEnumerable<DriveItemModel> items)
    {
        _selectedItems.Clear();
        _selectedItems.AddRange(items.DistinctBy(x => x.Id));
        SelectedItem = _selectedItems.FirstOrDefault();
        SelectionCount = _selectedItems.Count;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSingleSelection));
        OnPropertyChanged(nameof(HasFileSelection));
        OnPropertyChanged(nameof(HasDownloadableSelection));
        StatusText = SelectionCount > 0 ? $"已选择 {SelectionCount} 个项目" : $"{_allItems.Count} 个项目";
    }

    [RelayCommand]
    private void BeginCreateFolder()
    {
        PromptTitle = "新建文件夹";
        PromptMessage = "输入文件夹名称";
        PromptText = "新建文件夹";
        IsPromptInputVisible = true;
        _promptUseBusy = true;
        _promptAction = async text =>
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            await _oneDrive.CreateFolderAsync(CurrentFolderId, text.Trim());
            InvalidateCurrentFolderCache();
            await LoadCurrentFolderAsync(forceRemote: true);
        };
        IsPromptVisible = true;
    }

    [RelayCommand]
    private void BeginRename()
    {
        if (_selectedItems.Count != 1)
            return;
        var selected = _selectedItems[0];
        PromptTitle = "重命名";
        PromptMessage = $"重命名“{selected.Name}”";
        PromptText = selected.Name;
        IsPromptInputVisible = true;
        _promptUseBusy = true;
        _promptAction = async text =>
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            await _oneDrive.RenameAsync(selected.Id, text.Trim());
            _fileCache.Invalidate(selected.Id);
            _thumbnailCache.Invalidate(selected.Id);
            InvalidateCurrentFolderCache();
            await LoadCurrentFolderAsync(forceRemote: true);
        };
        IsPromptVisible = true;
    }

    [RelayCommand]
    private async Task BeginDeleteAsync()
    {
        if (_selectedItems.Count == 0)
            return;

        var selected = _selectedItems.ToArray();
        async Task DeleteSelectedAsync()
        {
            foreach (var item in selected)
            {
                await _oneDrive.DeleteAsync(item.Id);
                _fileCache.Invalidate(item.Id);
                _thumbnailCache.Invalidate(item.Id);
            }
            InvalidateCurrentFolderCache();
            await LoadCurrentFolderAsync(forceRemote: true);
        }

        if (!ConfirmBeforeDelete)
        {
            await RunBusyAsync(DeleteSelectedAsync);
            return;
        }

        PromptTitle = selected.Length == 1 ? "删除项目" : $"删除 {selected.Length} 个项目";
        PromptMessage = selected.Length == 1
            ? $"确定删除“{selected[0].Name}”吗？此操作会将项目移入 OneDrive 回收站。"
            : $"确定删除已选择的 {selected.Length} 个项目吗？这些项目会移入 OneDrive 回收站。";
        PromptText = string.Empty;
        IsPromptInputVisible = false;
        _promptUseBusy = true;
        _promptAction = async _ => await DeleteSelectedAsync();
        IsPromptVisible = true;
    }

    public void ShowConfirmation(string title, string message, Func<Task> action, bool useBusy = true)
    {
        PromptTitle = title;
        PromptMessage = message;
        PromptText = string.Empty;
        IsPromptInputVisible = false;
        _promptUseBusy = useBusy;
        _promptAction = async _ => await action();
        IsPromptVisible = true;
    }

    [RelayCommand]
    private async Task ConfirmPromptAsync()
    {
        var action = _promptAction;
        var useBusy = _promptUseBusy;
        IsPromptVisible = false;
        _promptAction = null;
        _promptUseBusy = true;
        if (action is null)
            return;

        if (useBusy)
        {
            await RunBusyAsync(() => action(PromptText));
            return;
        }

        try
        {
            ErrorMessage = null;
            await action(PromptText);
        }
        catch (OperationCanceledException)
        {
            StatusText = "操作已取消";
        }
        catch (Exception ex)
        {
            if (IsTransientNetworkFailure(ex))
            {
                ErrorMessage = null;
            }
            else
            {
                ErrorMessage = ex.Message;
                StatusText = "操作失败";
            }
        }
    }

    [RelayCommand]
    private void CancelPrompt()
    {
        IsPromptVisible = false;
        _promptAction = null;
        _promptUseBusy = true;
    }

    [RelayCommand]
    private void CloseError() => ErrorMessage = null;

    [RelayCommand]
    private void ShowSettings() => IsSettingsPanelVisible = true;

    [RelayCommand]
    private void HideSettings() => IsSettingsPanelVisible = false;

    public void RequestCloseConfirmation()
    {
        OnPropertyChanged(nameof(CloseConfirmationMessage));
        IsCloseConfirmVisible = true;
    }
    public void CancelCloseConfirmation() => IsCloseConfirmVisible = false;

    [RelayCommand]
    private void ToggleTransfers() => IsTransferPanelVisible = !IsTransferPanelVisible;

    [RelayCommand]
    private void ClearFinishedTransfers()
    {
        foreach (var item in Transfers.Where(x => x.State is TransferState.Completed or TransferState.Cancelled or TransferState.Failed).ToArray())
            Transfers.Remove(item);
        RaiseTransferSummary();
        ScheduleTransferPersistence();
    }

    private string GetTransferDisplayName(string fileName)
    {
        var normalized = (fileName ?? string.Empty).Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2)
            return $"{segments[^2]}/{segments[^1]}";

        var parentName = Breadcrumbs.LastOrDefault()?.Name?.Trim();
        return string.IsNullOrWhiteSpace(parentName)
            ? segments[0]
            : $"{parentName}/{segments[0]}";
    }

    public TransferItemModel RegisterTransfer(string fileName, TransferDirection direction)
    {
        var transfer = new TransferItemModel
        {
            FileName = GetTransferDisplayName(fileName),
            Direction = direction,
            State = TransferState.Waiting,
            Message = direction switch
            {
                TransferDirection.Upload => "等待上传",
                TransferDirection.Download => "等待下载",
                TransferDirection.Cache => "等待缓存",
                _ => "等待中"
            }
        };
        AttachTransfer(transfer);
        Transfers.Insert(0, transfer);
        RaiseTransferSummary();
        ScheduleTransferPersistence();
        return transfer;
    }

    public void SetTransferResumeInfo(TransferItemModel transfer, TransferResumeInfo resumeInfo)
    {
        transfer.ResumeInfo = resumeInfo;
        ScheduleTransferPersistence();
    }

    public IReadOnlyList<TransferItemModel> GetRestoredPendingTransfers() =>
        Transfers.Where(x => x.IsRestoredFromDisk && x.State == TransferState.Waiting)
                 .OrderBy(x => x.StartedAt)
                 .ToArray();

    public void MarkTransferResumePrepared(TransferItemModel transfer, Func<Task> retryAction)
    {
        transfer.IsRestoredFromDisk = false;
        transfer.RetryAction = retryAction;
        ScheduleTransferPersistence();
    }

    public void MarkTransferResumeUnavailable(TransferItemModel transfer, string message)
    {
        transfer.IsRestoredFromDisk = false;
        transfer.State = TransferState.Failed;
        transfer.Message = message;
        transfer.RetryAction = null;
        RaiseTransferSummary();
        ScheduleTransferPersistence();
    }

    public Task FlushTransferPersistenceAsync() => _transferPersistence.SaveAsync(Transfers);

    private void RestorePersistedTransfers()
    {
        foreach (var record in _transferPersistence.Load().OrderByDescending(x => x.StartedAt))
        {
            var wasPending = record.State is TransferState.Waiting or TransferState.Running;
            var transfer = new TransferItemModel
            {
                Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id,
                FileName = record.FileName ?? string.Empty,
                Direction = record.Direction,
                StartedAt = record.StartedAt == default ? DateTimeOffset.Now : record.StartedAt,
                Progress = wasPending ? 0 : Math.Clamp(record.Progress, 0, 1),
                State = wasPending ? TransferState.Waiting : record.State,
                Message = wasPending
                    ? record.Direction switch
                    {
                        TransferDirection.Upload => "等待恢复上传",
                        TransferDirection.Download => "等待恢复下载",
                        TransferDirection.Cache => "等待恢复缓存",
                        _ => "等待恢复"
                    }
                    : record.Message ?? string.Empty,
                ResumeInfo = record.ResumeInfo,
                IsRestoredFromDisk = wasPending
            };
            AttachTransfer(transfer);
            Transfers.Add(transfer);
        }

        RaiseTransferSummary();
    }

    private void AttachTransfer(TransferItemModel transfer)
    {
        transfer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TransferItemModel.State))
            {
                RaiseTransferSummary();
                // Persist state transitions, not every byte-progress tick. Progress is flushed
                // when the app closes, while state transitions keep resume metadata durable.
                ScheduleTransferPersistence();
            }
        };
    }

    private void ScheduleTransferPersistence()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _transferPersistenceCts, next);
        previous?.Cancel();
        _ = PersistTransfersAfterDelayAsync(next);
    }

    private async Task PersistTransfersAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(350, cts.Token);
            await _transferPersistence.SaveAsync(Transfers, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer transfer update superseded this pending save.
        }
        catch
        {
            // Persistence must never interrupt the transfer itself.
        }
        finally
        {
            if (ReferenceEquals(_transferPersistenceCts, cts))
                _transferPersistenceCts = null;
            cts.Dispose();
        }
    }

    public async Task UploadFileAsync(
        string? targetFolderId,
        string fileName,
        Stream stream,
        bool refreshWhenDone = true,
        TransferItemModel? existingTransfer = null)
    {
        var transfer = existingTransfer ?? RegisterTransfer(fileName, TransferDirection.Upload);
        try
        {
            transfer.Progress = 0;
            transfer.State = TransferState.Running;
            transfer.Message = "正在上传";
            StatusText = $"正在上传：{fileName}";
            var uploadPercent = -1;
            var progress = new ThrottledUiProgress(p =>
            {
                if (transfer.State != TransferState.Running)
                    return;

                transfer.Progress = p;
                var percent = (int)Math.Round(p * 100);
                if (percent != uploadPercent)
                {
                    uploadPercent = percent;
                    transfer.Message = $"正在上传 {percent}%";
                }
            });
            await _oneDrive.UploadFileAsync(targetFolderId, fileName, stream, progress);
            transfer.Progress = 1;
            transfer.State = TransferState.Completed;
            transfer.Message = "已上传";
            transfer.ResumeInfo = null;
            StatusText = $"上传完成：{fileName}";
            if (refreshWhenDone)
            {
                InvalidateFolderCache(targetFolderId);
                if (FolderCacheKey(CurrentFolderId) == FolderCacheKey(targetFolderId))
                    await LoadCurrentFolderAsync(forceRemote: true);
            }
        }
        catch (Exception ex)
        {
            transfer.State = TransferState.Failed;
            if (IsTransientNetworkFailure(ex))
            {
                transfer.Message = "网络暂时不可用，可重新尝试";
                ErrorMessage = null;
            }
            else
            {
                transfer.Message = ex.Message;
                ErrorMessage = ex.Message;
                StatusText = $"上传失败：{fileName}";
            }
        }
        finally
        {
            RaiseTransferSummary();
        }
    }

    public async Task DownloadFileAsync(
        DriveItemModel item,
        Stream destination,
        TransferItemModel? existingTransfer = null)
    {
        var transfer = existingTransfer ?? RegisterTransfer(item.Name, TransferDirection.Download);
        try
        {
            transfer.Progress = 0;
            transfer.State = TransferState.Running;
            transfer.Message = "正在下载";
            StatusText = $"正在下载：{item.Name}";
            var downloadPercent = -1;
            var progress = new ThrottledUiProgress(p =>
            {
                if (transfer.State != TransferState.Running)
                    return;

                transfer.Progress = p;
                var percent = (int)Math.Round(p * 100);
                if (percent != downloadPercent)
                {
                    downloadPercent = percent;
                    transfer.Message = $"正在下载 {percent}%";
                }
            });
            await _oneDrive.DownloadFileAsync(item.Id, destination, progress);
            transfer.Progress = 1;
            transfer.State = TransferState.Completed;
            transfer.Message = "已下载";
            transfer.ResumeInfo = null;
            StatusText = $"下载完成：{item.Name}";
        }
        catch (Exception ex)
        {
            transfer.State = TransferState.Failed;
            if (IsTransientNetworkFailure(ex))
            {
                transfer.Message = "网络暂时不可用，可重新尝试";
                ErrorMessage = null;
            }
            else
            {
                transfer.Message = ex.Message;
                ErrorMessage = ex.Message;
                StatusText = $"下载失败：{item.Name}";
            }
        }
        finally
        {
            RaiseTransferSummary();
        }
    }

    public async Task RetryTransferAsync(TransferItemModel transfer)
    {
        if (transfer.RetryAction is null || transfer.State != TransferState.Failed)
            return;

        transfer.Progress = 0;
        transfer.State = TransferState.Waiting;
        transfer.Message = transfer.Direction switch
        {
            TransferDirection.Upload => "等待重新上传",
            TransferDirection.Download => "等待重新下载",
            TransferDirection.Cache => "等待重新缓存",
            _ => "等待重试"
        };
        RaiseTransferSummary();
        try
        {
            await transfer.RetryAction();
        }
        catch (Exception ex)
        {
            transfer.State = TransferState.Failed;
            if (IsTransientNetworkFailure(ex))
            {
                transfer.Message = "网络暂时不可用，可重新尝试";
                ErrorMessage = null;
            }
            else
            {
                transfer.Message = ex.Message;
                ErrorMessage = ex.Message;
            }
            RaiseTransferSummary();
        }
    }

    private void RaiseTransferSummary()
    {
        OnPropertyChanged(nameof(ActiveTransferCount));
        OnPropertyChanged(nameof(HasActiveTransfers));
        OnPropertyChanged(nameof(UploadTransferCount));
        OnPropertyChanged(nameof(DownloadTransferCount));
        OnPropertyChanged(nameof(CacheTransferCount));
        OnPropertyChanged(nameof(TransferSummaryText));
        OnPropertyChanged(nameof(CloseConfirmationMessage));
        UpdateTransferBackgroundState();
    }

    private void UpdateTransferBackgroundState()
    {
        if (_transferBackgroundService is null)
            return;

        try
        {
            var active = Transfers
                .Where(x => x.State is TransferState.Waiting or TransferState.Running)
                .ToArray();
            _transferBackgroundService.Update(new TransferBackgroundState(
                ActiveCount: active.Length,
                RunningCount: active.Count(x => x.State == TransferState.Running),
                UploadCount: active.Count(x => x.Direction == TransferDirection.Upload),
                DownloadCount: active.Count(x => x.Direction == TransferDirection.Download),
                CacheCount: active.Count(x => x.Direction == TransferDirection.Cache)));
        }
        catch
        {
            // Platform keep-alive is best-effort. A notification/service failure must never
            // interrupt the actual OneDrive transfer or corrupt its persisted resume state.
        }
    }

    public async Task SetViewModeAsync(FileViewMode mode)
    {
        ViewMode = mode;

        // Keep the existing setting as the fallback for a folder that has never been visited,
        // and remember the explicit choice for this account + folder independently.
        Settings.ViewMode = mode;
        RememberCurrentFolderViewMode();
        await _settingsService.SaveAsync();
    }

    private string CurrentFolderViewMemoryKey()
    {
        var account = string.IsNullOrWhiteSpace(CurrentAccountId) ? "__ACCOUNT__" : CurrentAccountId;
        return $"{account}|{FolderCacheKey(CurrentFolderId)}";
    }

    private void RememberCurrentFolderViewMode()
    {
        var key = CurrentFolderViewMemoryKey();
        Settings.FolderViewModes.RemoveAll(x => string.Equals(x.FolderKey, key, StringComparison.Ordinal));
        Settings.FolderViewModes.Add(new RememberedFolderViewMode
        {
            FolderKey = key,
            ViewMode = ViewMode
        });
    }

    private void RestoreCurrentFolderViewMode()
    {
        var key = CurrentFolderViewMemoryKey();
        var remembered = Settings.FolderViewModes.LastOrDefault(
            x => string.Equals(x.FolderKey, key, StringComparison.Ordinal));
        ViewMode = remembered?.ViewMode ?? Settings.ViewMode;
    }

    public async Task CycleSortAsync(FileSortColumn column)
    {
        if (column == FileSortColumn.LegacyType)
            return;

        if (SortColumn != column || SortState == SortCycleState.Original)
        {
            SortColumn = column;
            SortState = SortCycleState.Ascending;
        }
        else if (SortState == SortCycleState.Ascending)
        {
            SortState = SortCycleState.Descending;
        }
        else
        {
            // "系统默认" at the folder level means the OneDrive/API original order.
            // It is persisted explicitly so a folder can opt out of a non-default
            // global sort rule.
            SortColumn = FileSortColumn.None;
            SortState = SortCycleState.Original;
        }

        await PersistCurrentFolderSortRuleAsync();
        var navigation = BeginFolderNavigation(FolderNavigationReason.Sort);
        await RunFolderNavigationAsync(
            token => LoadCurrentFolderAsync(forceRemote: true, token),
            navigation,
            showBusy: true);
    }

    public async Task SetSortAsync(FileSortColumn column, SortCycleState state)
    {
        if (column == FileSortColumn.LegacyType)
        {
            column = FileSortColumn.None;
            state = SortCycleState.Original;
        }

        SortColumn = state == SortCycleState.Original ? FileSortColumn.None : column;
        SortState = state;

        await PersistCurrentFolderSortRuleAsync();
        var navigation = BeginFolderNavigation(FolderNavigationReason.Sort);
        await RunFolderNavigationAsync(
            token => LoadCurrentFolderAsync(forceRemote: true, token),
            navigation,
            showBusy: true);
    }

    public async Task UseDefaultSortForCurrentFolderAsync()
    {
        var key = CurrentFolderSortMemoryKey();
        Settings.FolderSortRules.RemoveAll(x => string.Equals(x.FolderKey, key, StringComparison.Ordinal));
        ApplyGlobalDefaultSortToCurrentState();
        await _settingsService.SaveAsync();

        var navigation = BeginFolderNavigation(FolderNavigationReason.Sort);
        await RunFolderNavigationAsync(
            token => LoadCurrentFolderAsync(forceRemote: true, token),
            navigation,
            showBusy: true);
    }

    private string CurrentFolderSortMemoryKey()
    {
        var account = string.IsNullOrWhiteSpace(CurrentAccountId) ? "__ACCOUNT__" : CurrentAccountId;
        return $"{account}|{FolderCacheKey(CurrentFolderId)}";
    }

    private void ApplyGlobalDefaultSortToCurrentState()
    {
        var column = Settings.DefaultSortColumn;
        var state = Settings.DefaultSortState;

        if (column == FileSortColumn.LegacyType ||
            column == FileSortColumn.None ||
            state == SortCycleState.Original)
        {
            SortColumn = FileSortColumn.None;
            SortState = SortCycleState.Original;
            return;
        }

        SortColumn = column;
        SortState = state;
    }

    private void RestoreCurrentFolderSortRule()
    {
        var key = CurrentFolderSortMemoryKey();
        var rule = Settings.FolderSortRules.LastOrDefault(
            x => string.Equals(x.FolderKey, key, StringComparison.Ordinal));

        // No folder override: inherit the setting-wide default.
        if (rule is null)
        {
            ApplyGlobalDefaultSortToCurrentState();
            return;
        }

        // An explicit Original/None rule is meaningful: this folder wants the
        // server/API original order even if the global default is something else.
        if (rule.State == SortCycleState.Original ||
            rule.Column == FileSortColumn.None ||
            rule.Column == FileSortColumn.LegacyType)
        {
            SortColumn = FileSortColumn.None;
            SortState = SortCycleState.Original;
            return;
        }

        SortColumn = rule.Column;
        SortState = rule.State;
    }

    private async Task PersistCurrentFolderSortRuleAsync()
    {
        var key = CurrentFolderSortMemoryKey();
        Settings.FolderSortRules.RemoveAll(x => string.Equals(x.FolderKey, key, StringComparison.Ordinal));

        // Always persist the folder choice, including API-original order.
        // This is what lets one folder override a non-default global sort.
        Settings.FolderSortRules.Add(new RememberedFolderSortRule
        {
            FolderKey = key,
            Column = SortState == SortCycleState.Original ? FileSortColumn.None : SortColumn,
            State = SortState
        });

        await _settingsService.SaveAsync();
    }

    private static (FileSortColumn Column, SortCycleState State) ParseDefaultSortText(string? text) =>
        text switch
        {
            "日期 · 升序" => (FileSortColumn.Modified, SortCycleState.Ascending),
            "日期 · 降序" => (FileSortColumn.Modified, SortCycleState.Descending),
            "名称 · 升序" => (FileSortColumn.Name, SortCycleState.Ascending),
            "名称 · 降序" => (FileSortColumn.Name, SortCycleState.Descending),
            "大小 · 升序" => (FileSortColumn.Size, SortCycleState.Ascending),
            "大小 · 降序" => (FileSortColumn.Size, SortCycleState.Descending),
            _ => (FileSortColumn.None, SortCycleState.Original)
        };

    private static string FormatDefaultSortText(FileSortColumn column, SortCycleState state)
    {
        if (state == SortCycleState.Original || column == FileSortColumn.None)
            return "系统默认";

        return (column, state) switch
        {
            (FileSortColumn.Modified, SortCycleState.Ascending) => "日期 · 升序",
            (FileSortColumn.Modified, SortCycleState.Descending) => "日期 · 降序",
            (FileSortColumn.Name, SortCycleState.Ascending) => "名称 · 升序",
            (FileSortColumn.Name, SortCycleState.Descending) => "名称 · 降序",
            (FileSortColumn.Size, SortCycleState.Ascending) => "大小 · 升序",
            (FileSortColumn.Size, SortCycleState.Descending) => "大小 · 降序",
            _ => "系统默认"
        };
    }

    private async Task ApplyGlobalDefaultSortAsync(string text)
    {
        var (column, state) = ParseDefaultSortText(text);
        Settings.DefaultSortColumn = column;
        Settings.DefaultSortState = state;

        // The user explicitly asked that changing the setting overwrite all
        // folder-specific rules. New per-folder overrides can be created afterwards.
        Settings.FolderSortRules.Clear();
        ApplyGlobalDefaultSortToCurrentState();
        await _settingsService.SaveAsync();

        if (!IsAuthenticated)
            return;

        var navigation = BeginFolderNavigation(FolderNavigationReason.Sort);
        await RunFolderNavigationAsync(
            token => LoadCurrentFolderAsync(forceRemote: true, token),
            navigation,
            showBusy: true);
    }

    private string? GetGraphOrderBy()
    {
        if (SortState == SortCycleState.Original || SortColumn == FileSortColumn.None)
            return null;

        var field = SortColumn switch
        {
            FileSortColumn.Name => "name",
            FileSortColumn.Size => "size",
            FileSortColumn.Modified => "lastModifiedDateTime",
            _ => null
        };

        if (field is null)
            return null;

        var direction = SortState == SortCycleState.Descending ? "desc" : "asc";
        return $"{field} {direction}";
    }

    private bool UsesGraphOrdering => GetGraphOrderBy() is not null;

    private string SortIndicator(FileSortColumn column)
    {
        if (SortColumn != column || SortState == SortCycleState.Original)
            return string.Empty;
        return SortState == SortCycleState.Ascending ? "▲" : "▼";
    }

    private void RaiseSortIndicators()
    {
        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(SizeSortIndicator));
        OnPropertyChanged(nameof(ModifiedSortIndicator));
    }

    public async Task LoadPreviewAsync(DriveItemModel item, bool preserveSlideshow = false)
    {
        var keepMobileImageSurface = IsMobilePlatform && IsPreviewVisible && PreviewItem?.IsImage == true && item.IsImage;

        CancelPreviewLoad();
        ReleasePreviewImageBeforeNavigation();
        PreviewText = string.Empty;
        PreviewMediaUrl = string.Empty;
        PreviewCachedFilePath = string.Empty;
        PreviewStatus = string.Empty;
        if (!keepMobileImageSurface)
            PreviewKind = PreviewKind.Generic;
        PreviewItem = item;
        IsPreviewDetailsVisible = false;
        IsPreviewVisible = true;
        IsPreviewLoading = true;
        if (!preserveSlideshow)
            StopSlideshow();

        var cts = new CancellationTokenSource();
        _previewLoadCts = cts;
        var cancellationToken = cts.Token;

        try
        {
            if (!item.IsFile)
                return;

            if (item.IsText && item.Size > TextPreviewLimit)
            {
                PreviewStatus = $"文本文件超过 {DriveItemModel.FormatBytes(TextPreviewLimit)}，请下载后编辑。";
                return;
            }

            // Opened files use the persistent cache. The request is cancellable, so closing
            // the preview (or Android/iOS Back) immediately aborts metadata/download work.
            var cachedPath = await _fileCache.GetOrDownloadAsync(item, _oneDrive, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            PreviewCachedFilePath = cachedPath;
            UpdateCacheStatus();

            // While the current image is decoding, quietly cache two images before and
            // two images after it. Arrow-key / slideshow navigation can then open them
            // without another cloud download.
            if (item.IsImage)
                StartAdjacentImagePrefetch(item);

            if (item.IsText)
            {
                await using var stream = File.OpenRead(cachedPath);
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                PreviewText = await reader.ReadToEndAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                PreviewKind = PreviewKind.Text;
                PreviewStatus = "可直接编辑，修改后点击保存";
                return;
            }

            if (item.IsImage)
            {
                DisposeGifAnimation();
                if (string.Equals(Path.GetExtension(item.Name), ".gif", StringComparison.OrdinalIgnoreCase))
                {
                    _gifAnimation = await AnimatedGifService.LoadAsync(cachedPath, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_gifAnimation is { Frames.Count: > 1 })
                    {
                        _gifFrameIndex = 0;
                        PreviewImage = _gifAnimation.Frames[0];
                        _previewImagePixelWidth = _gifAnimation.PixelWidth;
                        _previewImagePixelHeight = _gifAnimation.PixelHeight;
                        if (!IsMobilePlatform)
                            PreviewZoom = 1.0;
                        UpdatePreviewImageSize();
                        PreviewKind = PreviewKind.Image;
                        PreviewStatus = string.Empty;
                        StartGifTimer();
                        return;
                    }
                }

                var decoded = await DecodePreviewBitmapAsync(cachedPath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (IsMobilePlatform)
                {
                    var oldGallery = item.GalleryImage;
                    item.GalleryImage = decoded;
                    if (oldGallery is not null && !ReferenceEquals(oldGallery, decoded))
                        oldGallery.Dispose();
                    PreviewImage = decoded;
                    TrimMobileGalleryImages(item);
                }
                else
                {
                    PreviewImage = decoded;
                }

                _previewImagePixelWidth = PreviewImage.PixelSize.Width;
                _previewImagePixelHeight = PreviewImage.PixelSize.Height;
                // On phones the carousel is already showing the fitted page. Do not publish a
                // transient 100% value before MainView can calculate the new viewport fit ratio.
                // Keeping the previous fitted ratio for this tiny hand-off avoids the visible flash.
                if (!IsMobilePlatform)
                    PreviewZoom = 1.0;
                UpdatePreviewImageSize();
                PreviewKind = PreviewKind.Image;
                PreviewStatus = string.Empty;
                return;
            }

            if (item.IsMedia)
            {
                // Playback uses the already-downloaded local cache. Avoid an extra Graph
                // request here so a cached video opens as soon as the local player is ready.
                PreviewMediaUrl = item.WebUrl ?? string.Empty;
                PreviewKind = PreviewKind.Media;
                PreviewStatus = string.Empty;
                return;
            }

            PreviewKind = PreviewKind.Generic;
            PreviewStatus = "暂不支持使用内置查看器，可使用系统应用打开。";
        }
        catch (OperationCanceledException)
        {
            // Expected when the user closes the overlay, presses Back, or switches files.
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                if (IsTransientNetworkFailure(ex))
                {
                    ErrorMessage = null;
                    PreviewStatus = string.Empty;
                }
                else
                {
                    PreviewStatus = "预览加载失败";
                    ErrorMessage = ex.Message;
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_previewLoadCts, cts))
            {
                _previewLoadCts = null;
                IsPreviewLoading = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Prepares a file for the OS default viewer. The preview surface is shown immediately so
    /// the existing Back/Close path can cancel a long cache download instead of blocking UI.
    /// The caller performs the platform launcher operation once this returns a local path.
    /// </summary>
    public async Task<string?> PrepareSystemOpenAsync(DriveItemModel item)
    {
        if (!item.IsFile)
            return null;

        CancelPreviewLoad();
        ReleasePreviewImageBeforeNavigation();
        PreviewText = string.Empty;
        PreviewMediaUrl = item.WebUrl ?? string.Empty;
        PreviewCachedFilePath = string.Empty;
        PreviewKind = PreviewKind.Generic;
        PreviewItem = item;
        PreviewStatus = "正在缓存文件…";
        IsPreviewDetailsVisible = false;
        IsPreviewVisible = true;
        IsPreviewLoading = true;
        StopSlideshow();

        var cts = new CancellationTokenSource();
        _previewLoadCts = cts;
        try
        {
            var path = await _fileCache.GetOrDownloadAsync(item, _oneDrive, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            PreviewCachedFilePath = path;
            PreviewStatus = "正在使用系统应用打开…";
            UpdateCacheStatus();
            return path;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (IsTransientNetworkFailure(ex))
            {
                ErrorMessage = null;
                PreviewStatus = string.Empty;
            }
            else
            {
                PreviewStatus = "文件缓存失败";
                ErrorMessage = ex.Message;
            }
            return null;
        }
        finally
        {
            if (ReferenceEquals(_previewLoadCts, cts))
            {
                _previewLoadCts = null;
                IsPreviewLoading = false;
            }
            cts.Dispose();
        }
    }

    public void MarkSystemOpenUnsupported(string? message = null)
    {
        PreviewKind = PreviewKind.Generic;
        IsPreviewLoading = false;
        IsPreviewVisible = true;
        PreviewStatus = message ?? "暂不支持。当前系统没有可用于打开此文件类型的默认应用。";
    }

    private static async Task<Bitmap> DecodePreviewBitmapAsync(
        string cachedPath,
        CancellationToken cancellationToken,
        int mobileMaxPreviewEdge = 4096)
    {
        if (!IsMobilePlatform)
        {
            await using var desktopStream = File.OpenRead(cachedPath);
            return new Bitmap(desktopStream);
        }

        // Phone cameras can easily produce 8K-12K images. The preview surface is far smaller,
        // and keeping the original full-resolution Skia bitmap makes both Back navigation and GC
        // noticeably stutter. Cap the longest decoded edge while preserving the aspect ratio.
        mobileMaxPreviewEdge = Math.Clamp(mobileMaxPreviewEdge, 512, 4096);
        try
        {
            var info = await Task.Run(() => SixLabors.ImageSharp.Image.Identify(cachedPath), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (info is not null && Math.Max(info.Width, info.Height) > mobileMaxPreviewEdge)
            {
                var scale = mobileMaxPreviewEdge / (double)Math.Max(info.Width, info.Height);
                var targetWidth = Math.Max(1, (int)Math.Round(info.Width * scale));
                await using var scaledStream = File.OpenRead(cachedPath);
                return Bitmap.DecodeToWidth(scaledStream, targetWidth, BitmapInterpolationMode.HighQuality);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // If metadata identification fails, Avalonia still gets a chance to decode normally.
        }

        await using var stream = File.OpenRead(cachedPath);
        return new Bitmap(stream);
    }

    private void StartAdjacentImagePrefetch(DriveItemModel current)
    {
        var images = GetVisibleLoadedItemsSnapshot().Where(static x => x.IsImage).ToArray();
        var index = Array.FindIndex(images, x => x.Id == current.Id);
        if (index < 0)
            return;

        var neighbours = new List<DriveItemModel>(4);
        for (var distance = 1; distance <= 2; distance++)
        {
            var previous = index - distance;
            var next = index + distance;
            if (previous >= 0)
                neighbours.Add(images[previous]);
            if (next < images.Length)
                neighbours.Add(images[next]);
        }

        if (neighbours.Count == 0)
            return;

        if (_previewPrefetchCts is null || _previewPrefetchCts.IsCancellationRequested)
        {
            _previewPrefetchCts?.Dispose();
            _previewPrefetchCts = new CancellationTokenSource();
        }

        var token = _previewPrefetchCts.Token;
        _ = PrefetchAdjacentImagesAsync(neighbours, token);
    }

    private async Task PrefetchAdjacentImagesAsync(IReadOnlyList<DriveItemModel> neighbours, CancellationToken cancellationToken)
    {
        foreach (var neighbour in neighbours)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                var cachedPath = await _fileCache.GetOrDownloadAsync(neighbour, _oneDrive, cancellationToken);
                if (IsMobilePlatform && neighbour.IsImage &&
                    !string.Equals(Path.GetExtension(neighbour.Name), ".gif", StringComparison.OrdinalIgnoreCase) &&
                    neighbour.GalleryImage is null && !string.IsNullOrWhiteSpace(cachedPath))
                {
                    var bitmap = await DecodePreviewBitmapAsync(cachedPath, cancellationToken, mobileMaxPreviewEdge: 2048);
                    cancellationToken.ThrowIfCancellationRequested();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (cancellationToken.IsCancellationRequested || neighbour.GalleryImage is not null)
                        {
                            bitmap.Dispose();
                            return;
                        }
                        neighbour.GalleryImage = bitmap;
                    });
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Prefetch is best-effort and must never interrupt the visible preview.
            }
        }
    }

    private void CancelPreviewPrefetch()
    {
        var cts = _previewPrefetchCts;
        _previewPrefetchCts = null;
        if (cts is null)
            return;
        cts.Cancel();
        cts.Dispose();
    }

    [RelayCommand]
    private async Task SavePreviewTextAsync()
    {
        if (PreviewItem is not { IsFile: true } item || PreviewKind != PreviewKind.Text)
            return;
        await RunBusyAsync(async () =>
        {
            var bytes = Encoding.UTF8.GetBytes(PreviewText);
            await using var stream = new MemoryStream(bytes, writable: false);
            await _oneDrive.UpdateFileContentAsync(item.Id, stream, "text/plain; charset=utf-8");
            _fileCache.Invalidate(item.Id);
            _thumbnailCache.Invalidate(item.Id);
            InvalidateCurrentFolderCache();
            PreviewStatus = "已保存到 OneDrive";
            await LoadCurrentFolderAsync(forceRemote: true);
        });
    }

    [RelayCommand]
    private void ClosePreview()
    {
        CancelPreviewLoad();
        CancelPreviewPrefetch();
        StopSlideshow();

        var previewIsGalleryBitmap = IsMobilePlatform && _gifAnimation is null &&
            PreviewItem?.GalleryImage is not null && ReferenceEquals(PreviewImage, PreviewItem.GalleryImage);

        IsPreviewVisible = false;
        IsPreviewLoading = false;
        IsPreviewDetailsVisible = false;
        PreviewKind = PreviewKind.None;
        PreviewText = string.Empty;
        PreviewMediaUrl = string.Empty;
        PreviewCachedFilePath = string.Empty;
        PreviewStatus = string.Empty;

        // Make the list visible first. Mobile gallery bitmaps are owned by DriveItemModel while
        // the carousel is open, so detach the current alias before disposing that small window.
        if (previewIsGalleryBitmap)
            PreviewImage = null;
        else
            DisposePreviewImageResourcesDeferred();

        PreviewItem = null;
        if (IsMobilePlatform)
            DisposeGalleryImagesDeferred();

        _previewImagePixelWidth = 0;
        _previewImagePixelHeight = 0;
        PreviewZoom = 1.0;
    }

    private void CancelPreviewLoad()
    {
        var cts = _previewLoadCts;
        _previewLoadCts = null;
        if (cts is null)
            return;
        cts.Cancel();
    }

    [RelayCommand]
    private async Task PreviewPreviousAsync() => await MovePreviewAsync(-1, imagesOnly: false, wrap: false);

    [RelayCommand]
    private async Task PreviewNextAsync() => await MovePreviewAsync(1, imagesOnly: false, wrap: false);

    /// <summary>
    /// Mobile gallery swipe navigation intentionally skips non-image files.
    /// </summary>
    public Task MoveImagePreviewAsync(int delta) => MovePreviewAsync(delta, imagesOnly: true, wrap: false);

    private async Task MovePreviewAsync(int delta, bool imagesOnly, bool wrap)
    {
        var sequence = GetVisibleLoadedItemsSnapshot().Where(x => x.IsFile && (!imagesOnly || x.IsImage)).ToArray();
        if (sequence.Length == 0)
            return;

        var index = PreviewItem is null ? -1 : Array.FindIndex(sequence, x => x.Id == PreviewItem.Id);
        if (index < 0)
            index = delta > 0 ? -1 : sequence.Length;

        var nextIndex = index + delta;
        if (wrap)
        {
            nextIndex = (nextIndex % sequence.Length + sequence.Length) % sequence.Length;
        }
        else if (nextIndex < 0 || nextIndex >= sequence.Length)
        {
            // Normal previous/next navigation stops at the first/last item.
            // Only slideshow mode is allowed to wrap around.
            return;
        }

        await LoadPreviewAsync(sequence[nextIndex], preserveSlideshow: IsSlideshowPlaying);
    }

    [RelayCommand]
    private void TogglePreviewDetails() => IsPreviewDetailsVisible = !IsPreviewDetailsVisible;

    [RelayCommand]
    private void ToggleSlideshow()
    {
        if (!IsImagePreview)
            return;

        if (IsSlideshowPlaying)
        {
            StopSlideshow();
            return;
        }

        IsSlideshowPlaying = true;
        _slideshowTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(SlideshowIntervalSeconds, 1, 3600));
        _slideshowTimer.Start();
    }

    private void StopSlideshow()
    {
        _slideshowTimer.Stop();
        IsSlideshowPlaying = false;
    }

    private async void SlideshowTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsSlideshowPlaying || !IsPreviewVisible)
        {
            StopSlideshow();
            return;
        }

        await MovePreviewAsync(1, imagesOnly: true, wrap: true);
    }

    public void AdjustPreviewZoom(double factor) => SetPreviewZoom(PreviewZoom * factor);
    public void SetPreviewZoomToActualSize() => SetPreviewZoom(1.0);
    public void SetPreviewZoomAbsolute(double value) => SetPreviewZoom(value);

    public void FitPreviewImage(double availableWidth, double availableHeight)
    {
        if (_previewImagePixelWidth <= 0 || _previewImagePixelHeight <= 0 || availableWidth <= 1 || availableHeight <= 1)
            return;

        var horizontalScale = Math.Max(0.01, (availableWidth - 20) / _previewImagePixelWidth);
        var verticalScale = Math.Max(0.01, (availableHeight - 20) / _previewImagePixelHeight);
        SetPreviewZoom(Math.Min(horizontalScale, verticalScale));
    }

    private void SetPreviewZoom(double value)
    {
        PreviewZoom = Math.Clamp(value, 0.01, 8.0);
        UpdatePreviewImageSize();
    }

    private void UpdatePreviewImageSize()
    {
        PreviewImageWidth = _previewImagePixelWidth * PreviewZoom;
        PreviewImageHeight = _previewImagePixelHeight * PreviewZoom;
    }

    private void StartGifTimer()
    {
        if (_gifAnimation is not { Frames.Count: > 1 })
            return;
        _gifTimer.Stop();
        _gifTimer.Interval = _gifAnimation.Delays[0];
        _gifTimer.Start();
    }

    private void GifTimer_Tick(object? sender, EventArgs e)
    {
        if (_gifAnimation is not { Frames.Count: > 1 } gif || !IsImagePreview)
        {
            _gifTimer.Stop();
            return;
        }

        _gifFrameIndex = (_gifFrameIndex + 1) % gif.Frames.Count;
        PreviewImage = gif.Frames[_gifFrameIndex];
        _gifTimer.Interval = gif.Delays[Math.Min(_gifFrameIndex, gif.Delays.Count - 1)];
    }

    private void DisposeGifAnimation()
    {
        _gifTimer.Stop();
        if (_gifAnimation is null)
            return;
        // PreviewImage points at one of these frames, so clear it before disposal.
        PreviewImage = null;
        _gifAnimation.Dispose();
        _gifAnimation = null;
        _gifFrameIndex = 0;
    }

    private void ReleasePreviewImageBeforeNavigation()
    {
        if (IsMobilePlatform && _gifAnimation is null && PreviewItem?.GalleryImage is not null &&
            ReferenceEquals(PreviewImage, PreviewItem.GalleryImage))
        {
            // The carousel still needs the old page while the finger/snap animation completes.
            // Detach the PreviewImage alias but keep the bitmap owned by that DriveItemModel.
            PreviewImage = null;
            return;
        }

        DisposePreviewImageResources();
    }

    private void TrimMobileGalleryImages(DriveItemModel current)
    {
        if (!IsMobilePlatform)
            return;

        var images = GetVisibleLoadedItemsSnapshot().Where(static x => x.IsImage).ToArray();
        var currentIndex = Array.FindIndex(images, x => x.Id == current.Id);
        if (currentIndex < 0)
            return;

        for (var i = 0; i < images.Length; i++)
        {
            if (Math.Abs(i - currentIndex) <= 2)
                continue;

            var bitmap = images[i].GalleryImage;
            if (bitmap is null || ReferenceEquals(bitmap, PreviewImage))
                continue;

            images[i].GalleryImage = null;
            bitmap.Dispose();
        }
    }

    private void DisposeGalleryImagesDeferred()
    {
        var bitmaps = _allItems
            .Where(static x => x.GalleryImage is not null)
            .Select(x =>
            {
                var bitmap = x.GalleryImage;
                x.GalleryImage = null;
                return bitmap;
            })
            .Where(static x => x is not null)
            .Cast<Bitmap>()
            .ToArray();

        if (bitmaps.Length == 0)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(180).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var bitmap in bitmaps)
                {
                    try { bitmap.Dispose(); } catch { }
                }
            }, DispatcherPriority.Background);
        });
    }

    private void DisposePreviewImageResources()
    {
        if (_gifAnimation is not null)
        {
            DisposeGifAnimation();
            return;
        }

        PreviewImage?.Dispose();
        PreviewImage = null;
    }

    private void DisposePreviewImageResourcesDeferred()
    {
        _gifTimer.Stop();

        var gif = _gifAnimation;
        _gifAnimation = null;
        _gifFrameIndex = 0;

        // A GIF's PreviewImage points to a frame owned by AnimatedGifData, so only the GIF
        // container should dispose it. For a normal image, the bitmap itself owns the resource.
        var bitmap = gif is null ? PreviewImage : null;
        PreviewImage = null;

        if (gif is null && bitmap is null)
            return;

        _ = DisposeDetachedPreviewResourcesAfterReturnAsync(gif, bitmap);
    }

    private static async Task DisposeDetachedPreviewResourcesAfterReturnAsync(AnimatedGifData? gif, Bitmap? bitmap)
    {
        // Give Android enough time to draw the underlying ItemsRepeater first. The user sees the
        // list immediately; expensive Skia/JNI cleanup happens after the Back transition settles.
        await Task.Delay(180).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (gif is not null)
                    gif.Dispose();
                else
                    bitmap?.Dispose();
            }
            catch
            {
                // Preview disposal is best-effort and must never block returning to the file list.
            }
        }, DispatcherPriority.Background);
    }

    public void UpdateLocalImageSetting(string bookmark, string displayName)
    {
        Settings.LocalImageBookmark = bookmark;
        Settings.LocalImageDisplayName = displayName;
        LocalImageDisplayName = displayName;
        SelectedBackgroundModeText = "本地图片";
        _ = _settingsService.SaveAsync();
    }

    public void UpdateLocalFolderSetting(string bookmark, string displayName)
    {
        Settings.LocalFolderBookmark = bookmark;
        Settings.LocalFolderDisplayName = displayName;
        LocalFolderDisplayName = displayName;
        SelectedBackgroundModeText = "本地文件夹";
        _ = _settingsService.SaveAsync();
    }

    public void UseCurrentOneDriveFolderAsBackground()
    {
        Settings.OneDriveBackgroundFolderId = CurrentFolderId ?? "__ROOT__";
        Settings.OneDriveBackgroundFolderName = CurrentLocation;
        OneDriveBackgroundFolderName = CurrentLocation;
        SelectedBackgroundModeText = "OneDrive 文件夹";
        _ = _settingsService.SaveAsync();
    }

    public async Task PersistSettingsAsync()
    {
        Settings.ThemeMode = SelectedThemeText switch
        {
            "浅色" => AppThemeMode.Light,
            "深色" => AppThemeMode.Dark,
            _ => AppThemeMode.System
        };
        Settings.BackgroundMode = CurrentBackgroundMode;
        Settings.BackgroundColor = string.IsNullOrWhiteSpace(BackgroundColorText) ? "#F7F7F8" : BackgroundColorText.Trim();
        Settings.BackgroundUrl = BackgroundUrl.Trim();
        Settings.BackgroundIntervalMinutes = Math.Max(0.1, BackgroundIntervalMinutes);
        Settings.AcrylicBlurPercent = Math.Clamp(AcrylicBlurPercent, 0, 100);
        Settings.RememberLastFolder = RememberLastFolder;
        Settings.ShowFloatingUploadButton = ShowFloatingUploadButton;
        Settings.ShowToolbar = ShowToolbar;
        Settings.TransparentFileItemBackground = TransparentFileItemBackground;
        Settings.ConfirmBeforeDelete = ConfirmBeforeDelete;
        Settings.UseBuiltInViewer = UseBuiltInViewer;
        Settings.StartWithWindows = StartWithWindows;
        var defaultSort = ParseDefaultSortText(SelectedDefaultSortText);
        Settings.DefaultSortColumn = defaultSort.Column;
        Settings.DefaultSortState = defaultSort.State;
        Settings.SlideshowIntervalSeconds = Math.Clamp(SlideshowIntervalSeconds, 1, 3600);
        Settings.LimitDownloadSpeed = LimitDownloadSpeed;
        Settings.DownloadSpeedLimitKBps = Math.Max(1, DownloadSpeedLimitKBps);
        Settings.LimitUploadSpeed = LimitUploadSpeed;
        Settings.UploadSpeedLimitKBps = Math.Max(1, UploadSpeedLimitKBps);
        ApplyTransferRateLimits();
        if (RememberLastFolder)
            CaptureCurrentFolderMemory();
        await _settingsService.SaveAsync();
    }

    public Task ResumeCacheFileAsync(DriveItemModel item, TransferItemModel transfer, CancellationToken cancellationToken = default) =>
        CacheFileTransferAsync(item, transfer, cancellationToken);

    public async Task CacheItemsAsync(IEnumerable<DriveItemModel> items, CancellationToken cancellationToken = default)
    {
        var requested = items.Where(x => x is not null).DistinctBy(x => x.Id).ToArray();
        if (requested.Length == 0)
            return;

        // Open the panel before doing any network enumeration. Folder cache discovery is
        // incremental: each fetched page registers its file rows immediately, then the next
        // page/subfolder is discovered in the background while already queued files can cache.
        IsTransferPanelVisible = true;
        StatusText = "正在获取缓存文件…";
        // Let the transfer panel paint before the first Graph page request starts.
        await Task.Yield();

        var jobs = new List<CacheJob>();
        var runningTasks = new List<Task>();
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var seenFolders = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var item in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.IsFile)
                {
                    QueueCacheFile(item, jobs, runningTasks, seenFiles, cancellationToken);
                }
                else if (!string.IsNullOrWhiteSpace(item.Id) && seenFolders.Add(item.Id))
                {
                    await DiscoverFolderCacheJobsIncrementallyAsync(
                        item.Id,
                        jobs,
                        runningTasks,
                        seenFiles,
                        seenFolders,
                        cancellationToken);
                }
            }

            if (jobs.Count == 0)
            {
                StatusText = "没有需要缓存的文件";
                return;
            }

            // Discovery can finish before transfers do. All concrete file rows have already
            // appeared in the panel, and at most two file bodies are cached concurrently.
            await Task.WhenAll(runningTasks);

            var completed = jobs.Count(x => x.Transfer.State == TransferState.Completed);
            UpdateCacheStatus();
            StatusText = $"缓存任务完成：{completed}/{jobs.Count} 个文件";
        }
        catch (OperationCanceledException)
        {
            foreach (var job in jobs.Where(x => x.Transfer.State is TransferState.Waiting or TransferState.Running))
            {
                job.Transfer.State = TransferState.Cancelled;
                job.Transfer.Message = "已取消";
            }
            RaiseTransferSummary();
            StatusText = "缓存已取消";
        }
        catch (Exception ex)
        {
            // Read-side Graph calls normally stay inside the automatic retry loop. If a transient
            // transport failure still escapes a lower layer, keep existing jobs and stay silent.
            if (IsTransientNetworkFailure(ex))
            {
                ErrorMessage = null;
            }
            else
            {
                ErrorMessage = ex.Message;
                StatusText = jobs.Count > 0 ? "部分缓存任务已加入，后续文件获取失败" : "缓存任务准备失败";
            }
        }
    }

    private async Task DiscoverFolderCacheJobsIncrementallyAsync(
        string rootFolderId,
        List<CacheJob> jobs,
        List<Task> runningTasks,
        HashSet<string> seenFiles,
        HashSet<string> seenFolders,
        CancellationToken cancellationToken)
    {
        // Breadth-first traversal avoids diving through one deep branch before the user sees
        // files from the rest of the selected folder. Each Graph page is consumed immediately.
        var folders = new Queue<string>();
        folders.Enqueue(rootFolderId);

        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderId = folders.Dequeue();
            string? nextLink = null;

            do
            {
                var page = await _oneDrive.GetChildrenPageAsync(folderId, nextLink, 120, cancellationToken);

                // First add every file from this page to the transfer list. This is intentionally
                // done before requesting the next page or entering child folders.
                foreach (var child in page.Items.Where(x => x.IsFile))
                    QueueCacheFile(child, jobs, runningTasks, seenFiles, cancellationToken);

                // Then remember subfolders for later breadth-first discovery.
                foreach (var childFolder in page.Items.Where(x => !x.IsFile && !string.IsNullOrWhiteSpace(x.Id)))
                {
                    if (seenFolders.Add(childFolder.Id))
                        folders.Enqueue(childFolder.Id);
                }

                // Give Avalonia a render opportunity after every page so newly queued rows are
                // visible immediately rather than appearing in one large batch at the end.
                await Task.Yield();
                nextLink = page.NextLink;
            } while (!string.IsNullOrWhiteSpace(nextLink));
        }
    }

    private void QueueCacheFile(
        DriveItemModel item,
        List<CacheJob> jobs,
        List<Task> runningTasks,
        HashSet<string> seenFiles,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Id) || !seenFiles.Add(item.Id))
            return;

        var transfer = RegisterTransfer(item.Name, TransferDirection.Cache);
        SetTransferResumeInfo(transfer, new TransferResumeInfo
        {
            AccountId = CurrentAccountId,
            Kind = TransferResumeKind.CacheFile,
            OneDriveItemId = item.Id
        });

        var capturedItem = item;
        transfer.RetryAction = () => CacheFileTransferAsync(capturedItem, transfer, CancellationToken.None);
        jobs.Add(new CacheJob(item, transfer));
        runningTasks.Add(CacheFileTransferAsync(item, transfer, cancellationToken));
    }

    private async Task CacheFileTransferAsync(
        DriveItemModel item,
        TransferItemModel transfer,
        CancellationToken cancellationToken)
    {
        var gateEntered = false;
        try
        {
            // Keep newly discovered rows in "等待缓存" while two active workers do the actual
            // file I/O. This lets folder enumeration continue without launching hundreds of
            // simultaneous Graph downloads.
            await _cacheTransferGate.WaitAsync(cancellationToken);
            gateEntered = true;

            transfer.Progress = 0;
            transfer.State = TransferState.Running;
            transfer.Message = "正在缓存";
            StatusText = $"正在缓存：{item.Name}";

            // The file body owns 95% of the visible progress; thumbnail warming is the final
            // step. High-frequency byte callbacks are throttled and marshalled to the UI thread
            // so caching cannot flood Avalonia's dispatcher.
            var progress = new ThrottledUiProgress(p =>
            {
                if (transfer.State != TransferState.Running)
                    return;

                transfer.Progress = Math.Clamp(p, 0, 1) * 0.95;
                transfer.Message = "正在缓存";
            });

            await _fileCache.GetOrDownloadAsync(item, _oneDrive, cancellationToken, progress);

            if (item.SupportsThumbnail)
            {
                transfer.Message = "正在缓存缩略图";
                try
                {
                    await _thumbnailCache.GetOrDownloadAsync(item, _oneDrive, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Thumbnail warming is best-effort. The original file cache remains valid.
                }
            }

            transfer.Progress = 1;
            transfer.State = TransferState.Completed;
            transfer.Message = "已缓存";
            transfer.ResumeInfo = null;
            transfer.RetryAction = null;
            StatusText = $"缓存完成：{item.Name}";
            UpdateCacheStatus();
        }
        catch (OperationCanceledException)
        {
            transfer.State = TransferState.Cancelled;
            transfer.Message = "已取消";
            throw;
        }
        catch (Exception ex)
        {
            transfer.State = TransferState.Failed;
            transfer.Message = ex.Message;
            StatusText = $"缓存失败：{item.Name}";
        }
        finally
        {
            if (gateEntered)
                _cacheTransferGate.Release();
            RaiseTransferSummary();
        }
    }

    private sealed record CacheJob(DriveItemModel Item, TransferItemModel Transfer);

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        // Stop writers before deleting the metadata index; otherwise an in-flight delta/folder
        // enumeration could persist the just-cleared cache again a moment later.
        Interlocked.Exchange(ref _folderMetadataSyncCts, null)?.Cancel();
        Interlocked.Exchange(ref _driveIndexSyncCts, null)?.Cancel();
        _fileCache.Clear();
        _thumbnailCache.Clear();
        if (!string.IsNullOrWhiteSpace(CurrentAccountId))
            _localDriveIndex.ClearAccount(CurrentAccountId);
        else
            _localDriveIndex.ClearAll();
        ClearFolderCache();
        UpdateCacheStatus();

        if (IsAuthenticated)
        {
            await RunBusyAsync(() => LoadCurrentFolderAsync(forceRemote: true));
            if (!string.IsNullOrWhiteSpace(CurrentAccountId))
                _ = StartDriveIndexSynchronizationAsync(CurrentAccountId);
            StatusText = "缓存已清除";
        }
        else
        {
            StatusText = "缓存已清除";
        }
    }

    public async Task SaveFloatingUploadPositionAsync(double normalizedX, double normalizedY)
    {
        Settings.FloatingUploadX = Math.Clamp(normalizedX, 0, 1);
        Settings.FloatingUploadY = Math.Clamp(normalizedY, 0, 1);
        await _settingsService.SaveAsync();
    }

    public (double X, double Y) GetFloatingUploadPosition() =>
        (Math.Clamp(Settings.FloatingUploadX, 0, 1), Math.Clamp(Settings.FloatingUploadY, 0, 1));

    private void UpdateCacheStatus()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _cacheStatusRefreshCts, next);
        previous?.Cancel();
        _ = RefreshCacheStatusAfterDelayAsync(next);
    }

    private async Task RefreshCacheStatusAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            // Cache size calculation recursively scans both cache trees. Doing that synchronously
            // on the UI thread caused visible hitches after large downloads/caches completed.
            await Task.Delay(300, cts.Token).ConfigureAwait(false);
            var sizes = await Task.Run(() =>
            {
                var fileBytes = _fileCache.GetCacheSizeBytes();
                var thumbnailBytes = _thumbnailCache.GetCacheSizeBytes();
                return (fileBytes, thumbnailBytes);
            }, cts.Token).ConfigureAwait(false);

            var cacheRoot = Path.GetDirectoryName(_fileCache.CacheRoot) ?? _fileCache.CacheRoot;
            var text = $"总计 {DriveItemModel.FormatBytes(sizes.fileBytes + sizes.thumbnailBytes)} · " +
                       $"文件 {DriveItemModel.FormatBytes(sizes.fileBytes)} · " +
                       $"缩略图 {DriveItemModel.FormatBytes(sizes.thumbnailBytes)}\n{cacheRoot}";
            Dispatcher.UIThread.Post(() => CacheStatusText = text, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            // A newer cache change superseded this scan.
        }
        catch
        {
            // Cache statistics are informational and must never interrupt normal use.
        }
        finally
        {
            if (ReferenceEquals(_cacheStatusRefreshCts, cts))
                _cacheStatusRefreshCts = null;
            cts.Dispose();
        }
    }

    private void CaptureCurrentFolderMemory()
    {
        Settings.LastFolderBreadcrumbs = Breadcrumbs
            .Select(x => new RememberedBreadcrumb { Name = x.Name, ItemId = x.ItemId })
            .ToList();
    }

    private void LoadSettingsIntoProperties()
    {
        var s = Settings;
        SelectedThemeText = s.ThemeMode switch
        {
            AppThemeMode.Light => "浅色",
            AppThemeMode.Dark => "深色",
            _ => "跟随系统"
        };
        ViewMode = s.ViewMode;
        SelectedBackgroundModeText = s.BackgroundMode switch
        {
            WindowBackgroundMode.Color => "纯色",
            WindowBackgroundMode.LocalImage => "本地图片",
            WindowBackgroundMode.Url => "图片 URL",
            WindowBackgroundMode.LocalFolder => "本地文件夹",
            WindowBackgroundMode.OneDriveFolder => "OneDrive 文件夹",
            _ => "默认"
        };
        BackgroundColorText = s.BackgroundColor;
        BackgroundUrl = s.BackgroundUrl;
        BackgroundIntervalMinutes = s.BackgroundIntervalMinutes;
        AcrylicBlurPercent = s.AcrylicBlurPercent;
        LocalImageDisplayName = s.LocalImageDisplayName;
        LocalFolderDisplayName = s.LocalFolderDisplayName;
        OneDriveBackgroundFolderName = s.OneDriveBackgroundFolderName;
        RememberLastFolder = s.RememberLastFolder;
        ShowFloatingUploadButton = s.ShowFloatingUploadButton;
        ShowToolbar = s.ShowToolbar;
        TransparentFileItemBackground = s.TransparentFileItemBackground;
        ConfirmBeforeDelete = s.ConfirmBeforeDelete;
        UseBuiltInViewer = s.UseBuiltInViewer;

        _syncingStartWithWindowsSetting = true;
        StartWithWindows = _startupRegistrationService?.IsSupported == true
            ? _startupRegistrationService.IsEnabled
            : false;
        _syncingStartWithWindowsSetting = false;
        s.StartWithWindows = StartWithWindows;

        _syncingDefaultSortSetting = true;
        SelectedDefaultSortText = FormatDefaultSortText(s.DefaultSortColumn, s.DefaultSortState);
        _syncingDefaultSortSetting = false;

        SlideshowIntervalSeconds = s.SlideshowIntervalSeconds;
        LimitDownloadSpeed = s.LimitDownloadSpeed;
        DownloadSpeedLimitKBps = s.DownloadSpeedLimitKBps;
        LimitUploadSpeed = s.LimitUploadSpeed;
        UploadSpeedLimitKBps = s.UploadSpeedLimitKBps;
    }

    private void ApplyTransferRateLimits()
    {
        static long ToBytesPerSecond(double kbps) =>
            (long)Math.Clamp(kbps * 1024d, 1024d, (double)long.MaxValue);

        _oneDrive.DownloadBytesPerSecondLimit = LimitDownloadSpeed ? ToBytesPerSecond(Math.Max(1, DownloadSpeedLimitKBps)) : null;
        _oneDrive.UploadBytesPerSecondLimit = LimitUploadSpeed ? ToBytesPerSecond(Math.Max(1, UploadSpeedLimitKBps)) : null;
    }

    private void TryRestoreStartupSnapshot()
    {
        var snapshot = _startupSnapshot.TryLoad();
        if (snapshot is null)
            return;

        if (!Settings.RememberLastFolder && snapshot.Breadcrumbs.Count > 1)
        {
            _startupSnapshot.Clear();
            return;
        }

        var restoredItems = snapshot.Items.Select(static x => x.ToModel()).ToList();

        _startupSnapshotRestored = true;
        _startupSnapshotAccountId = snapshot.AccountId;
        CurrentAccountId = snapshot.AccountId;
        UserDisplayName = snapshot.UserDisplayName;
        UserEmail = snapshot.UserEmail;
        QuotaText = snapshot.QuotaText;
        QuotaUsedBytes = snapshot.QuotaUsedBytes;
        QuotaTotalBytes = snapshot.QuotaTotalBytes;
        IsAuthenticated = true;

        Breadcrumbs.Clear();
        foreach (var crumb in snapshot.Breadcrumbs)
        {
            if (!string.IsNullOrWhiteSpace(crumb.Name))
                Breadcrumbs.Add(new BreadcrumbItem(crumb.Name, crumb.ItemId));
        }

        if (Breadcrumbs.Count == 0 || Breadcrumbs[0].ItemId is not null)
        {
            Breadcrumbs.Clear();
            Breadcrumbs.Add(new BreadcrumbItem("OneDrive", null));
        }

        RestoreCurrentFolderViewMode();
        RestoreCurrentFolderSortRule();
        _nextChildrenLink = snapshot.NextLink;
        HasMoreItems = !string.IsNullOrWhiteSpace(_nextChildrenLink);
        SetCurrentFolderTotalItemCount(snapshot.TotalItemCount);

        _allItems.Clear();
        _currentItemIds.Clear();
        _allItems.AddRange(restoredItems);
        foreach (var item in restoredItems)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
                _currentItemIds.Add(item.Id);
        }

        ApplyFilterAndSort();
        CurrentLocation = string.Join(" / ", Breadcrumbs.Select(x => x.Name));
        StatusText = snapshot.TotalItemCount is > 0
            ? $"{snapshot.TotalItemCount.Value} 个项目 · 正在同步"
            : $"{_allItems.Count} 个项目 · 正在同步";

        var cacheKey = FolderCacheKey(CurrentFolderId);
        _folderCache[cacheKey] = new FolderCacheEntry(
            restoredItems.ToList(),
            snapshot.NextLink,
            snapshot.TotalItemCount,
            snapshot.SavedAtUtc,
            GetGraphOrderBy());

        // Decode only thumbnails already present in the persistent thumbnail cache. No network
        // request is started here, so the restored screen stays responsive before MSAL finishes.
        if (IsMobilePlatform && !UsesNativeMobileFileList)
        {
            var cachedThumbs = restoredItems
                .Where(x => x.SupportsThumbnail && _thumbnailCache.TryGetCachedPath(x, out _))
                .Take(48)
                .ToArray();
            if (cachedThumbs.Length > 0)
                StartThumbnailLoading(cachedThumbs);
        }
        // Desktop thumbnails are queued from the actually realized ItemsRepeater viewport after
        // layout. Never construct a 96/200/thousands-item task batch during first paint.
    }

    private void RestoreRememberedStartupBreadcrumbs()
    {
        Breadcrumbs.Clear();
        if (Settings.RememberLastFolder && Settings.LastFolderBreadcrumbs.Count > 0)
        {
            foreach (var crumb in Settings.LastFolderBreadcrumbs)
            {
                if (!string.IsNullOrWhiteSpace(crumb.Name))
                    Breadcrumbs.Add(new BreadcrumbItem(crumb.Name, crumb.ItemId));
            }
        }

        if (Breadcrumbs.Count == 0 || Breadcrumbs[0].ItemId is not null)
        {
            Breadcrumbs.Clear();
            Breadcrumbs.Add(new BreadcrumbItem("OneDrive", null));
        }
    }

    private async Task LoadProfilePhotoInBackgroundAsync()
    {
        try
        {
            var photo = await _oneDrive.GetProfilePhotoAsync();
            if (photo is not { Length: > 0 })
                return;

            using var ms = new MemoryStream(photo);
            var bitmap = new Bitmap(ms);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UserAvatar?.Dispose();
                UserAvatar = bitmap;
            });
        }
        catch
        {
            // The profile photo is optional and must never affect startup or folder browsing.
        }
    }

    private async Task RefreshRestoredFolderInBackgroundAsync()
    {
        try
        {
            await LoadCurrentFolderAsync(forceRemote: true);
            StatusText = $"{(_currentFolderTotalItemCount ?? _allItems.Count)} 个项目";
        }
        catch when (Breadcrumbs.Count > 1)
        {
            try
            {
                Breadcrumbs.Clear();
                Breadcrumbs.Add(new BreadcrumbItem("OneDrive", null));
                Settings.LastFolderBreadcrumbs.Clear();
                await _settingsService.SaveAsync();
                await LoadCurrentFolderAsync(forceRemote: true);
            }
            catch (Exception ex)
            {
                if (IsTransientNetworkFailure(ex))
                {
                    ErrorMessage = null;
                    StatusText = "已显示本地缓存";
                }
                else
                {
                    ErrorMessage = $"OneDrive 同步失败：{ex.Message}";
                    StatusText = "已显示本地缓存 · 暂时无法同步";
                }
            }
        }
        catch (Exception ex)
        {
            if (IsTransientNetworkFailure(ex))
            {
                ErrorMessage = null;
                StatusText = "已显示本地缓存";
            }
            else
            {
                ErrorMessage = $"OneDrive 同步失败：{ex.Message}";
                StatusText = "已显示本地缓存 · 暂时无法同步";
            }
        }
    }

    private async Task SaveStartupSnapshotAsync()
    {
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(CurrentAccountId))
            return;

        // Respect "remember last folder". When disabled, only a root snapshot is persisted.
        if (!Settings.RememberLastFolder && CurrentFolderId is not null)
            return;

        var snapshot = new StartupSnapshot
        {
            SavedAtUtc = DateTimeOffset.UtcNow,
            AccountId = CurrentAccountId,
            UserDisplayName = UserDisplayName,
            UserEmail = UserEmail,
            QuotaText = QuotaText,
            QuotaUsedBytes = QuotaUsedBytes,
            QuotaTotalBytes = QuotaTotalBytes,
            Breadcrumbs = Breadcrumbs
                .Select(x => new RememberedBreadcrumb { Name = x.Name, ItemId = x.ItemId })
                .ToList(),
            // Every platform now rebuilds the complete logical extent from TotalItemCount and
            // LocalDriveIndex. The startup snapshot therefore only needs one first-paint page;
            // serializing thousands of duplicate metadata rows would slow desktop startup too.
            Items = _allItems.Take(CurrentFolderPageSize)
                .Select(StartupDriveItem.FromModel)
                .ToList(),
            NextLink = null,
            TotalItemCount = _currentFolderTotalItemCount
        };

        await _startupSnapshot.SaveAsync(snapshot);
    }

    private void ScheduleStartupSnapshotSave()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _startupSnapshotSaveCts, next);
        previous?.Cancel();
        _ = SaveStartupSnapshotAfterListIdleAsync(next);
    }

    private async Task SaveStartupSnapshotAfterListIdleAsync(CancellationTokenSource request)
    {
        try
        {
            // The snapshot is a startup optimization, not part of the paging critical path.
            // Coalesce rapid page arrivals and, on phones, wait until inertia has stopped so the
            // model projection never steals time from a scroll frame.
            await Task.Delay(IsMobilePlatform ? 450 : 250, request.Token);
            while ((IsMobilePlatform && _mobileListScrolling) ||
                   (!IsMobilePlatform && _desktopListScrolling))
            {
                await Task.Delay(IsMobilePlatform ? 220 : 120, request.Token);
            }

            request.Token.ThrowIfCancellationRequested();
            await SaveStartupSnapshotAsync();
        }
        catch (OperationCanceledException)
        {
            // A newer page/navigation snapshot superseded this delayed save.
        }
        finally
        {
            if (ReferenceEquals(_startupSnapshotSaveCts, request))
                _startupSnapshotSaveCts = null;
            request.Dispose();
        }
    }

    private void ClearRestoredStartupState(bool clearAuthenticationState = true)
    {
        _startupSnapshotRestored = false;
        _startupSnapshotAccountId = string.Empty;
        _startupSnapshot.Clear();
        var pendingSnapshotSave = Interlocked.Exchange(ref _startupSnapshotSaveCts, null);
        pendingSnapshotSave?.Cancel();
        CancelThumbnailLoading();
        ResetMobileThumbnailWindow();
        ResetDesktopThumbnailViewport();
        ClearFolderCache();
        Items.Clear();
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbItem("OneDrive", null));
        SetCurrentFolderTotalItemCount(null);
        CurrentLocation = "OneDrive";
        SetSelectedItems([]);

        if (!clearAuthenticationState)
            return;

        IsAuthenticated = false;
        UserDisplayName = string.Empty;
        UserEmail = string.Empty;
        CurrentAccountId = string.Empty;
        UserAvatar?.Dispose();
        UserAvatar = null;
        QuotaText = string.Empty;
        QuotaUsedBytes = 0;
        QuotaTotalBytes = 0;
    }

    private async Task LoadSignedInStateAsync(bool preferRestoredSnapshot = false)
    {
        // Profile and quota are independent Graph calls. Running them together removes one
        // complete network round-trip from the startup critical path.
        var userTask = _oneDrive.GetCurrentUserAsync();
        var driveTask = _oneDrive.GetDriveInfoAsync();
        await Task.WhenAll(userTask, driveTask);

        var user = await userTask;
        var drive = await driveTask;
        var resolvedAccountId = !string.IsNullOrWhiteSpace(user.Id) ? user.Id! : user.DisplayEmail;
        var restoredSnapshotMatchesAccount =
            preferRestoredSnapshot &&
            _startupSnapshotRestored &&
            string.Equals(_startupSnapshotAccountId, resolvedAccountId, StringComparison.Ordinal);

        UserDisplayName = user.DisplayName ?? "Microsoft 用户";
        UserEmail = user.DisplayEmail;
        CurrentAccountId = resolvedAccountId;
        IsAuthenticated = true;
        await _localDriveIndex.EnsureAccountLoadedAsync(CurrentAccountId);
        _driveIndexRefreshTimer.Start();

        if (drive.Quota?.Total is > 0 && drive.Quota.Used is not null)
        {
            QuotaUsedBytes = drive.Quota.Used.Value;
            QuotaTotalBytes = drive.Quota.Total.Value;
            QuotaText = $"已用 {DriveItemModel.FormatBytes(QuotaUsedBytes)} / {DriveItemModel.FormatBytes(QuotaTotalBytes)}";
        }
        else
        {
            QuotaUsedBytes = 0;
            QuotaTotalBytes = 0;
            QuotaText = "OneDrive";
        }

        // The avatar is cosmetic and must never delay the directory listing.
        _ = LoadProfilePhotoInBackgroundAsync();

        if (restoredSnapshotMatchesAccount)
        {
            _startupSnapshotRestored = false;
            StatusText = "已显示本地缓存 · 正在同步 OneDrive";

            // The cached folder is already on screen. Revalidate it without a busy overlay so
            // startup feels immediate even when Graph needs several seconds to answer.
            var restoredFolderId = CurrentFolderId;
            var restoredCacheKey = FolderCacheKey(restoredFolderId);
            var restoredNavigationVersion = _folderNavigationVersion;
            var restoredLocalSnapshot = await _localDriveIndex
                .GetFolderAsync(CurrentAccountId, restoredFolderId, GetGraphOrderBy())
                .ConfigureAwait(true);
            if (restoredLocalSnapshot is not null &&
                restoredNavigationVersion == _folderNavigationVersion &&
                FolderCacheKey(CurrentFolderId) == restoredCacheKey &&
                restoredLocalSnapshot.Items.Count >= _allItems.Count)
            {
                StoreFolderCache(restoredCacheKey, restoredLocalSnapshot.Items, null, restoredLocalSnapshot.TotalCount);
                if (_folderCache.TryGetValue(restoredCacheKey, out var restoredEntry))
                    restoredEntry.LastValidatedUtc = restoredLocalSnapshot.LastSyncedUtc ?? DateTimeOffset.MinValue;
                SetCurrentFolderTotalItemCount(restoredLocalSnapshot.TotalCount);
                ApplyFolderItems(restoredLocalSnapshot.Items, restoredLocalSnapshot.TotalCount);
            }
            else if (restoredLocalSnapshot is not null)
            {
                DisposeItemThumbnails(restoredLocalSnapshot.Items);
            }

            _ = RefreshRestoredFolderInBackgroundAsync();
            _ = StartDriveIndexSynchronizationAsync(CurrentAccountId);
            _ = SaveStartupSnapshotAsync();
            return;
        }

        if (preferRestoredSnapshot && _startupSnapshotRestored)
        {
            // The cached metadata belongs to a different Microsoft account. Never keep another
            // account's file names on screen after the actual signed-in identity is known.
            ClearRestoredStartupState(clearAuthenticationState: false);
            CurrentAccountId = resolvedAccountId;
            UserDisplayName = user.DisplayName ?? "Microsoft 用户";
            UserEmail = user.DisplayEmail;
            IsAuthenticated = true;
            if (drive.Quota?.Total is > 0 && drive.Quota.Used is not null)
            {
                QuotaUsedBytes = drive.Quota.Used.Value;
                QuotaTotalBytes = drive.Quota.Total.Value;
                QuotaText = $"已用 {DriveItemModel.FormatBytes(QuotaUsedBytes)} / {DriveItemModel.FormatBytes(QuotaTotalBytes)}";
            }
            else
            {
                QuotaUsedBytes = 0;
                QuotaTotalBytes = 0;
                QuotaText = "OneDrive";
            }
            Settings.LastFolderBreadcrumbs.Clear();
            await _settingsService.SaveAsync();
        }

        RestoreRememberedStartupBreadcrumbs();

        try
        {
            await LoadCurrentFolderAsync();
        }
        catch when (Breadcrumbs.Count > 1)
        {
            // The remembered folder may have been moved/deleted while the app was closed.
            Breadcrumbs.Clear();
            Breadcrumbs.Add(new BreadcrumbItem("OneDrive", null));
            Settings.LastFolderBreadcrumbs.Clear();
            await _settingsService.SaveAsync();
            await LoadCurrentFolderAsync(forceRemote: true);
        }

        _ = StartDriveIndexSynchronizationAsync(CurrentAccountId);
        _ = SaveStartupSnapshotAsync();
    }

    private void DriveIndexRefreshTimer_Tick(object? sender, EventArgs e)
    {
        // Delta is a low-priority cache-coherency pass. Never stack periodic runs or interrupt
        // a full initial scan; the next five-minute tick is sufficient.
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(CurrentAccountId) ||
            Volatile.Read(ref _driveIndexSyncCts) is not null)
            return;

        _ = StartDriveIndexSynchronizationAsync(CurrentAccountId);
    }

    private async Task StartDriveIndexSynchronizationAsync(string accountId)
    {
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(accountId))
            return;

        var request = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _driveIndexSyncCts, request);
        previous?.Cancel();
        var token = request.Token;

        try
        {
            // Current-folder first paint and touch scrolling always win. The drive-wide index is
            // deliberately background work and carries no thumbnails/file bodies.
            await Task.Delay(IsMobilePlatform ? 1400 : 650, token).ConfigureAwait(false);
            await WaitForDriveIndexQuietWindowAsync(token).ConfigureAwait(false);

            var rootItemId = _localDriveIndex.GetRootItemId(accountId);
            if (string.IsNullOrWhiteSpace(rootItemId))
            {
                var root = await _oneDrive.GetItemMetadataAsync(null, token).ConfigureAwait(false);
                rootItemId = root.Id;
            }
            if (string.IsNullOrWhiteSpace(rootItemId))
                return;

            var deltaLink = _localDriveIndex.GetDeltaLink(accountId);
            if (string.IsNullOrWhiteSpace(deltaLink))
            {
                await RunFullDriveIndexScanAsync(accountId, rootItemId, null, token).ConfigureAwait(false);
                return;
            }

            var changes = new List<DriveItemModel>();
            var cursor = deltaLink;
            string? finalDeltaLink = null;
            while (!string.IsNullOrWhiteSpace(cursor))
            {
                token.ThrowIfCancellationRequested();
                await WaitForDriveIndexQuietWindowAsync(token).ConfigureAwait(false);
                var page = await _oneDrive.GetDriveDeltaPageAsync(cursor, 200, token).ConfigureAwait(false);
                if (page.ResyncRequired)
                {
                    await RunFullDriveIndexScanAsync(accountId, rootItemId, page.ResyncLink, token).ConfigureAwait(false);
                    return;
                }

                changes.AddRange(page.Items);
                if (!string.IsNullOrWhiteSpace(page.NextLink))
                {
                    cursor = page.NextLink;
                    continue;
                }

                finalDeltaLink = page.DeltaLink;
                break;
            }

            if (!string.IsNullOrWhiteSpace(finalDeltaLink))
            {
                await WaitForDriveIndexQuietWindowAsync(token).ConfigureAwait(false);
                await _localDriveIndex.ApplyIncrementalDeltaAsync(
                    accountId,
                    rootItemId,
                    changes,
                    finalDeltaLink,
                    token).ConfigureAwait(false);

                if (changes.Count > 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!IsAuthenticated || !string.Equals(CurrentAccountId, accountId, StringComparison.Ordinal) ||
                            SelectionCount > 0 || _mobileListScrolling ||
                            Volatile.Read(ref _folderMetadataSyncCts) is not null)
                            return;

                        var folderId = CurrentFolderId;
                        StartFolderMetadataSync(
                            folderId,
                            FolderCacheKey(folderId),
                            _folderNavigationVersion,
                            GetGraphOrderBy(),
                            seedItems: null,
                            nextLink: null,
                            streamIntoPlaceholders: false);
                    });
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A logout/new sync superseded this index run.
        }
        catch
        {
            // The index is an offline/performance layer. Network or delta-token failures must not
            // affect normal browsing; the next session/refresh will try again.
        }
        finally
        {
            if (ReferenceEquals(_driveIndexSyncCts, request))
                Interlocked.CompareExchange(ref _driveIndexSyncCts, null, request);
            request.Dispose();
        }
    }

    private async Task WaitForDriveIndexQuietWindowAsync(CancellationToken cancellationToken)
    {
        while ((IsMobilePlatform && _mobileListScrolling) ||
               (!IsMobilePlatform && _desktopListScrolling) ||
               Volatile.Read(ref _folderMetadataSyncCts) is not null)
        {
            // Folder navigation / touch rendering is latency-sensitive. A drive-wide delta pass is
            // durable cache maintenance, so it yields between every Graph page and before JSON
            // persistence instead of competing with an active folder enumeration or fling.
            await Task.Delay(180, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunFullDriveIndexScanAsync(
        string accountId,
        string rootItemId,
        string? firstLink,
        CancellationToken cancellationToken)
    {
        var latest = new Dictionary<string, DriveItemModel>(StringComparer.Ordinal);
        var cursor = firstLink;
        string? finalDeltaLink = null;
        var restarted = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForDriveIndexQuietWindowAsync(cancellationToken).ConfigureAwait(false);
            var page = await _oneDrive.GetDriveDeltaPageAsync(cursor, 200, cancellationToken).ConfigureAwait(false);
            if (page.ResyncRequired)
            {
                if (restarted)
                    return;
                restarted = true;
                latest.Clear();
                cursor = page.ResyncLink;
                continue;
            }

            foreach (var item in page.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Id))
                    continue;
                if (item.IsDeleted)
                    latest.Remove(item.Id);
                else
                    latest[item.Id] = item; // Delta may return one item more than once; last wins.
            }

            if (!string.IsNullOrWhiteSpace(page.NextLink))
            {
                cursor = page.NextLink;
                continue;
            }

            finalDeltaLink = page.DeltaLink;
            break;
        }

        if (!string.IsNullOrWhiteSpace(finalDeltaLink))
        {
            await WaitForDriveIndexQuietWindowAsync(cancellationToken).ConfigureAwait(false);
            await _localDriveIndex.ReplaceFromFullDeltaAsync(
                accountId,
                rootItemId,
                latest.Values.ToArray(),
                finalDeltaLink,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private CancellationTokenSource BeginFolderNavigation(FolderNavigationReason reason)
    {
        // Capture the view of the folder we are leaving before Breadcrumbs changes. This makes
        // even inherited/default views become an explicit per-folder memory after the first visit.
        RememberCurrentFolderViewMode();
        _ = _settingsService.SaveAsync();

        // Stop work that belongs to the folder we are leaving. Old thumbnail requests and the
        // folder's background metadata enumerator must not compete with the new first page.
        CancelThumbnailLoading();
        ResetMobileThumbnailWindow();
        ResetDesktopThumbnailViewport();
        var previousMetadataSync = Interlocked.Exchange(ref _folderMetadataSyncCts, null);
        previousMetadataSync?.Cancel();

        // Folder navigation is superseding: a second navigation (especially Back) must cancel
        // the outstanding Graph request instead of waiting for it to finish.
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _folderNavigationCts, next);
        previous?.Cancel();

        Interlocked.Increment(ref _folderNavigationVersion);
        FolderNavigating?.Invoke(this, new FolderNavigationEventArgs(reason, FolderCacheKey(CurrentFolderId)));
        _nextNavigationReason = reason;
        return next;
    }

    private async Task LoadCurrentFolderAsync(bool forceRemote = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreCurrentFolderViewMode();
        RestoreCurrentFolderSortRule();
        var folderId = CurrentFolderId;
        var cacheKey = FolderCacheKey(folderId);
        var orderBy = GetGraphOrderBy();
        var navigationVersion = _folderNavigationVersion;
        var reason = _nextNavigationReason;
        _nextNavigationReason = FolderNavigationReason.Refresh;

        if (!forceRemote &&
            _folderCache.TryGetValue(cacheKey, out var cached) &&
            string.Equals(cached.OrderBy, orderBy, StringComparison.Ordinal))
        {
            var now = DateTimeOffset.UtcNow;
            cached.LastAccessUtc = now;
            _nextChildrenLink = cached.NextLink;
            HasMoreItems = !string.IsNullOrWhiteSpace(_nextChildrenLink);
            SetCurrentFolderTotalItemCount(cached.TotalItemCount);
            ApplyFolderItems(cached.Items, cached.TotalItemCount);
            FolderLoaded?.Invoke(this, new FolderNavigationEventArgs(reason, cacheKey));

            // Back/forward navigation is instant from memory. A cache entry may represent a
            // first-visit enumeration that was cancelled when the user navigated away. In that
            // case its continuation link must resume immediately; otherwise the stable tail would
            // remain placeholders until an explicit refresh. Complete entries only revalidate when
            // stale. Scrolling itself never owns pagination.
            if (!string.IsNullOrWhiteSpace(cached.NextLink))
            {
                StartFolderMetadataSync(
                    folderId,
                    cacheKey,
                    navigationVersion,
                    orderBy,
                    seedItems: cached.Items,
                    nextLink: cached.NextLink,
                    streamIntoPlaceholders: true);
            }
            else if (now - cached.LastValidatedUtc >= FolderCacheValidationInterval)
            {
                StartFolderMetadataSync(
                    folderId,
                    cacheKey,
                    navigationVersion,
                    orderBy,
                    seedItems: null,
                    nextLink: null,
                    streamIntoPlaceholders: false);
            }
            return;
        }

        // Persistent local index is the normal fast path after the first visit / first delta scan.
        // It gives the UI a complete logical folder immediately, before any network request.
        LocalFolderIndexSnapshot? localSnapshot = null;
        if (!forceRemote && !string.IsNullOrWhiteSpace(CurrentAccountId))
        {
            localSnapshot = await _localDriveIndex
                .GetFolderAsync(CurrentAccountId, folderId, orderBy, cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (navigationVersion != _folderNavigationVersion || FolderCacheKey(CurrentFolderId) != cacheKey)
            {
                if (localSnapshot is not null)
                    DisposeItemThumbnails(localSnapshot.Items);
                return;
            }
        }

        if (localSnapshot is not null)
        {
            _nextChildrenLink = null;
            HasMoreItems = false;

            // The item that was tapped (or the parent metadata index) may know a larger childCount
            // than this folder has locally materialized. Never shrink that known logical extent just
            // because the child itself has not been enumerated yet. The unloaded tail remains stable
            // lightweight slots and is filled by the background metadata stream below.
            var localTotalCount = Math.Max(localSnapshot.TotalCount, _currentFolderTotalItemCount ?? 0);
            SetCurrentFolderTotalItemCount(localTotalCount);
            StoreFolderCache(cacheKey, localSnapshot.Items, null, localTotalCount);
            if (_folderCache.TryGetValue(cacheKey, out var indexedEntry))
            {
                indexedEntry.LastValidatedUtc = localSnapshot.LastSyncedUtc ?? DateTimeOffset.MinValue;
                if (string.IsNullOrWhiteSpace(orderBy) && !localSnapshot.HasServerDefaultOrder)
                    indexedEntry.LastValidatedUtc = DateTimeOffset.MinValue;
            }
            ApplyFolderItems(localSnapshot.Items, localTotalCount);
            StatusText = $"{localTotalCount} 个项目";
            FolderLoaded?.Invoke(this, new FolderNavigationEventArgs(reason, cacheKey));

            var stale = localSnapshot.LastSyncedUtc is null ||
                        DateTimeOffset.UtcNow - localSnapshot.LastSyncedUtc.Value >= FolderCacheValidationInterval ||
                        (string.IsNullOrWhiteSpace(orderBy) && !localSnapshot.HasServerDefaultOrder);

            // A locally known-but-not-enumerated folder (normally learned from its parent's
            // childCount) must stream page one into its existing slots. A complete cached folder can
            // revalidate off-screen and swap only if metadata actually changed.
            if (!localSnapshot.IsComplete || stale)
            {
                StartFolderMetadataSync(
                    folderId,
                    cacheKey,
                    navigationVersion,
                    orderBy,
                    seedItems: null,
                    nextLink: null,
                    streamIntoPlaceholders: !localSnapshot.IsComplete);
            }
            return;
        }

        // First-ever visit: only page one is on the critical path. folder.childCount (normally
        // inherited from the parent row) creates a stable mobile slot array immediately; all
        // remaining metadata pages continue in the background without any LoadMore UI. When the
        // count is unknown (commonly the root), request folder metadata in parallel with page one
        // so a fast count response can establish the final extent without adding startup latency.
        Task<int?>? totalCountTask = _currentFolderTotalItemCount is null
            ? TryGetFolderTotalItemCountAsync(folderId, cancellationToken)
            : null;
        var sizeSortFallback = false;
        DriveItemPage page;
        try
        {
            page = await _oneDrive.GetChildrenPageAsync(
                folderId,
                pageSize: CurrentFolderPageSize,
                cancellationToken: cancellationToken,
                orderBy: orderBy);
        }
        catch (GraphOrderByNotSupportedException) when (SortColumn == FileSortColumn.Size)
        {
            SortColumn = FileSortColumn.None;
            SortState = SortCycleState.Original;
            await PersistCurrentFolderSortRuleAsync();
            orderBy = null;
            sizeSortFallback = true;
            page = await _oneDrive.GetChildrenPageAsync(
                folderId,
                pageSize: CurrentFolderPageSize,
                cancellationToken: cancellationToken,
                orderBy: null);
        }

        if (navigationVersion != _folderNavigationVersion || FolderCacheKey(CurrentFolderId) != cacheKey)
        {
            DisposeItemThumbnails(page.Items);
            return;
        }

        _nextChildrenLink = page.NextLink;
        HasMoreItems = page.HasMore;
        var totalCount = _currentFolderTotalItemCount;
        if (totalCount is null && totalCountTask?.IsCompletedSuccessfully == true)
            totalCount = totalCountTask.Result;
        if (totalCount is null && !page.HasMore)
            totalCount = page.Items.Count;
        SetCurrentFolderTotalItemCount(totalCount);
        StoreFolderCache(cacheKey, page.Items, page.NextLink, totalCount);
        ApplyFolderItems(page.Items, totalCount);
        if (sizeSortFallback)
            StatusText = "当前账户后端不支持大小排序，已对当前文件夹改用系统默认顺序";
        FolderLoaded?.Invoke(this, new FolderNavigationEventArgs(reason, cacheKey));

        if (totalCount is null && page.HasMore)
        {
            if (totalCountTask is not null)
                _ = ApplyFolderTotalCountTaskAsync(totalCountTask, cacheKey, navigationVersion);
            else
                _ = RefreshFolderTotalCountInBackgroundAsync(folderId, cacheKey, navigationVersion);
        }

        if (page.HasMore)
        {
            StartFolderMetadataSync(
                folderId,
                cacheKey,
                navigationVersion,
                orderBy,
                seedItems: page.Items,
                nextLink: page.NextLink,
                streamIntoPlaceholders: true);
        }
        else if (!string.IsNullOrWhiteSpace(CurrentAccountId))
        {
            _ = _localDriveIndex.SaveFolderAsync(
                CurrentAccountId,
                folderId,
                _localDriveIndex.GetRootItemId(CurrentAccountId),
                orderBy,
                page.Items,
                page.Items.Count);
        }
    }

    public Task LoadMoreCurrentFolderAsync()
    {
        // Kept for API/backward compatibility. Paging is no longer coupled to ScrollChanged:
        // folder metadata is continuously enumerated by StartFolderMetadataSync after page one.
        return Task.CompletedTask;
    }

    private void StartFolderMetadataSync(
        string? folderId,
        string cacheKey,
        long navigationVersion,
        string? orderBy,
        IReadOnlyList<DriveItemModel>? seedItems,
        string? nextLink,
        bool streamIntoPlaceholders)
    {
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(CurrentAccountId))
            return;

        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _folderMetadataSyncCts, next);
        previous?.Cancel();

        var accountId = CurrentAccountId;
        _ = RunFolderMetadataSyncAsync(
            accountId,
            folderId,
            cacheKey,
            navigationVersion,
            orderBy,
            seedItems,
            nextLink,
            streamIntoPlaceholders,
            next);
    }

    private async Task WaitForMetadataPresentationWindowAsync(
        DateTime notBeforeUtc,
        CancellationToken cancellationToken)
    {
        // Reserve the first few hundred milliseconds after folder presentation for input/rendering
        // on both form factors. On desktop a scrollbar-thumb drag can jump thousands of logical
        // slots immediately; a 200-slot metadata burst landing in that same frame is just as visible
        // as it is during a phone fling.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scrolling = IsMobilePlatform ? _mobileListScrolling : _desktopListScrolling;
            var now = DateTime.UtcNow;
            if (!scrolling && now >= notBeforeUtc)
            {
                // Give the first post-idle frame to input/render/current-viewport thumbnail
                // recovery, then re-check in case a new gesture started meanwhile.
                await Task.Delay(IsMobilePlatform ? 70 : 45, cancellationToken).ConfigureAwait(false);
                scrolling = IsMobilePlatform ? _mobileListScrolling : _desktopListScrolling;
                if (!scrolling && DateTime.UtcNow >= notBeforeUtc)
                    return;
            }

            var remainingMs = Math.Max(0, (notBeforeUtc - now).TotalMilliseconds);
            var delayMs = (int)Math.Clamp(remainingMs > 0 ? Math.Min(remainingMs, 80) : 80, 24, 80);
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunFolderMetadataSyncAsync(
        string accountId,
        string? folderId,
        string cacheKey,
        long navigationVersion,
        string? orderBy,
        IReadOnlyList<DriveItemModel>? seedItems,
        string? nextLink,
        bool streamIntoPlaceholders,
        CancellationTokenSource request)
    {
        var token = request.Token;
        var collected = seedItems?.ToList() ?? [];
        var cursor = nextLink;
        // Page one is already on-screen for a normal first visit. Hold page-two-and-later UI
        // mutation briefly so a user can start scrolling immediately without competing with a
        // burst of slot updates. An incomplete local-only folder still gets page one as soon as
        // the user is not actively scrolling.
        var presentationNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(seedItems is null ? 120 : 360);
        try
        {
            if (seedItems is null)
            {
                DriveItemPage first;
                try
                {
                    first = await _oneDrive.GetChildrenPageAsync(
                        folderId,
                        pageSize: CurrentFolderPageSize,
                        cancellationToken: token,
                        orderBy: orderBy).ConfigureAwait(false);
                }
                catch (GraphOrderByNotSupportedException)
                {
                    // A cached size-sorted folder remains usable even if this particular backend
                    // temporarily refuses that server order. An explicit user refresh can choose
                    // a different sort; background synchronization itself should stay silent.
                    return;
                }

                collected.AddRange(first.Items);
                cursor = first.NextLink;

                // This path is used when the local index knows the folder/count but has not yet
                // enumerated its children. Fill page one into the already-created fixed slots as
                // soon as it arrives; otherwise slots 0..N would remain placeholders until the
                // complete enumeration finished.
                if (streamIntoPlaceholders)
                {
                    var firstApplied = false;
                    while (!firstApplied)
                    {
                        await WaitForMetadataPresentationWindowAsync(presentationNotBeforeUtc, token).ConfigureAwait(false);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (navigationVersion != _folderNavigationVersion ||
                                FolderCacheKey(CurrentFolderId) != cacheKey)
                            {
                                firstApplied = true;
                                return;
                            }

                            if ((IsMobilePlatform && _mobileListScrolling) ||
                                (!IsMobilePlatform && _desktopListScrolling))
                                return;

                            AppendBackgroundMetadataPage(0, first.Items, first.NextLink);
                            firstApplied = true;
                        }, DispatcherPriority.Background);
                    }
                }
            }

            while (!string.IsNullOrWhiteSpace(cursor))
            {
                token.ThrowIfCancellationRequested();
                var offset = collected.Count;
                var page = await _oneDrive.GetChildrenPageAsync(
                    folderId,
                    cursor,
                    CurrentFolderPageSize,
                    token,
                    orderBy: null).ConfigureAwait(false);
                collected.AddRange(page.Items);
                cursor = page.NextLink;

                if (streamIntoPlaceholders)
                {
                    var applied = false;
                    while (!applied)
                    {
                        await WaitForMetadataPresentationWindowAsync(presentationNotBeforeUtc, token).ConfigureAwait(false);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (navigationVersion != _folderNavigationVersion ||
                                FolderCacheKey(CurrentFolderId) != cacheKey)
                            {
                                applied = true; // Navigation changed; stop retrying this old page.
                                return;
                            }

                            if ((IsMobilePlatform && _mobileListScrolling) ||
                                (!IsMobilePlatform && _desktopListScrolling))
                                return;

                            AppendBackgroundMetadataPage(offset, page.Items, page.NextLink);
                            applied = true;
                        }, DispatcherPriority.Background);
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            var finalCount = collected.Count;

            // JSON index persistence and completed-folder reconciliation are cache maintenance,
            // not part of an active gesture. Keep that CPU/disk work out of both a phone fling and
            // a desktop scrollbar-thumb drag.
            while ((IsMobilePlatform && _mobileListScrolling) ||
                   (!IsMobilePlatform && _desktopListScrolling))
            {
                await Task.Delay(80, token).ConfigureAwait(false);
            }

            await _localDriveIndex.SaveFolderAsync(
                accountId,
                folderId,
                _localDriveIndex.GetRootItemId(accountId),
                orderBy,
                collected,
                finalCount,
                token).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (navigationVersion != _folderNavigationVersion ||
                    FolderCacheKey(CurrentFolderId) != cacheKey)
                    return;

                _nextChildrenLink = null;
                HasMoreItems = false;
                SetCurrentFolderTotalItemCount(finalCount);

                if (streamIntoPlaceholders)
                {
                    ReconcileMobileSlotCount(finalCount);
                    if (_folderCache.TryGetValue(cacheKey, out var entry))
                    {
                        entry.NextLink = null;
                        entry.TotalItemCount = finalCount;
                        entry.LastAccessUtc = DateTimeOffset.UtcNow;
                        entry.LastValidatedUtc = DateTimeOffset.UtcNow;
                    }
                    StatusText = $"{finalCount} 个项目";
                    ScheduleStartupSnapshotSave();
                }
                else
                {
                    // If the server says nothing visible changed, retain the exact same model/slot
                    // instances. That avoids a needless collection reset after every cache validation.
                    if (FolderItemsEquivalent(_allItems, collected))
                    {
                        if (_folderCache.TryGetValue(cacheKey, out var unchangedEntry))
                        {
                            unchangedEntry.NextLink = null;
                            unchangedEntry.TotalItemCount = finalCount;
                            unchangedEntry.LastAccessUtc = DateTimeOffset.UtcNow;
                            unchangedEntry.LastValidatedUtc = DateTimeOffset.UtcNow;
                        }
                        DisposeItemThumbnails(collected);
                        StatusText = $"{finalCount} 个项目";
                        ScheduleStartupSnapshotSave();
                        return;
                    }

                    // Do not reshuffle items under a user's active long-press selection. The new
                    // metadata is already durable in LocalDriveIndex and will be shown on the next
                    // navigation/refresh; preserving selection is more important than a live reorder.
                    if (SelectionCount > 0)
                    {
                        DisposeItemThumbnails(collected);
                        StatusText = $"{finalCount} 个项目";
                        return;
                    }

                    StoreFolderCache(cacheKey, collected, null, finalCount);
                    ApplyFolderItems(collected, finalCount);
                    StatusText = $"{finalCount} 个项目";
                }
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Navigation superseded this folder's background enumeration.
        }
        catch
        {
            // Local metadata is already usable. Background cloud synchronization must never turn
            // an offline / temporarily throttled folder into an error surface.
        }
        finally
        {
            if (ReferenceEquals(_folderMetadataSyncCts, request))
                Interlocked.CompareExchange(ref _folderMetadataSyncCts, null, request);
            request.Dispose();
        }
    }

    private void AppendBackgroundMetadataPage(int offset, IReadOnlyList<DriveItemModel> pageItems, string? nextLink)
    {
        _nextChildrenLink = nextLink;
        HasMoreItems = !string.IsNullOrWhiteSpace(nextLink);

        if (_folderCache.TryGetValue(FolderCacheKey(CurrentFolderId), out var entry))
        {
            entry.Items.AddRange(pageItems);
            entry.NextLink = nextLink;
            entry.LastAccessUtc = DateTimeOffset.UtcNow;
        }

        _allItems.AddRange(pageItems);
        foreach (var pageItem in pageItems)
        {
            if (!string.IsNullOrWhiteSpace(pageItem.Id))
                _currentItemIds.Add(pageItem.Id);
            pageItem.IsMobileSelectionMode = MobileSelectionModeActive;
        }

        if (IsMobilePlatform && !string.IsNullOrWhiteSpace(SearchText))
        {
            ApplyFilterAndSort();
        }
        else
        {
            AppendLoadedPageToVisibleItems(pageItems);
            FillMobileSlots(offset, pageItems);
        }

    }

    private async Task RefreshFolderInBackgroundAsync(string? folderId, string cacheKey, long navigationVersion)
    {
        try
        {
            var remotePage = await _oneDrive.GetChildrenPageAsync(folderId, pageSize: CurrentFolderPageSize, orderBy: GetGraphOrderBy());
            if (navigationVersion != _folderNavigationVersion || FolderCacheKey(CurrentFolderId) != cacheKey)
            {
                DisposeItemThumbnails(remotePage.Items);
                return;
            }

            if (_folderCache.TryGetValue(cacheKey, out var existing) && FolderItemsEquivalent(existing.Items, remotePage.Items) && !remotePage.HasMore)
            {
                var now = DateTimeOffset.UtcNow;
                existing.LastAccessUtc = now;
                existing.LastValidatedUtc = now;
                DisposeItemThumbnails(remotePage.Items);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (navigationVersion != _folderNavigationVersion || FolderCacheKey(CurrentFolderId) != cacheKey)
                {
                    DisposeItemThumbnails(remotePage.Items);
                    return;
                }

                _nextChildrenLink = remotePage.NextLink;
                HasMoreItems = remotePage.HasMore;
                StoreFolderCache(cacheKey, remotePage.Items, remotePage.NextLink, _currentFolderTotalItemCount);
                ApplyFolderItems(remotePage.Items);
            });
        }
        catch
        {
            // Cached navigation remains usable when a background refresh fails.
        }
    }

    private async Task ApplyFolderTotalCountTaskAsync(
        Task<int?> totalCountTask,
        string cacheKey,
        long navigationVersion)
    {
        try
        {
            var total = await totalCountTask.ConfigureAwait(false);
            if (total is null)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (navigationVersion != _folderNavigationVersion || FolderCacheKey(CurrentFolderId) != cacheKey)
                    return;

                SetCurrentFolderTotalItemCount(total);
                if (string.IsNullOrWhiteSpace(SearchText))
                    ReconcileMobileSlotCount(Math.Max(total.Value, _allItems.Count));
                if (_folderCache.TryGetValue(cacheKey, out var entry))
                    entry.TotalItemCount = total;
                ScheduleStartupSnapshotSave();
            });
        }
        catch (OperationCanceledException)
        {
            // Navigation superseded the parallel count request.
        }
        catch
        {
            // Count metadata is optional; background enumeration still discovers the final count.
        }
    }

    private async Task RefreshFolderTotalCountInBackgroundAsync(
        string? folderId,
        string cacheKey,
        long navigationVersion)
    {
        try
        {
            var total = await TryGetFolderTotalItemCountAsync(folderId).ConfigureAwait(false);
            if (total is null)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (navigationVersion != _folderNavigationVersion || FolderCacheKey(CurrentFolderId) != cacheKey)
                    return;

                SetCurrentFolderTotalItemCount(total);
                if (string.IsNullOrWhiteSpace(SearchText))
                    ReconcileMobileSlotCount(Math.Max(total.Value, _allItems.Count));
                if (_folderCache.TryGetValue(cacheKey, out var entry))
                    entry.TotalItemCount = total;
                ScheduleStartupSnapshotSave();
            });
        }
        catch
        {
            // Item count is supplementary metadata and must never hold up folder navigation.
        }
    }

    private async Task<int?> TryGetFolderTotalItemCountAsync(
        string? folderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = await _oneDrive.GetItemMetadataAsync(folderId, cancellationToken).ConfigureAwait(false);
            return metadata.IsFolder ? metadata.ChildCount : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The listing itself can still load even if the lightweight metadata request fails.
            return null;
        }
    }

    private void ApplyFolderItems(IReadOnlyList<DriveItemModel> items, int? totalItemCount = null)
    {
        CancelThumbnailLoading();
        ResetMobileThumbnailWindow();
        ResetDesktopThumbnailViewport();
        Items.Clear();
        ClearMobileSlots();
        _allItems.Clear();
        _currentItemIds.Clear();
        _allItems.AddRange(items);
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
                _currentItemIds.Add(item.Id);
            item.IsMobileSelectionMode = MobileSelectionModeActive;
        }

        if (totalItemCount is not null)
            SetCurrentFolderTotalItemCount(totalItemCount);
        ApplyFilterAndSort();
        CurrentLocation = string.Join(" / ", Breadcrumbs.Select(x => x.Name));
        StatusText = $"{(_currentFolderTotalItemCount ?? _allItems.Count)} 个项目";
        SetSelectedItems([]);
        if (RememberLastFolder)
        {
            CaptureCurrentFolderMemory();
            _ = _settingsService.SaveAsync();
        }

        if (IsAuthenticated && !string.IsNullOrWhiteSpace(CurrentAccountId))
            ScheduleStartupSnapshotSave();
    }

    private void ClearMobileSlots()
    {
        foreach (var slot in MobileItems)
            slot.Dispose();
        MobileItems.Clear();
    }

    private void RebuildMobileSlots(int totalCount, IReadOnlyList<DriveItemModel> loadedItems)
    {
        totalCount = Math.Max(totalCount, loadedItems.Count);
        var slots = new VirtualDriveItemSlot[totalCount];
        for (var i = 0; i < totalCount; i++)
            slots[i] = new VirtualDriveItemSlot(i, i < loadedItems.Count ? loadedItems[i] : null);

        ClearMobileSlots();
        MobileItems.AddRange(slots);
    }

    private void FillMobileSlots(int startIndex, IReadOnlyList<DriveItemModel> pageItems)
    {
        if (pageItems.Count == 0)
            return;

        var required = startIndex + pageItems.Count;
        if (required > MobileItems.Count)
        {
            var newSlots = new List<VirtualDriveItemSlot>(required - MobileItems.Count);
            for (var i = MobileItems.Count; i < required; i++)
                newSlots.Add(new VirtualDriveItemSlot(i));
            MobileItems.AddRange(newSlots);
        }

        var viewportCandidates = new List<DriveItemModel>();
        var intersectsThumbnailWindow = IsMobilePlatform && !UsesNativeMobileFileList &&
                                        startIndex < _mobileThumbnailWindowToExclusive &&
                                        startIndex + pageItems.Count > _mobileThumbnailWindowFrom;
        var desktopVisibleSlots = _desktopThumbnailVisibleSlotIndices;
        var desktopVisibleIds = !IsMobilePlatform
            ? new HashSet<string>(_desktopThumbnailWantedIds, StringComparer.Ordinal)
            : null;
        for (var i = 0; i < pageItems.Count; i++)
        {
            var index = startIndex + i;
            var item = pageItems[i];
            MobileItems[index].SetItem(item);

            if (IsMobilePlatform && !UsesNativeMobileFileList &&
                index >= _mobileThumbnailVisibleFrom &&
                index < _mobileThumbnailVisibleToExclusive &&
                item.SupportsThumbnail && !item.HasThumbnailImage &&
                (!_mobileListScrolling || _thumbnailCache.TryGetCachedPath(item, out _)))
            {
                viewportCandidates.Add(item);
            }
            else if (!IsMobilePlatform &&
                     desktopVisibleSlots.Contains(index) &&
                     item.SupportsThumbnail && !item.HasThumbnailImage &&
                     (!_desktopListScrolling || _thumbnailCache.TryGetCachedPath(item, out _)))
            {
                if (!string.IsNullOrWhiteSpace(item.Id))
                    desktopVisibleIds!.Add(item.Id);
                viewportCandidates.Add(item);
            }
        }

        // Native Android/iOS lists use fixed VirtualDriveItemSlot instances. Hydrating an
        // existing slot does not raise MobileItems.CollectionChanged, so publish one lightweight
        // page-level signal after SetItem calls. Native adapters use it to retry the current +/-1
        // viewport thumbnail window when metadata arrives after their first layout pass.
        if (UsesNativeMobileFileList)
            OnPropertyChanged(nameof(MobileItems));

        if (intersectsThumbnailWindow)
            RefreshMobileThumbnailWantedIds();

        if (!IsMobilePlatform && desktopVisibleIds is not null)
            _desktopThumbnailWantedIds = desktopVisibleIds;

        if (viewportCandidates.Count > 0)
        {
            StartThumbnailLoading(
                viewportCandidates,
                requireVisibleOnMobile: IsMobilePlatform,
                requireVisibleOnDesktop: !IsMobilePlatform);
        }
    }

    private void ReconcileMobileSlotCount(int finalCount)
    {
        finalCount = Math.Max(0, finalCount);
        if (MobileItems.Count < finalCount)
        {
            var newSlots = new List<VirtualDriveItemSlot>(finalCount - MobileItems.Count);
            for (var i = MobileItems.Count; i < finalCount; i++)
                newSlots.Add(new VirtualDriveItemSlot(i));
            MobileItems.AddRange(newSlots);
        }
        else
        {
            while (MobileItems.Count > finalCount)
            {
                var last = MobileItems[^1];
                last.Dispose();
                MobileItems.RemoveAt(MobileItems.Count - 1);
            }
        }
    }

    private void StoreFolderCache(string cacheKey, IReadOnlyList<DriveItemModel> items, string? nextLink = null, int? totalItemCount = null)
    {
        if (_folderCache.TryGetValue(cacheKey, out var previous) && !ReferenceEquals(previous.Items, items))
            DisposeItemThumbnails(previous.Items);

        _folderCache[cacheKey] = new FolderCacheEntry(
            items.ToList(),
            nextLink,
            totalItemCount,
            DateTimeOffset.UtcNow,
            GetGraphOrderBy());
        TrimFolderCache(cacheKey);
    }

    private bool HasFolderCache(string? folderId) =>
        _folderCache.ContainsKey(FolderCacheKey(folderId)) ||
        (!string.IsNullOrWhiteSpace(CurrentAccountId) && _localDriveIndex.HasFolder(CurrentAccountId, folderId));

    private void InvalidateCurrentFolderCache() => InvalidateFolderCache(CurrentFolderId);

    private void InvalidateFolderCache(string? folderId)
    {
        var key = FolderCacheKey(folderId);
        if (_folderCache.Remove(key, out var entry))
            DisposeItemThumbnails(entry.Items);
    }

    private void ClearFolderCache()
    {
        foreach (var entry in _folderCache.Values)
            DisposeItemThumbnails(entry.Items);
        _folderCache.Clear();
        _allItems.Clear();
        Items.Clear();
        ClearMobileSlots();
        _currentItemIds.Clear();
        _nextChildrenLink = null;
        HasMoreItems = false;
        SetCurrentFolderTotalItemCount(null);
    }

    private void TrimFolderCache(string keepKey)
    {
        const int maxFolders = 16;
        if (_folderCache.Count <= maxFolders)
            return;

        foreach (var pair in _folderCache
                     .Where(x => !string.Equals(x.Key, keepKey, StringComparison.Ordinal))
                     .OrderBy(x => x.Value.LastAccessUtc)
                     .Take(_folderCache.Count - maxFolders)
                     .ToArray())
        {
            if (_folderCache.Remove(pair.Key, out var removed))
                DisposeItemThumbnails(removed.Items);
        }
    }

    private static bool FolderItemsEquivalent(IReadOnlyList<DriveItemModel> left, IReadOnlyList<DriveItemModel> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal) ||
                !string.Equals(a.Name, b.Name, StringComparison.Ordinal) ||
                !string.Equals(a.VersionToken, b.VersionToken, StringComparison.Ordinal) ||
                a.Size != b.Size ||
                a.ChildCount != b.ChildCount)
            {
                return false;
            }
        }

        return true;
    }

    private static string FolderCacheKey(string? folderId) => folderId ?? "__ROOT__";

    private bool IsDesktopThumbnailWanted(DriveItemModel item)
    {
        if (IsMobilePlatform || string.IsNullOrWhiteSpace(item.Id))
            return true;

        return _desktopThumbnailWantedIds.Contains(item.Id);
    }

    private bool IsMobileThumbnailWanted(DriveItemModel item)
    {
        if (!IsMobilePlatform || _mobileThumbnailWindowFrom < 0 || _mobileThumbnailWindowToExclusive <= _mobileThumbnailWindowFrom)
            return true;

        return _mobileThumbnailWantedIds.Contains(item.Id);
    }

    private bool IsMobileThumbnailVisible(DriveItemModel item)
    {
        if (!IsMobilePlatform)
            return true;

        var visible = _mobileThumbnailVisibleIds;
        if (visible.Count > 0)
            return visible.Contains(item.Id);

        if (_mobileThumbnailVisibleFrom < 0 || _mobileThumbnailVisibleToExclusive <= _mobileThumbnailVisibleFrom)
            return true;

        return false;
    }

    private void RefreshMobileThumbnailWantedIds()
    {
        if (!IsMobilePlatform || _mobileThumbnailWindowFrom < 0 || _mobileThumbnailWindowToExclusive <= _mobileThumbnailWindowFrom)
        {
            _mobileThumbnailWantedIds = new HashSet<string>(StringComparer.Ordinal);
            _mobileThumbnailVisibleIds = new HashSet<string>(StringComparer.Ordinal);
            return;
        }

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var visible = new HashSet<string>(StringComparer.Ordinal);
        var from = Math.Max(0, _mobileThumbnailWindowFrom);
        var toExclusive = Math.Min(MobileItems.Count, _mobileThumbnailWindowToExclusive);
        var visibleFrom = Math.Max(from, _mobileThumbnailVisibleFrom);
        var visibleToExclusive = Math.Min(toExclusive, _mobileThumbnailVisibleToExclusive);

        for (var i = from; i < toExclusive; i++)
        {
            var item = MobileItems[i].Item;
            if (item is null || string.IsNullOrWhiteSpace(item.Id))
                continue;

            wanted.Add(item.Id);
            if (i >= visibleFrom && i < visibleToExclusive)
                visible.Add(item.Id);
        }

        _mobileThumbnailWantedIds = wanted;
        _mobileThumbnailVisibleIds = visible;
    }

    private bool TryRegisterThumbnailLoad(string itemId, long generation)
    {
        while (true)
        {
            if (_thumbnailLoadsInFlight.TryAdd(itemId, generation))
                return true;

            if (!_thumbnailLoadsInFlight.TryGetValue(itemId, out var existingGeneration))
                continue;

            if (existingGeneration == generation)
                return false;

            // A cancelled generation must never block the final viewport. Replace the stale
            // marker immediately; the old worker's finally block only removes its own generation.
            if (_thumbnailLoadsInFlight.TryUpdate(itemId, generation, existingGeneration))
                return true;
        }
    }

    private void StartThumbnailLoading(
        IReadOnlyList<DriveItemModel> items,
        bool requireVisibleOnMobile = false,
        bool requireVisibleOnDesktop = false)
    {
        // Android's file surface is a native RecyclerView. Its adapter owns thumbnail loading
        // and decodes directly into Android bitmaps, so never create Avalonia Bitmap work for it.
        if (UsesNativeMobileFileList && requireVisibleOnMobile)
            return;

        var generation = Volatile.Read(ref _thumbnailLoadGeneration);
        var mediaItems = items
            .Where(x => x.SupportsThumbnail && !x.HasThumbnailImage && TryRegisterThumbnailLoad(x.Id, generation))
            .ToArray();
        if (mediaItems.Length == 0)
            return;

        var cts = _thumbnailLoadCts;
        if (cts is null || cts.IsCancellationRequested)
        {
            cts = new CancellationTokenSource();
            _thumbnailLoadCts = cts;
        }
        _ = LoadThumbnailsInBackgroundAsync(
            mediaItems,
            requireVisibleOnMobile,
            requireVisibleOnDesktop,
            generation,
            cts.Token);
    }

    private async Task LoadThumbnailsInBackgroundAsync(
        IReadOnlyList<DriveItemModel> items,
        bool requireVisibleOnMobile,
        bool requireVisibleOnDesktop,
        long generation,
        CancellationToken cancellationToken)
    {
        // Capture the batch mode once. All batches share the same worker gates below, so repeated
        // 90ms viewport updates cannot multiply the actual decode/download concurrency.
        var serializeMobileBatch = IsMobilePlatform && _mobileListScrolling;
        var tasks = items.Select(item =>
            LoadThumbnailForItemAsync(
                item,
                serializeMobileBatch,
                requireVisibleOnMobile,
                requireVisibleOnDesktop,
                generation,
                cancellationToken));

        try
        {
            await Task.WhenAll(tasks);
            if (!cancellationToken.IsCancellationRequested)
                Dispatcher.UIThread.Post(UpdateCacheStatus);
        }
        catch (OperationCanceledException)
        {
            // Navigation/refresh cancels thumbnail work from the previous folder.
        }
        catch
        {
            // A thumbnail is cosmetic; a failure must never make the folder unusable.
        }
    }

    private async Task LoadThumbnailForItemAsync(
        DriveItemModel item,
        bool serializeMobileBatch,
        bool requireVisibleOnMobile,
        bool requireVisibleOnDesktop,
        long generation,
        CancellationToken cancellationToken)
    {
        var workGate = IsMobilePlatform ? _mobileThumbnailWorkGate : _desktopThumbnailWorkGate;
        var flingGate = serializeMobileBatch ? _mobileFlingThumbnailGate : null;
        var workGateAcquired = false;
        var flingGateAcquired = false;
        try
        {
            if (IsMobilePlatform &&
                (requireVisibleOnMobile ? !IsMobileThumbnailVisible(item) : !IsMobileThumbnailWanted(item)))
                return;
            if (!IsMobilePlatform && requireVisibleOnDesktop && !IsDesktopThumbnailWanted(item))
                return;
            if (flingGate is not null)
            {
                await flingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                flingGateAcquired = true;
            }

            await workGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            workGateAcquired = true;

            // A long fling can enqueue several old disk-cache decodes. Re-check after the shared
            // gate so the final resting viewport is never stuck behind stale off-screen work.
            if (IsMobilePlatform &&
                (requireVisibleOnMobile ? !IsMobileThumbnailVisible(item) : !IsMobileThumbnailWanted(item)))
                return;
            if (!IsMobilePlatform && requireVisibleOnDesktop && !IsDesktopThumbnailWanted(item))
                return;

            var hadDiskCache = _thumbnailCache.TryGetCachedPath(item, out var cachedPath);
            if (!hadDiskCache)
            {
                cachedPath = await _thumbnailCache
                    .GetOrDownloadAsync(item, _oneDrive, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(cachedPath) || cancellationToken.IsCancellationRequested)
                return;

            Bitmap? bitmap = null;
            try
            {
                bitmap = await DecodeThumbnailBitmapAsync(
                    cachedPath,
                    IsMobilePlatform ? 160 : 320,
                    cancellationToken).ConfigureAwait(false);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // A partially written/corrupt cache entry should heal itself once. This path is
                // intentionally rare; the normal cached path never performs a network request.
                _thumbnailCache.Invalidate(item.Id);
                cachedPath = await _thumbnailCache
                    .GetOrDownloadAsync(item, _oneDrive, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(cachedPath))
                    return;

                bitmap = await DecodeThumbnailBitmapAsync(
                    cachedPath,
                    IsMobilePlatform ? 160 : 320,
                    cancellationToken).ConfigureAwait(false);
            }

            if (bitmap is null)
                return;

            if (cancellationToken.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Use the O(1) id set rather than List.Contains() on every thumbnail completion.
                // In thousand-item folders the old linear check became measurable UI-thread work.
                if (cancellationToken.IsCancellationRequested || !_currentItemIds.Contains(item.Id) ||
                    (IsMobilePlatform && (requireVisibleOnMobile
                        ? !IsMobileThumbnailVisible(item)
                        : !IsMobileThumbnailWanted(item))) ||
                    (!IsMobilePlatform && requireVisibleOnDesktop && !IsDesktopThumbnailWanted(item)))
                {
                    bitmap?.Dispose();
                    bitmap = null;
                    return;
                }

                item.ThumbnailImage?.Dispose();
                item.ThumbnailImage = bitmap;
                TouchMobileThumbnail(item);
                bitmap = null;
            });

            bitmap?.Dispose();
        }
        catch (OperationCanceledException)
        {
            // Expected during navigation/refresh.
        }
        catch
        {
            // Keep the normal file icon when the service cannot generate/download a thumbnail.
        }
        finally
        {
            if (_thumbnailLoadsInFlight.TryGetValue(item.Id, out var activeGeneration) &&
                activeGeneration == generation)
            {
                _thumbnailLoadsInFlight.TryRemove(item.Id, out _);
            }
            if (workGateAcquired)
                workGate.Release();
            if (flingGateAcquired)
                flingGate!.Release();
        }
    }

    private static Task<Bitmap> DecodeThumbnailBitmapAsync(
        string cachedPath,
        int decodeWidth,
        CancellationToken cancellationToken)
    {
        // Decode is CPU work. Keeping it off Avalonia's UI dispatcher is important during a
        // fling; only the final immutable Bitmap property swap is posted back to the UI thread.
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(
                cachedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            var bitmap = Bitmap.DecodeToWidth(
                stream,
                decodeWidth,
                BitmapInterpolationMode.MediumQuality);
            if (cancellationToken.IsCancellationRequested)
            {
                bitmap.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return bitmap;
        }, cancellationToken);
    }

    private void TouchMobileThumbnail(DriveItemModel item)
    {
        if (!IsMobilePlatform || item.ThumbnailImage is null || string.IsNullOrWhiteSpace(item.Id))
            return;

        if (_mobileThumbnailLruNodes.Remove(item.Id, out var existing))
        {
            _mobileThumbnailLru.Remove(existing);
            if (!ReferenceEquals(existing.Value, item))
            {
                existing.Value.ThumbnailImage?.Dispose();
                existing.Value.ThumbnailImage = null;
            }
        }

        var node = _mobileThumbnailLru.AddLast(item);
        _mobileThumbnailLruNodes[item.Id] = node;

        while (_mobileThumbnailLru.Count > MobileDecodedThumbnailCacheLimit)
        {
            var oldest = _mobileThumbnailLru.First;
            if (oldest is null)
                break;

            _mobileThumbnailLru.RemoveFirst();
            var oldItem = oldest.Value;
            _mobileThumbnailLruNodes.Remove(oldItem.Id);

            // The just-touched/visible window is always moved to the LRU tail before trimming,
            // so the evicted entries are normally far behind the current viewport.
            oldItem.ThumbnailImage?.Dispose();
            oldItem.ThumbnailImage = null;
        }
    }

    private void CancelThumbnailLoading()
    {
        // Move to a fresh logical generation before cancelling. StartThumbnailLoading can then
        // replace stale in-flight markers immediately instead of waiting for cancelled workers
        // to unwind their network/decode awaits.
        Interlocked.Increment(ref _thumbnailLoadGeneration);
        var cts = _thumbnailLoadCts;
        _thumbnailLoadCts = null;
        if (cts is null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    private static void DisposeItemThumbnails(IEnumerable<DriveItemModel> items)
    {
        foreach (var item in items)
            item.Dispose();
    }

    private void AppendLoadedPageToVisibleItems(IReadOnlyList<DriveItemModel> pageItems)
    {
        // Every visible file surface renders the stable slot collection directly. Items remains a
        // non-visual compatibility shadow on desktop only; updating it can no longer change scroll
        // extent or force the realized ItemsRepeater elements to be recreated.
        if (IsMobilePlatform)
            return;

        // The default/original view preserves Graph order. Rebuilding Items with Clear()+Add()
        // every time a page arrives forces a large virtualized control to throw away realized
        // elements and is very noticeable during a touch fling. Append only the new tail instead.
        if ((SortState == SortCycleState.Original && SortColumn == FileSortColumn.None) || UsesGraphOrdering)
        {
            var keyword = SearchText.Trim();
            var visiblePage = string.IsNullOrWhiteSpace(keyword)
                ? pageItems
                : pageItems.Where(item => item.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)).ToArray();

            // AvaloniaList.AddRange emits one range update instead of dozens/hundreds of
            // ObservableCollection Add notifications. This matters when a large Graph page is
            // appended while the user is still flinging through a thousand-item folder.
            Items.AddRange(visiblePage);
            return;
        }

        ApplyFilterAndSort();
    }

    public void UpdateDesktopRealizedThumbnails(
        IReadOnlyList<int> visibleSlotIndices,
        IReadOnlyList<DriveItemModel> visibleItems,
        bool allowNetwork)
    {
        if (IsMobilePlatform)
            return;

        _desktopThumbnailVisibleSlotIndices = new HashSet<int>(visibleSlotIndices);

        var exactIds = new HashSet<string>(StringComparer.Ordinal);
        var exactItems = new List<DriveItemModel>(visibleItems.Count);
        foreach (var item in visibleItems)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || !exactIds.Add(item.Id))
                continue;
            exactItems.Add(item);
        }

        _desktopThumbnailWantedIds = exactIds;
        if (exactItems.Count == 0)
            return;

        var candidates = exactItems
            .Where(item => item.SupportsThumbnail && !item.HasThumbnailImage &&
                           (allowNetwork || _thumbnailCache.TryGetCachedPath(item, out _)))
            .ToArray();
        if (candidates.Length > 0)
        {
            StartThumbnailLoading(
                candidates,
                requireVisibleOnDesktop: true);
        }
    }

    public void UpdateMobileRealizedThumbnails(IReadOnlyList<DriveItemModel> visibleItems)
    {
        if (!IsMobilePlatform || UsesNativeMobileFileList || visibleItems.Count == 0)
            return;

        // Offset/row-pitch math is only an estimate for UniformGridLayout because MinItemHeight is
        // a minimum, not a promise of the final arranged row height. After a long fling even a tiny
        // per-row difference can point the thumbnail scheduler at the wrong slots. At rest the view
        // supplies the actually realized controls, making this set authoritative for network work.
        var exactVisible = new HashSet<string>(StringComparer.Ordinal);
        var exactItems = new List<DriveItemModel>(visibleItems.Count);
        foreach (var item in visibleItems)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || !exactVisible.Add(item.Id))
                continue;

            exactItems.Add(item);
            if (item.ThumbnailImage is not null)
                TouchMobileThumbnail(item);
        }

        if (exactItems.Count == 0)
            return;

        _mobileThumbnailVisibleIds = exactVisible;

        // Keep current realized items valid even if the approximate buffered index window drifted.
        var wanted = new HashSet<string>(_mobileThumbnailWantedIds, StringComparer.Ordinal);
        wanted.UnionWith(exactVisible);
        _mobileThumbnailWantedIds = wanted;

        var candidates = exactItems
            .Where(item => item.SupportsThumbnail && !item.HasThumbnailImage &&
                           (!_mobileListScrolling || _thumbnailCache.TryGetCachedPath(item, out _)))
            .ToArray();
        if (candidates.Length > 0)
            StartThumbnailLoading(candidates, requireVisibleOnMobile: true);
    }

    public void UpdateMobileThumbnailWindow(int startIndex, int visibleCount, bool forceRescan = false)
    {
        if (!IsMobilePlatform || UsesNativeMobileFileList || MobileItems.Count == 0)
            return;

        var buffer = Math.Max(16, visibleCount);
        var from = Math.Max(0, startIndex - buffer);
        var visibleFrom = Math.Max(from, startIndex);
        var visibleToExclusive = Math.Min(MobileItems.Count, startIndex + visibleCount);
        var toExclusive = Math.Min(MobileItems.Count, startIndex + visibleCount + buffer);
        var scrolling = _mobileListScrolling;

        var previousFrom = _mobileThumbnailWindowFrom;
        var previousToExclusive = _mobileThumbnailWindowToExclusive;
        var previousScrolling = _mobileThumbnailWindowWasScrolling;

        // ScrollChanged may fire once per rendered frame while the estimated item window only
        // changes every row. A forced idle retry intentionally bypasses this guard so a metadata
        // page that arrived just after the previous scan can still start its thumbnail work.
        if (!forceRescan &&
            from == previousFrom &&
            toExclusive == previousToExclusive &&
            scrolling == previousScrolling)
        {
            return;
        }

        _mobileThumbnailWindowFrom = from;
        _mobileThumbnailWindowToExclusive = toExclusive;
        _mobileThumbnailVisibleFrom = visibleFrom;
        _mobileThumbnailVisibleToExclusive = visibleToExclusive;
        _mobileThumbnailWindowWasScrolling = scrolling;
        RefreshMobileThumbnailWantedIds();

        var visibleCandidates = new List<DriveItemModel>(Math.Min(32, Math.Max(0, visibleToExclusive - visibleFrom)));
        var cachedPrefetchCandidates = new List<DriveItemModel>(Math.Min(32, Math.Max(0, toExclusive - from)));
        var candidateIds = new HashSet<string>(StringComparer.Ordinal);

        void ProcessRange(int rangeFrom, int rangeToExclusive, bool visibleRange)
        {
            rangeFrom = Math.Max(from, rangeFrom);
            rangeToExclusive = Math.Min(toExclusive, rangeToExclusive);
            if (rangeFrom >= rangeToExclusive)
                return;

            for (var i = rangeFrom; i < rangeToExclusive; i++)
            {
                var item = MobileItems[i].Item;
                if (item is null)
                    continue;

                if (item.ThumbnailImage is not null)
                    TouchMobileThumbnail(item);

                if (!item.SupportsThumbnail || item.HasThumbnailImage || !candidateIds.Add(item.Id))
                    continue;

                var hasDiskCache = _thumbnailCache.TryGetCachedPath(item, out _);

                // Network thumbnail work is reserved for the actual visible screen. The buffer
                // only decodes persistent disk hits, so it can never occupy both mobile workers
                // while the user's final viewport is still waiting for cloud thumbnails.
                if (visibleRange)
                {
                    if (!scrolling || hasDiskCache)
                        visibleCandidates.Add(item);
                }
                else if (hasDiskCache)
                {
                    cachedPrefetchCandidates.Add(item);
                }
            }
        }

        var previousWindowValid = previousFrom >= 0 && previousToExclusive > previousFrom;
        var scrollStateChanged = scrolling != previousScrolling;
        var disjointWindow = previousWindowValid &&
                             (toExclusive <= previousFrom || from >= previousToExclusive);

        if (forceRescan || !previousWindowValid || scrollStateChanged || !scrolling || disjointWindow)
        {
            // The final visible screen must win over prefetch buffers. After a large fling this
            // starts exactly what the user is looking at before above/below-buffer work.
            ProcessRange(visibleFrom, visibleToExclusive, visibleRange: true);
            ProcessRange(visibleToExclusive, toExclusive, visibleRange: false);
            ProcessRange(from, visibleFrom, visibleRange: false);
        }
        else
        {
            // While flinging, only inspect newly entering edges. Stale queued workers self-cancel
            // against _mobileThumbnailWantedIds once the viewport has moved away.
            if (toExclusive > previousToExclusive)
                ProcessRange(Math.Max(previousToExclusive, from), toExclusive, visibleRange: false);
            if (from < previousFrom)
                ProcessRange(from, Math.Min(previousFrom, toExclusive), visibleRange: false);
        }

        if (visibleCandidates.Count > 0)
            StartThumbnailLoading(visibleCandidates, requireVisibleOnMobile: true);
        if (cachedPrefetchCandidates.Count > 0)
            StartThumbnailLoading(cachedPrefetchCandidates);
    }

    private void ResetDesktopThumbnailViewport()
    {
        _desktopThumbnailVisibleSlotIndices = new HashSet<int>();
        _desktopThumbnailWantedIds = new HashSet<string>(StringComparer.Ordinal);
    }

    private void ResetMobileThumbnailWindow()
    {
        _mobileThumbnailWindowFrom = -1;
        _mobileThumbnailWindowToExclusive = -1;
        _mobileThumbnailVisibleFrom = -1;
        _mobileThumbnailVisibleToExclusive = -1;
        _mobileThumbnailWindowWasScrolling = false;
        _mobileThumbnailWantedIds = new HashSet<string>(StringComparer.Ordinal);
        _mobileThumbnailVisibleIds = new HashSet<string>(StringComparer.Ordinal);
    }

    private void ApplyFilterAndSort()
    {
        IEnumerable<DriveItemModel> source = _allItems;
        var keyword = SearchText.Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
            source = source.Where(x => x.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));

        // Sorting is kept server/global-index consistent. The persistent local index applies the
        // same order before a folder is shown; newly streamed Graph pages already arrive in that
        // order, so the UI never repeatedly re-sorts a growing partial list.
        var visible = source.ToArray();
        ResetMobileThumbnailWindow();
        ResetDesktopThumbnailViewport();

        var slotCount = string.IsNullOrWhiteSpace(keyword)
            ? Math.Max(_currentFolderTotalItemCount ?? visible.Length, visible.Length)
            : visible.Length;
        RebuildMobileSlots(slotCount, visible);

        // Items is retained as a compatibility/read-only shadow for older integrations. Desktop UI
        // no longer binds to it, so Graph pages cannot grow the desktop scroll extent in chunks.
        if (!IsMobilePlatform)
        {
            Items.Clear();
            Items.AddRange(visible);
        }
    }

    internal static bool IsTransientNetworkFailure(Exception ex)
    {
        if (ex is OperationCanceledException)
            return false;

        if (ex is HttpRequestException http)
        {
            if (http.StatusCode is null)
                return true;

            var code = (int)http.StatusCode.Value;
            return code is 408 or 425 or 429 || code >= 500;
        }

        // TLS/DNS/socket failures are commonly wrapped by the authentication stack or file-cache
        // helpers. Walk the inner-exception chain instead of ever exposing their raw English text.
        return ex.InnerException is not null && IsTransientNetworkFailure(ex.InnerException);
    }

    private async Task<Exception?> RunFolderNavigationAsync(
        Func<CancellationToken, Task> action,
        CancellationTokenSource navigation,
        bool showBusy,
        bool suppressChildrenOnNonFolderError = false)
    {
        var busyVersion = Interlocked.Increment(ref _folderNavigationBusyVersion);

        // A superseding Back navigation is allowed to take ownership of the UI immediately.
        // Do not keep the old busy state alive until the canceled request unwinds.
        IsBusy = showBusy;
        ErrorMessage = null;
        Exception? failure = null;
        try
        {
            await action(navigation.Token);
        }
        catch (OperationCanceledException) when (navigation.IsCancellationRequested)
        {
            // Superseded folder loads are expected and silent.
        }
        catch (Exception ex)
        {
            failure = ex;
            if (IsTransientNetworkFailure(ex))
            {
                // Read-side Graph calls already retry continuously in OneDriveService. If a lower
                // layer still reports a transient transport failure, keep the current/local view and
                // stay silent rather than showing an SSL/DNS/socket message to the user.
                ErrorMessage = null;
            }
            else if (!(suppressChildrenOnNonFolderError && ex is GraphChildrenOnNonFolderException) &&
                     busyVersion == _folderNavigationBusyVersion)
            {
                ErrorMessage = ex.Message;
                StatusText = "操作失败";
            }
        }
        finally
        {
            if (busyVersion == _folderNavigationBusyVersion)
                IsBusy = false;

            if (ReferenceEquals(_folderNavigationCts, navigation))
                Interlocked.CompareExchange(ref _folderNavigationCts, null, navigation);

            navigation.Dispose();
        }

        return failure;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
            return;
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            await action();
        }
        catch (OperationCanceledException)
        {
            StatusText = "操作已取消";
        }
        catch (Exception ex)
        {
            if (IsTransientNetworkFailure(ex))
            {
                ErrorMessage = null;
            }
            else
            {
                ErrorMessage = ex.Message;
                StatusText = "操作失败";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private sealed class FolderCacheEntry
    {
        public FolderCacheEntry(
            List<DriveItemModel> items,
            string? nextLink,
            int? totalItemCount,
            DateTimeOffset timestampUtc,
            string? orderBy)
        {
            Items = items;
            NextLink = nextLink;
            TotalItemCount = totalItemCount;
            LastAccessUtc = timestampUtc;
            LastValidatedUtc = timestampUtc;
            OrderBy = orderBy;
        }

        public List<DriveItemModel> Items { get; }
        public string? NextLink { get; set; }
        public int? TotalItemCount { get; set; }
        public DateTimeOffset LastAccessUtc { get; set; }
        public DateTimeOffset LastValidatedUtc { get; set; }
        public string? OrderBy { get; }
    }

    private sealed class ObjectComparer : IComparer<object?>
    {
        public static readonly ObjectComparer Instance = new();
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            if (x is string xs && y is string ys)
                return StringComparer.CurrentCultureIgnoreCase.Compare(xs, ys);
            if (x is IComparable comparable)
                return comparable.CompareTo(y);
            return StringComparer.CurrentCultureIgnoreCase.Compare(x.ToString(), y.ToString());
        }
    }
}
