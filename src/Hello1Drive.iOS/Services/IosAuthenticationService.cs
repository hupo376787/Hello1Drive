using Hello1Drive.Configuration;
using Hello1Drive.Services;
using Microsoft.Identity.Client;
using UIKit;

namespace Hello1Drive.iOS.Services;

public sealed class IosAuthenticationService : IAuthenticationService
{
    private readonly IPublicClientApplication _app;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _cachedAccessToken;
    private DateTimeOffset _cachedAccessTokenExpiresOn;

    public IosAuthenticationService()
    {
        _app = PublicClientApplicationBuilder
            .Create(AppConfig.ClientId)
            .WithAuthority(AppConfig.Authority)
            .WithRedirectUri(AppConfig.IosRedirectUri)
            .WithIosKeychainSecurityGroup("com.microsoft.adalcache")
            .Build();
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

            var controller = GetCurrentViewController()
                ?? throw new InvalidOperationException("找不到当前 iOS UIViewController。");

            var interactiveResult = await _app.AcquireTokenInteractive(AppConfig.GraphScopes)
                .WithParentActivityOrWindow(controller)
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

    public bool TryHandleProtocolActivation(Uri uri)
    {
        using var nativeUrl = new Foundation.NSUrl(uri.AbsoluteUri);
        return AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(nativeUrl);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _cachedAccessToken = null;
        _cachedAccessTokenExpiresOn = default;
        foreach (var account in await _app.GetAccountsAsync())
            await _app.RemoveAsync(account);
    }

    private static UIViewController? GetCurrentViewController()
    {
        var window = UIApplication.SharedApplication.Windows.FirstOrDefault(w => w.IsKeyWindow)
                     ?? UIApplication.SharedApplication.Windows.FirstOrDefault();
        var controller = window?.RootViewController;
        while (controller?.PresentedViewController is not null)
            controller = controller.PresentedViewController;
        return controller;
    }
}
