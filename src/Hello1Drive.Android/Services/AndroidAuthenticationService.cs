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

    public async Task<string?> GetAccessTokenAsync(bool interactive, CancellationToken cancellationToken = default)
    {
        var account = (await _app.GetAccountsAsync()).FirstOrDefault();
        if (account is not null)
        {
            try
            {
                var result = await _app.AcquireTokenSilent(AppConfig.GraphScopes, account)
                    .ExecuteAsync(cancellationToken);
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
        return interactiveResult.AccessToken;
    }

    public bool TryHandleProtocolActivation(Uri uri) => false;

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        foreach (var account in await _app.GetAccountsAsync())
            await _app.RemoveAsync(account);
    }
}
