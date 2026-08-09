using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hello1Drive.Models;
using Hello1Drive.Services;

namespace Hello1Drive.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private const long TextPreviewLimit = 8L * 1024 * 1024;
    private static readonly TimeSpan FolderCacheValidationInterval = TimeSpan.FromSeconds(30);

    private readonly IOneDriveService _oneDrive;
    private readonly IAuthenticationService _authentication;
    private readonly AppSettingsService _settingsService;
    private readonly FileCacheService _fileCache;
    private readonly ThumbnailCacheService _thumbnailCache;
    private readonly TransferPersistenceService _transferPersistence;
    private readonly List<DriveItemModel> _allItems = [];
    private readonly Dictionary<string, FolderCacheEntry> _folderCache = new(StringComparer.Ordinal);
    private long _folderNavigationVersion;
    private readonly List<DriveItemModel> _selectedItems = [];
    private Func<string?, Task>? _promptAction;
    private bool _promptUseBusy = true;
    private bool _initialized;
    private CancellationTokenSource? _thumbnailLoadCts;
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

    public ObservableCollection<DriveItemModel> Items { get; } = [];
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];
    public ObservableCollection<TransferItemModel> Transfers { get; } = [];

    public event EventHandler<FolderNavigationEventArgs>? FolderNavigating;
    public event EventHandler<FolderNavigationEventArgs>? FolderLoaded;

    public IReadOnlyList<string> ThemeOptions { get; } = ["跟随系统", "浅色", "深色"];
    public IReadOnlyList<string> BackgroundModeOptions { get; } = ["默认", "纯色", "本地图片", "图片 URL", "本地文件夹", "OneDrive 文件夹"];

    [ObservableProperty] private DriveItemModel? selectedItem;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isAuthenticated;
    [ObservableProperty] private string userDisplayName = string.Empty;
    [ObservableProperty] private string userEmail = string.Empty;
    [ObservableProperty] private Bitmap? userAvatar;
    [ObservableProperty] private string quotaText = string.Empty;
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
    public string UserInitial => string.IsNullOrWhiteSpace(UserDisplayName) ? "M" : UserDisplayName.Trim()[0].ToString().ToUpperInvariant();

    public bool IsDetailsView => ViewMode == FileViewMode.Details;
    public bool IsLargeIconView => ViewMode == FileViewMode.LargeIcons;
    public bool IsExtraLargeIconView => ViewMode == FileViewMode.ExtraLargeIcons;

    public string NameSortIndicator => SortIndicator(FileSortColumn.Name);
    public string TypeSortIndicator => SortIndicator(FileSortColumn.Type);
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
        TransferPersistenceService transferPersistence)
    {
        _oneDrive = oneDrive;
        _authentication = authentication;
        _settingsService = settingsService;
        _fileCache = fileCache;
        _thumbnailCache = thumbnailCache;
        _transferPersistence = transferPersistence;
        _gifTimer.Tick += GifTimer_Tick;
        _slideshowTimer.Tick += SlideshowTimer_Tick;
        Breadcrumbs.Add(new BreadcrumbItem("OneDrive", null));
        LoadSettingsIntoProperties();
        ApplyTransferRateLimits();
        UpdateCacheStatus();
        RestorePersistedTransfers();
    }

    partial void OnIsAuthenticatedChanged(bool value) => OnPropertyChanged(nameof(IsNotAuthenticated));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnUserDisplayNameChanged(string value) => OnPropertyChanged(nameof(UserInitial));
    partial void OnUserAvatarChanged(Bitmap? value) => OnPropertyChanged(nameof(HasUserAvatar));

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
            Settings.LastFolderBreadcrumbs.Clear();
        else
            CaptureCurrentFolderMemory();
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

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;

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
        CancelThumbnailLoading();
        Items.Clear();
        ClearFolderCache();
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbItem("OneDrive", null));
        CurrentLocation = "OneDrive";
        SetSelectedItems([]);
        ClosePreview();
        StatusText = "已退出登录";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!IsAuthenticated)
            return;
        BeginFolderNavigation(FolderNavigationReason.Refresh);
        await RunBusyAsync(() => LoadCurrentFolderAsync(forceRemote: true));
    }

    public Task RefreshCurrentFolderAsync()
    {
        BeginFolderNavigation(FolderNavigationReason.Refresh);
        return LoadCurrentFolderAsync(forceRemote: true);
    }

    [RelayCommand]
    private async Task GoRootAsync()
    {
        if (!IsAuthenticated)
            return;
        BeginFolderNavigation(FolderNavigationReason.Root);
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbItem("OneDrive", null));
        await RunBusyAsync(() => LoadCurrentFolderAsync());
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (Breadcrumbs.Count <= 1)
            return;
        BeginFolderNavigation(FolderNavigationReason.Back);
        Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
        await RunBusyAsync(() => LoadCurrentFolderAsync());
    }

    public async Task OpenItemAsync(DriveItemModel item)
    {
        if (item.IsFolder)
        {
            BeginFolderNavigation(FolderNavigationReason.EnterChild);
            Breadcrumbs.Add(new BreadcrumbItem(item.Name, item.Id));
            await RunBusyAsync(() => LoadCurrentFolderAsync());
            return;
        }

        // Preview has its own cancellable loading state. Do not cover it with the global
        // busy overlay, otherwise Close/Back cannot cancel a large download.
        await LoadPreviewAsync(item);
    }

    public async Task NavigateToBreadcrumbAsync(BreadcrumbItem item)
    {
        var index = Breadcrumbs.IndexOf(item);
        if (index < 0)
            return;
        BeginFolderNavigation(FolderNavigationReason.Breadcrumb);
        while (Breadcrumbs.Count > index + 1)
            Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
        await RunBusyAsync(() => LoadCurrentFolderAsync());
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
    }

    public async Task SetViewModeAsync(FileViewMode mode)
    {
        ViewMode = mode;
        Settings.ViewMode = mode;
        await _settingsService.SaveAsync();
    }

    public void CycleSort(FileSortColumn column)
    {
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
            SortColumn = FileSortColumn.None;
            SortState = SortCycleState.Original;
        }
        ApplyFilterAndSort();
    }

    public void SetSort(FileSortColumn column, SortCycleState state)
    {
        SortColumn = state == SortCycleState.Original ? FileSortColumn.None : column;
        SortState = state;
        ApplyFilterAndSort();
    }

    private string SortIndicator(FileSortColumn column)
    {
        if (SortColumn != column || SortState == SortCycleState.Original)
            return string.Empty;
        return SortState == SortCycleState.Ascending ? "▲" : "▼";
    }

    private void RaiseSortIndicators()
    {
        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(TypeSortIndicator));
        OnPropertyChanged(nameof(SizeSortIndicator));
        OnPropertyChanged(nameof(ModifiedSortIndicator));
    }

    public async Task LoadPreviewAsync(DriveItemModel item, bool preserveSlideshow = false)
    {
        CancelPreviewLoad();
        DisposePreviewImageResources();
        PreviewText = string.Empty;
        PreviewMediaUrl = string.Empty;
        PreviewCachedFilePath = string.Empty;
        PreviewStatus = string.Empty;
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

                await using var stream = File.OpenRead(cachedPath);
                PreviewImage = new Bitmap(stream);
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
            PreviewStatus = "此类型暂不支持内嵌内容解析，可下载或使用关联应用打开。";
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
                await _fileCache.GetOrDownloadAsync(neighbour, _oneDrive, cancellationToken);
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
        IsPreviewVisible = false;
        IsPreviewLoading = false;
        IsPreviewDetailsVisible = false;
        PreviewKind = PreviewKind.None;
        PreviewItem = null;
        PreviewText = string.Empty;
        PreviewMediaUrl = string.Empty;
        PreviewCachedFilePath = string.Empty;
        PreviewStatus = string.Empty;
        DisposePreviewImageResources();
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

    private async Task LoadSignedInStateAsync()
    {
        var user = await _oneDrive.GetCurrentUserAsync();
        var drive = await _oneDrive.GetDriveInfoAsync();

        UserDisplayName = user.DisplayName ?? "Microsoft 用户";
        UserEmail = user.DisplayEmail;
        CurrentAccountId = !string.IsNullOrWhiteSpace(user.Id) ? user.Id! : user.DisplayEmail;
        IsAuthenticated = true;

        try
        {
            var photo = await _oneDrive.GetProfilePhotoAsync();
            if (photo is { Length: > 0 })
            {
                UserAvatar?.Dispose();
                using var ms = new MemoryStream(photo);
                UserAvatar = new Bitmap(ms);
            }
        }
        catch
        {
            UserAvatar?.Dispose();
            UserAvatar = null;
        }

        if (drive.Quota?.Total is > 0 && drive.Quota.Used is not null)
            QuotaText = $"已用 {DriveItemModel.FormatBytes(drive.Quota.Used.Value)} / {DriveItemModel.FormatBytes(drive.Quota.Total.Value)}";
        else
            QuotaText = "OneDrive";

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
    }

    private void BeginFolderNavigation(FolderNavigationReason reason)
    {
        FolderNavigating?.Invoke(this, new FolderNavigationEventArgs(reason, FolderCacheKey(CurrentFolderId)));
        _nextNavigationReason = reason;
    }

    private async Task LoadCurrentFolderAsync(bool forceRemote = false)
    {
        var folderId = CurrentFolderId;
        var cacheKey = FolderCacheKey(folderId);
        var navigationVersion = ++_folderNavigationVersion;
        var reason = _nextNavigationReason;
        _nextNavigationReason = FolderNavigationReason.Refresh;

        if (!forceRemote && _folderCache.TryGetValue(cacheKey, out var cached))
        {
            var now = DateTimeOffset.UtcNow;
            cached.LastAccessUtc = now;
            _nextChildrenLink = cached.NextLink;
            HasMoreItems = !string.IsNullOrWhiteSpace(_nextChildrenLink);
            ApplyFolderItems(cached.Items);
            FolderLoaded?.Invoke(this, new FolderNavigationEventArgs(reason, cacheKey));

            // Cached back/forward navigation is intentionally network-free. This preserves
            // both the loaded pages and exact scroll position; the toolbar Refresh command
            // is the explicit way to revalidate a directory listing.
            return;
        }

        var page = await _oneDrive.GetChildrenPageAsync(folderId, pageSize: 120);
        if (navigationVersion != _folderNavigationVersion || FolderCacheKey(CurrentFolderId) != cacheKey)
        {
            DisposeItemThumbnails(page.Items);
            return;
        }

        _nextChildrenLink = page.NextLink;
        HasMoreItems = page.HasMore;
        StoreFolderCache(cacheKey, page.Items, page.NextLink);
        ApplyFolderItems(page.Items);
        FolderLoaded?.Invoke(this, new FolderNavigationEventArgs(reason, cacheKey));
    }

    public async Task LoadMoreCurrentFolderAsync()
    {
        if (IsLoadingMore || string.IsNullOrWhiteSpace(_nextChildrenLink) || !IsAuthenticated)
            return;

        var cacheKey = FolderCacheKey(CurrentFolderId);
        var navigationVersion = _folderNavigationVersion;
        var nextLink = _nextChildrenLink;
        IsLoadingMore = true;
        try
        {
            var page = await _oneDrive.GetChildrenPageAsync(CurrentFolderId, nextLink, 120);
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
                entry.LastAccessUtc = DateTimeOffset.UtcNow;
            }

            _allItems.AddRange(page.Items);
            ApplyFilterAndSort();
            StatusText = HasMoreItems ? $"已加载 {_allItems.Count} 项 · 向下滚动继续加载" : $"{_allItems.Count} 个项目";
            StartThumbnailLoading(page.Items);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载更多失败：{ex.Message}";
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private async Task RefreshFolderInBackgroundAsync(string? folderId, string cacheKey, long navigationVersion)
    {
        try
        {
            var remotePage = await _oneDrive.GetChildrenPageAsync(folderId, pageSize: 120);
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
                StoreFolderCache(cacheKey, remotePage.Items, remotePage.NextLink);
                ApplyFolderItems(remotePage.Items);
            });
        }
        catch
        {
            // Cached navigation remains usable when a background refresh fails.
        }
    }

    private void ApplyFolderItems(IReadOnlyList<DriveItemModel> items)
    {
        CancelThumbnailLoading();
        Items.Clear();
        _allItems.Clear();
        _allItems.AddRange(items);
        ApplyFilterAndSort();
        CurrentLocation = string.Join(" / ", Breadcrumbs.Select(x => x.Name));
        StatusText = HasMoreItems ? $"已加载 {_allItems.Count} 项 · 向下滚动继续加载" : $"{_allItems.Count} 个项目";
        SetSelectedItems([]);
        StartThumbnailLoading(items);
        if (RememberLastFolder)
        {
            CaptureCurrentFolderMemory();
            _ = _settingsService.SaveAsync();
        }
    }

    private void StoreFolderCache(string cacheKey, IReadOnlyList<DriveItemModel> items, string? nextLink = null)
    {
        if (_folderCache.TryGetValue(cacheKey, out var previous) && !ReferenceEquals(previous.Items, items))
            DisposeItemThumbnails(previous.Items);

        _folderCache[cacheKey] = new FolderCacheEntry(items.ToList(), nextLink, DateTimeOffset.UtcNow);
        TrimFolderCache(cacheKey);
    }

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
        _nextChildrenLink = null;
        HasMoreItems = false;
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
        var mediaItems = items.Where(x => x.SupportsThumbnail && !x.HasThumbnailImage).ToArray();
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
        using var gate = new SemaphoreSlim(6);
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
        await gate.WaitAsync(cancellationToken);
        try
        {
            var cachedPath = await _thumbnailCache.GetOrDownloadAsync(item, _oneDrive, cancellationToken);
            if (string.IsNullOrWhiteSpace(cachedPath) || cancellationToken.IsCancellationRequested)
                return;

            Bitmap? bitmap = null;
            try
            {
                await using var stream = new FileStream(
                    cachedPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                bitmap = new Bitmap(stream);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // A partially written/corrupt cache entry should heal itself once.
                _thumbnailCache.Invalidate(item.Id);
                cachedPath = await _thumbnailCache.GetOrDownloadAsync(item, _oneDrive, cancellationToken);
                if (string.IsNullOrWhiteSpace(cachedPath))
                    return;

                await using var retryStream = new FileStream(
                    cachedPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                bitmap = new Bitmap(retryStream);
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
                if (cancellationToken.IsCancellationRequested || !_allItems.Contains(item))
                {
                    bitmap?.Dispose();
                    bitmap = null;
                    return;
                }

                item.ThumbnailImage?.Dispose();
                item.ThumbnailImage = bitmap;
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
            gate.Release();
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

    private void ApplyFilterAndSort()
    {
        IEnumerable<DriveItemModel> source = _allItems;
        var keyword = SearchText.Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
            source = source.Where(x => x.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));

        if (SortState != SortCycleState.Original && SortColumn != FileSortColumn.None)
        {
            if (SortColumn == FileSortColumn.Name)
            {
                // Match Windows launcher/file-manager expectations for mixed Chinese + Latin names:
                // Chinese characters compare by pinyin in the same A-Z sequence as English names.
                source = SortState == SortCycleState.Ascending
                    ? source.OrderBy(x => x.Name, PinyinNameComparer.Instance)
                    : source.OrderByDescending(x => x.Name, PinyinNameComparer.Instance);
            }
            else
            {
                Func<DriveItemModel, object?> selector = SortColumn switch
                {
                    FileSortColumn.Type => x => x.TypeDisplay,
                    FileSortColumn.Size => x => x.Size,
                    FileSortColumn.Modified => x => x.LastModifiedDateTime,
                    _ => x => x.Name
                };
                source = SortState == SortCycleState.Ascending
                    ? source.OrderBy(selector, ObjectComparer.Instance)
                    : source.OrderByDescending(selector, ObjectComparer.Instance);
            }
        }

        Items.Clear();
        foreach (var item in source)
            Items.Add(item);
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
        public FolderCacheEntry(List<DriveItemModel> items, string? nextLink, DateTimeOffset timestampUtc)
        {
            Items = items;
            NextLink = nextLink;
            LastAccessUtc = timestampUtc;
            LastValidatedUtc = timestampUtc;
        }

        public List<DriveItemModel> Items { get; }
        public string? NextLink { get; set; }
        public DateTimeOffset LastAccessUtc { get; set; }
        public DateTimeOffset LastValidatedUtc { get; set; }
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
