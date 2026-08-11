using Hello1Drive.Configuration;
using Hello1Drive.Services;
using Microsoft.Identity.Client;

namespace Hello1Drive.Desktop.Services;

public sealed class DesktopAuthenticationService : IAuthenticationService
{
    private readonly IPublicClientApplication _app;
    private readonly string _cachePath;
    private readonly object _cacheLock = new();
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _cachedAccessToken;
    private DateTimeOffset _cachedAccessTokenExpiresOn;

    public DesktopAuthenticationService()
    {
        _app = PublicClientApplicationBuilder
            .Create(AppConfig.ClientId)
            .WithAuthority(AppConfig.Authority)
            .WithRedirectUri(AppConfig.DesktopRedirectUri)
            .Build();

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cacheDir = Path.Combine(baseDir, "Hello1Drive");
        Directory.CreateDirectory(cacheDir);
        _cachePath = Path.Combine(cacheDir, "hello1drive.msalcache");
        ConfigureTokenCache();
    }

    public async Task<string?> GetAccessTokenAsync(bool interactive, CancellationToken cancellationToken = default)
    {
        if (HasUsableInMemoryToken())
            return _cachedAccessToken;

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (HasUsableInMemoryToken())
                return _cachedAccessToken;

            var accounts = await _app.GetAccountsAsync();
            var account = accounts.FirstOrDefault();

            if (account is not null)
            {
                try
                {
                    var silent = await _app.AcquireTokenSilent(AppConfig.GraphScopes, account)
                        .ExecuteAsync(cancellationToken);
                    CacheResult(silent);
                    return silent.AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                    // Interactive sign-in below when requested.
                }
            }

            if (!interactive)
                return null;

            var result = await _app.AcquireTokenInteractive(AppConfig.GraphScopes)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(cancellationToken);
            CacheResult(result);
            return result.AccessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private bool HasUsableInMemoryToken() =>
        !string.IsNullOrWhiteSpace(_cachedAccessToken) &&
        DateTimeOffset.UtcNow < _cachedAccessTokenExpiresOn - TimeSpan.FromMinutes(2);

    private void CacheResult(AuthenticationResult result)
    {
        _cachedAccessToken = result.AccessToken;
        _cachedAccessTokenExpiresOn = result.ExpiresOn;
    }

    public bool TryHandleProtocolActivation(Uri uri) => false;

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _cachedAccessToken = null;
        _cachedAccessTokenExpiresOn = default;
        var accounts = await _app.GetAccountsAsync();
        foreach (var account in accounts)
            await _app.RemoveAsync(account);

        lock (_cacheLock)
        {
            if (File.Exists(_cachePath))
                File.Delete(_cachePath);
        }
    }

    private void ConfigureTokenCache()
    {
        _app.UserTokenCache.SetBeforeAccess(args =>
        {
            lock (_cacheLock)
            {
                if (File.Exists(_cachePath))
                    args.TokenCache.DeserializeMsalV3(File.ReadAllBytes(_cachePath));
            }
        });

        _app.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged)
                return;

            lock (_cacheLock)
            {
                File.WriteAllBytes(_cachePath, args.TokenCache.SerializeMsalV3());
                TryRestrictUnixPermissions(_cachePath);
            }
        });
    }

    private static void TryRestrictUnixPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best-effort only. The cache still follows the current user's profile directory permissions.
        }
    }
}
