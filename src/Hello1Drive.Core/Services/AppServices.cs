namespace Hello1Drive.Services;

public static class AppServices
{
    private static IAuthenticationService? _authentication;
    private static IOneDriveService? _oneDrive;
    private static IEmbeddedMediaPlayerFactory? _mediaPlayerFactory;
    private static IPlatformShareService? _platformShareService;
    private static IPlatformAppLifecycleService? _platformAppLifecycleService;
    private static IPlatformConfirmationService? _platformConfirmationService;
    private static IStartupRegistrationService? _startupRegistrationService;
    private static ITransferBackgroundService? _transferBackgroundService;
    private static INativeMobileFileListFactory? _nativeMobileFileListFactory;
    private static IDesktopInputSettingsService? _desktopInputSettingsService;
    private static readonly AppSettingsService _settings = new();
    private static readonly FileCacheService _fileCache = new();
    private static readonly ThumbnailCacheService _thumbnailCache = new();
    private static readonly TransferPersistenceService _transferPersistence = new();
    private static readonly StartupSnapshotService _startupSnapshot = new();
    private static readonly LocalDriveIndexService _localDriveIndex = new();

    public static IAuthenticationService Authentication =>
        _authentication ?? throw new InvalidOperationException("Authentication service has not been configured by the platform head.");

    public static IOneDriveService OneDrive =>
        _oneDrive ?? throw new InvalidOperationException("OneDrive service has not been configured by the platform head.");

    public static AppSettingsService Settings => _settings;
    public static FileCacheService FileCache => _fileCache;
    public static ThumbnailCacheService ThumbnailCache => _thumbnailCache;
    public static TransferPersistenceService TransferPersistence => _transferPersistence;
    public static StartupSnapshotService StartupSnapshot => _startupSnapshot;
    public static LocalDriveIndexService LocalDriveIndex => _localDriveIndex;
    public static IEmbeddedMediaPlayerFactory? MediaPlayerFactory => _mediaPlayerFactory;
    public static IPlatformShareService? PlatformShareService => _platformShareService;
    public static IPlatformAppLifecycleService? PlatformAppLifecycleService => _platformAppLifecycleService;
    public static IPlatformConfirmationService? PlatformConfirmationService => _platformConfirmationService;
    public static IStartupRegistrationService? StartupRegistrationService => _startupRegistrationService;
    public static ITransferBackgroundService? TransferBackgroundService => _transferBackgroundService;
    public static INativeMobileFileListFactory? NativeMobileFileListFactory => _nativeMobileFileListFactory;
    public static IDesktopInputSettingsService? DesktopInputSettingsService => _desktopInputSettingsService;

    public static void Configure(
        IAuthenticationService authentication,
        IEmbeddedMediaPlayerFactory? mediaPlayerFactory = null,
        IPlatformShareService? platformShareService = null,
        IPlatformAppLifecycleService? platformAppLifecycleService = null,
        IPlatformConfirmationService? platformConfirmationService = null,
        IStartupRegistrationService? startupRegistrationService = null,
        ITransferBackgroundService? transferBackgroundService = null,
        INativeMobileFileListFactory? nativeMobileFileListFactory = null,
        IDesktopInputSettingsService? desktopInputSettingsService = null)
    {
        _authentication = authentication;
        _oneDrive = new ResilientOneDriveService(new OneDriveService(authentication));
        _mediaPlayerFactory = mediaPlayerFactory;
        _platformShareService = platformShareService;
        _platformAppLifecycleService = platformAppLifecycleService;
        _platformConfirmationService = platformConfirmationService;
        _startupRegistrationService = startupRegistrationService;
        _transferBackgroundService = transferBackgroundService;
        _nativeMobileFileListFactory = nativeMobileFileListFactory;
        _desktopInputSettingsService = desktopInputSettingsService;
    }
}
