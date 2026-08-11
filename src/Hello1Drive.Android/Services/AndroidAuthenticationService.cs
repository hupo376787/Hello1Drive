using Hello1Drive.Configuration;
using Hello1Drive.Services;
using Microsoft.Identity.Client;

namespace Hello1Drive.Android.Services;

public sealed class AndroidAuthenticationService : IAuthenticationService
{
    private readonly IPublicClientApplication _app = PublicClientApplicationBuilder
        .Create(AppConfig.ClientId)
        .WithAuthority(AppConfig.Authority)
        .WithRedirectUri(AppConfig.AndroidRedirectUri)
        .Build();

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _cachedAccessToken;
    private DateTimeOffset _cachedAccessTokenExpiresOn;

    public async Task<string?> GetAccessTokenAsync(bool interactive, CancellationToken cancellationToken = default)
    {
        if (HasUsableInMemoryToken())
            return _cachedAccessToken;

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (HasUsableInMemoryToken())
                return _cachedAccessToken;

            var account = (await _app.GetAccountsAsync()).FirstOrDefault();
            if (account is not null)
            {
                try
                {
                    var result = await _app.AcquireTokenSilent(AppConfig.GraphScopes, account)
                        .ExecuteAsync(cancellationToken);
                    CacheResult(result);
                    return result.AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                }
            }

            if (!interactive)
                return null;

            var activity = MainActivity.Instance
                ?? throw new InvalidOperationException("Android Activity 尚未初始化。");

            var interactiveResult = await _app.AcquireTokenInteractive(AppConfig.GraphScopes)
                .WithParentActivityOrWindow(activity)
                .ExecuteAsync(cancellationToken);
            CacheResult(interactiveResult);
            return interactiveResult.AccessToken;
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
        foreach (var account in await _app.GetAccountsAsync())
            await _app.RemoveAsync(account);
    }
}
