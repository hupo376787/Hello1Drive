namespace Hello1Drive.Services;

public static class AppServices
{
    private static IAuthenticationService? _authentication;
    private static IOneDriveService? _oneDrive;
    private static IEmbeddedMediaPlayerFactory? _mediaPlayerFactory;
    private static readonly AppSettingsService _settings = new();
    private static readonly FileCacheService _fileCache = new();
    private static readonly ThumbnailCacheService _thumbnailCache = new();
    private static readonly TransferPersistenceService _transferPersistence = new();

    public static IAuthenticationService Authentication =>
        _authentication ?? throw new InvalidOperationException("Authentication service has not been configured by the platform head.");

    public static IOneDriveService OneDrive =>
        _oneDrive ?? throw new InvalidOperationException("OneDrive service has not been configured by the platform head.");

    public static AppSettingsService Settings => _settings;
    public static FileCacheService FileCache => _fileCache;
    public static ThumbnailCacheService ThumbnailCache => _thumbnailCache;
    public static TransferPersistenceService TransferPersistence => _transferPersistence;
    public static IEmbeddedMediaPlayerFactory? MediaPlayerFactory => _mediaPlayerFactory;

    public static void Configure(IAuthenticationService authentication, IEmbeddedMediaPlayerFactory? mediaPlayerFactory = null)
    {
        _authentication = authentication;
        _oneDrive = new OneDriveService(authentication);
        _mediaPlayerFactory = mediaPlayerFactory;
    }
}
