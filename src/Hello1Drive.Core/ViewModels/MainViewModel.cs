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
    private static int CurrentFolderPageSize => IsMobilePlatform ? 160 : 120;
    private volatile bool _mobileListScrolling;
    private int _mobileThumbnailWindowFrom = -1;
    private int _mobileThumbnailWindowToExclusive = -1;

    private readonly IOneDriveService _oneDrive;
    private readonly IAuthenticationService _authentication;
    private readonly AppSettingsService _settingsService;
    private readonly FileCacheService _fileCache;
    private readonly ThumbnailCacheService _thumbnailCache;
    private readonly TransferPersistenceService _transferPersistence;
    private readonly StartupSnapshotService _startupSnapshot;
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
    private CancellationTokenSource? _loadMoreCts;
    private readonly List<DriveItemModel> _selectedItems = [];
    private Func<string?, Task>? _promptAction;
    private bool _promptUseBusy = true;
    private bool _initialized;
    private CancellationTokenSource? _thumbnailLoadCts;
    private readonly ConcurrentDictionary<string, byte> _thumbnailLoadsInFlight = new(StringComparer.Ordinal);
    private int _previewImagePixelWidth;
    private int _previewImagePixelHeight;
    private AnimatedGifData? _gifAnimation;
    private int _gifFrameIndex;
    private readonly DispatcherTimer _gifTimer = new();
    private readonly DispatcherTimer _slideshowTimer = new();
    private CancellationTokenSource? _previewLoadCts;
    private CancellationTokenSource? _previewPrefetchCts;
    private string? _nextChildrenLink;
    private CancellationTokenSource? _transferPersistenceCts;
    private CancellationTokenSource? _cacheStatusRefreshCts;
    private readonly SemaphoreSlim _cacheTransferGate = new(2, 2);
    private FolderNavigationReason _nextNavigationReason = FolderNavigationReason.Initial;
    private bool _syncingBackgroundColor;
    private bool _syncingDefaultSortSetting;
    private bool _syncingStartWithWindowsSetting;
    private int? _currentFolderTotalItemCount;
    private bool _startupSnapshotRestored;
    private string _startupSnapshotAccountId = string.Empty;

    public AvaloniaList<DriveItemModel> Items { get; } = [];
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
    [ObservableProperty] private bool isLoadingMore;

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
    public string ItemCountText => $"{(_currentFolderTotalItemCount ?? Items.Count)} 项";

    private void SetCurrentFolderTotalItemCount(int? total)
    {
        var normalized = total is >= 0 ? total : null;
        if (_currentFolderTotalItemCount == normalized)
            return;

        _currentFolderTotalItemCount = normalized;
        OnPropertyChanged(nameof(ItemCountText));
    }

    public void SetMobileListScrolling(bool value)
    {
        if (IsMobilePlatform)
            _mobileListScrolling = value;
    }
    public string CloseConfirmationMessage => ActiveTransferCount > 0
        ? $"当前还有 {ActiveTransferCount} 个传输任务正在等待或进行中。关闭后任务列表会保存，可恢复的任务会在下次打开 Hello1Drive 时自动继续。确定关闭吗？"
        : "确定关闭软件吗？";

    public string? CurrentFolderId => Breadcrumbs.LastOrDefault()?.ItemId;
    public string CurrentAccountId { get; private set; } = string.Empty;
    public IReadOnlyList<DriveItemModel> SelectedItemsSnapshot => _selectedItems.ToArray();
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
        _startupRegistrationService = startupRegistrationService;
        _transferBackgroundService = transferBackgroundService;
        _gifTimer.Tick += GifTimer_Tick;
        _slideshowTimer.Tick += SlideshowTimer_Tick;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ItemCountText));
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
                var token = await _authentication.GetAccessTokenAsync(interactive: false);
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
                // A valid local snapshot is still useful when startup happens offline. Keep it
                // visible instead of replacing the screen with a blocking failure page.
                ErrorMessage = $"OneDrive 同步失败：{ex.Message}";
                StatusText = "已显示本地缓存 · 暂时无法同步";
            }

            return;
        }

        await RunBusyAsync(async () =>
        {
            var token = await _authentication.GetAccessTokenAsync(interactive: false);
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
        Items.Clear();
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
            ErrorMessage = ex.Message;
            StatusText = "操作失败";
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

    public TransferItemModel RegisterTransfer(string fileName, TransferDirection direction)
    {
        var transfer = new TransferItemModel
        {
            FileName = fileName,
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
            transfer.Message = ex.Message;
            ErrorMessage = ex.Message;
            StatusText = $"上传失败：{fileName}";
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
            transfer.Message = ex.Message;
            ErrorMessage = ex.Message;
            StatusText = $"下载失败：{item.Name}";
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
            transfer.Message = ex.Message;
            ErrorMessage = ex.Message;
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
                PreviewStatus = "预览加载失败";
                ErrorMessage = ex.Message;
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
            PreviewStatus = "文件缓存失败";
            ErrorMessage = ex.Message;
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
        var images = Items.Where(static x => x.IsImage).ToArray();
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
        var sequence = Items.Where(x => x.IsFile && (!imagesOnly || x.IsImage)).ToArray();
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

        var images = Items.Where(static x => x.IsImage).ToArray();
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
        var bitmaps = Items
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
            // A folder page may fail while previously discovered files are already queued.
            // Keep those rows/tasks intact and surface only the enumeration error.
            ErrorMessage = ex.Message;
            StatusText = jobs.Count > 0 ? "部分缓存任务已加入，后续文件获取失败" : "缓存任务准备失败";
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
        _fileCache.Clear();
        _thumbnailCache.Clear();
        ClearFolderCache();
        UpdateCacheStatus();

        if (IsAuthenticated)
        {
            await RunBusyAsync(() => LoadCurrentFolderAsync(forceRemote: true));
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
        if (IsMobilePlatform)
        {
            var cachedThumbs = restoredItems
                .Where(x => x.SupportsThumbnail && _thumbnailCache.TryGetCachedPath(x, out _))
                .Take(48)
                .ToArray();
            if (cachedThumbs.Length > 0)
                StartThumbnailLoading(cachedThumbs);
        }
        else
        {
            var cachedThumbs = restoredItems
                .Where(x => x.SupportsThumbnail && _thumbnailCache.TryGetCachedPath(x, out _))
                .Take(96)
                .ToArray();
            if (cachedThumbs.Length > 0)
                StartThumbnailLoading(cachedThumbs);
        }
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
            StatusText = HasMoreItems
                ? $"已加载 {_allItems.Count} 项 · 向下滚动继续加载"
                : $"{_allItems.Count} 个项目";
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
                ErrorMessage = $"OneDrive 同步失败：{ex.Message}";
                StatusText = "已显示本地缓存 · 暂时无法同步";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"OneDrive 同步失败：{ex.Message}";
            StatusText = "已显示本地缓存 · 暂时无法同步";
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
            Items = _allItems.Select(StartupDriveItem.FromModel).ToList(),
            NextLink = _nextChildrenLink,
            TotalItemCount = _currentFolderTotalItemCount
        };

        await _startupSnapshot.SaveAsync(snapshot);
    }

    private void ClearRestoredStartupState(bool clearAuthenticationState = true)
    {
        _startupSnapshotRestored = false;
        _startupSnapshotAccountId = string.Empty;
        _startupSnapshot.Clear();
        CancelThumbnailLoading();
        ResetMobileThumbnailWindow();
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
            _ = RefreshRestoredFolderInBackgroundAsync();
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

        _ = SaveStartupSnapshotAsync();
    }

    private CancellationTokenSource BeginFolderNavigation(FolderNavigationReason reason)
    {
        // Capture the view of the folder we are leaving before Breadcrumbs changes. This makes
        // even inherited/default views become an explicit per-folder memory after the first visit.
        RememberCurrentFolderViewMode();
        _ = _settingsService.SaveAsync();

        // Stop work that belongs to the folder we are leaving. In particular, old thumbnail
        // requests and a pending "load more" page must not compete with the new folder's first
        // children request on a mobile connection.
        CancelThumbnailLoading();
        ResetMobileThumbnailWindow();
        var previousLoadMore = Interlocked.Exchange(ref _loadMoreCts, null);
        previousLoadMore?.Cancel();
        IsLoadingMore = false;

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
            ApplyFolderItems(cached.Items);
            FolderLoaded?.Invoke(this, new FolderNavigationEventArgs(reason, cacheKey));

            // Cached back/forward navigation is intentionally network-free. This preserves
            // both the loaded pages and exact scroll position; the toolbar Refresh command
            // is the explicit way to revalidate a directory listing.
            return;
        }

        // The first children page is the only request on the folder-opening critical path.
        // ChildCount is already known for most clicked folders from the parent listing; for
        // root/unknown counts, metadata is refreshed after the list is visible instead of
        // delaying navigation behind a second Graph round-trip.
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
            // A few OneDrive consumer backends still reject size ordering even with the
            // non-indexed-query Prefer header. Do not leave the folder unusable or keep a
            // broken remembered rule: fall back to the API's original order for this folder.
            SortColumn = FileSortColumn.None;
            SortState = SortCycleState.Original;
            await PersistCurrentFolderSortRuleAsync();
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
        if (totalCount is null && !page.HasMore)
            totalCount = page.Items.Count;
        SetCurrentFolderTotalItemCount(totalCount);
        StoreFolderCache(cacheKey, page.Items, page.NextLink, totalCount);
        ApplyFolderItems(page.Items);
        if (sizeSortFallback)
            StatusText = "当前账户后端不支持大小排序，已对当前文件夹改用系统默认顺序";
        FolderLoaded?.Invoke(this, new FolderNavigationEventArgs(reason, cacheKey));

        if (totalCount is null && page.HasMore)
            _ = RefreshFolderTotalCountInBackgroundAsync(folderId, cacheKey, navigationVersion);
    }

    public async Task LoadMoreCurrentFolderAsync()
    {
        if (IsLoadingMore || string.IsNullOrWhiteSpace(_nextChildrenLink) || !IsAuthenticated)
            return;

        var cacheKey = FolderCacheKey(CurrentFolderId);
        var navigationVersion = _folderNavigationVersion;
        var nextLink = _nextChildrenLink;
        var loadMoreCts = new CancellationTokenSource();
        var previousLoadMore = Interlocked.Exchange(ref _loadMoreCts, loadMoreCts);
        previousLoadMore?.Cancel();
        IsLoadingMore = true;
        try
        {
            var page = await _oneDrive.GetChildrenPageAsync(
                CurrentFolderId, nextLink, CurrentFolderPageSize, loadMoreCts.Token);
            if (navigationVersion != _folderNavigationVersion || FolderCacheKey(CurrentFolderId) != cacheKey)
            {
                DisposeItemThumbnails(page.Items);
                return;
            }

            _nextChildrenLink = page.NextLink;
            HasMoreItems = page.HasMore;
            if (_folderCache.TryGetValue(cacheKey, out var entry))
            {
                entry.Items.AddRange(page.Items);
                entry.NextLink = page.NextLink;
                if (!page.HasMore && entry.TotalItemCount is null)
                    entry.TotalItemCount = entry.Items.Count;
                entry.LastAccessUtc = DateTimeOffset.UtcNow;
            }

            _allItems.AddRange(page.Items);
            foreach (var pageItem in page.Items)
            {
                if (!string.IsNullOrWhiteSpace(pageItem.Id))
                    _currentItemIds.Add(pageItem.Id);
            }
            if (!page.HasMore && _currentFolderTotalItemCount is null)
                SetCurrentFolderTotalItemCount(_allItems.Count);
            AppendLoadedPageToVisibleItems(page.Items);
            StatusText = HasMoreItems ? $"已加载 {_allItems.Count} 项 · 向下滚动继续加载" : $"{_allItems.Count} 个项目";
            if (!IsMobilePlatform)
                StartThumbnailLoading(page.Items);

            _ = SaveStartupSnapshotAsync();
        }
        catch (OperationCanceledException) when (loadMoreCts.IsCancellationRequested)
        {
            // Folder navigation or a newer page request superseded this one.
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载更多失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadMoreCts, loadMoreCts))
            {
                Interlocked.CompareExchange(ref _loadMoreCts, null, loadMoreCts);
                IsLoadingMore = false;
            }
            loadMoreCts.Dispose();
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
                if (_folderCache.TryGetValue(cacheKey, out var entry))
                    entry.TotalItemCount = total;
                _ = SaveStartupSnapshotAsync();
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

    private void ApplyFolderItems(IReadOnlyList<DriveItemModel> items)
    {
        CancelThumbnailLoading();
        ResetMobileThumbnailWindow();
        Items.Clear();
        _allItems.Clear();
        _currentItemIds.Clear();
        _allItems.AddRange(items);
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
                _currentItemIds.Add(item.Id);
        }
        ApplyFilterAndSort();
        CurrentLocation = string.Join(" / ", Breadcrumbs.Select(x => x.Name));
        StatusText = HasMoreItems ? $"已加载 {_allItems.Count} 项 · 向下滚动继续加载" : $"{_allItems.Count} 个项目";
        SetSelectedItems([]);
        if (!IsMobilePlatform)
            StartThumbnailLoading(items);
        if (RememberLastFolder)
        {
            CaptureCurrentFolderMemory();
            _ = _settingsService.SaveAsync();
        }

        if (IsAuthenticated && !string.IsNullOrWhiteSpace(CurrentAccountId))
            _ = SaveStartupSnapshotAsync();
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

    private bool HasFolderCache(string? folderId) => _folderCache.ContainsKey(FolderCacheKey(folderId));

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

    private void StartThumbnailLoading(IReadOnlyList<DriveItemModel> items)
    {
        var mediaItems = items
            .Where(x => x.SupportsThumbnail && !x.HasThumbnailImage && _thumbnailLoadsInFlight.TryAdd(x.Id, 0))
            .ToArray();
        if (mediaItems.Length == 0)
            return;

        var cts = _thumbnailLoadCts;
        if (cts is null || cts.IsCancellationRequested)
        {
            cts = new CancellationTokenSource();
            _thumbnailLoadCts = cts;
        }
        _ = LoadThumbnailsInBackgroundAsync(mediaItems, cts.Token);
    }

    private async Task LoadThumbnailsInBackgroundAsync(
        IReadOnlyList<DriveItemModel> items,
        CancellationToken cancellationToken)
    {
        // Keep concurrent thumbnail downloads modest so large photo folders stay responsive
        // and do not flood Microsoft Graph / the thumbnail CDN with hundreds of requests.
        var concurrency = IsMobilePlatform ? 2 : 6;
        using var gate = new SemaphoreSlim(concurrency);
        var tasks = items.Select(item => LoadThumbnailForItemAsync(item, gate, cancellationToken));

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
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        var gateAcquired = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;

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
                if (cancellationToken.IsCancellationRequested || !_currentItemIds.Contains(item.Id))
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
            _thumbnailLoadsInFlight.TryRemove(item.Id, out _);
            if (gateAcquired)
                gate.Release();
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

    public void UpdateMobileThumbnailWindow(int startIndex, int visibleCount)
    {
        if (!IsMobilePlatform || Items.Count == 0)
            return;

        var buffer = Math.Max(16, visibleCount);
        var from = Math.Max(0, startIndex - buffer);
        var toExclusive = Math.Min(Items.Count, startIndex + visibleCount + buffer);

        _mobileThumbnailWindowFrom = from;
        _mobileThumbnailWindowToExclusive = toExclusive;

        // Keep decoded thumbnails around in a bounded LRU instead of clearing them when they
        // leave the viewport. Touch the current window so it cannot be the next eviction victim.
        for (var i = from; i < toExclusive; i++)
        {
            var item = Items[i];
            if (item.ThumbnailImage is not null)
                TouchMobileThumbnail(item);
        }

        var candidates = Items
            .Skip(from)
            .Take(toExclusive - from)
            .Where(x => x.SupportsThumbnail && !x.HasThumbnailImage)
            .ToArray();

        if (candidates.Length == 0)
            return;

        if (_mobileListScrolling)
        {
            // During a fling only hydrate items that already exist in the persistent disk cache.
            // This mirrors native image loaders: memory/disk hits are allowed to bind while
            // scrolling, but new network work waits until the gesture settles.
            candidates = candidates
                .Where(x => _thumbnailCache.TryGetCachedPath(x, out _))
                .ToArray();
        }

        StartThumbnailLoading(candidates);
    }

    private void ResetMobileThumbnailWindow()
    {
        _mobileThumbnailWindowFrom = -1;
        _mobileThumbnailWindowToExclusive = -1;
    }

    private void ApplyFilterAndSort()
    {
        IEnumerable<DriveItemModel> source = _allItems;
        var keyword = SearchText.Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
            source = source.Where(x => x.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));

        // Sorting is intentionally limited to fields that OneDrive can order server-side.
        // This keeps paging globally ordered even in folders containing thousands of items.

        ResetMobileThumbnailWindow();
        Items.Clear();
        Items.AddRange(source);
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
            if (!(suppressChildrenOnNonFolderError && ex is GraphChildrenOnNonFolderException) &&
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
            ErrorMessage = ex.Message;
            StatusText = "操作失败";
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
