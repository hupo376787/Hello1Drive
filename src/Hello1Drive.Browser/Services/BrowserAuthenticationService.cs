using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Hello1Drive.Configuration;
using Hello1Drive.Services;

namespace Hello1Drive.Browser.Services;

[SupportedOSPlatform("browser")]
public sealed class BrowserAuthenticationService : IAuthenticationService
{
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    private static string ScopeText => string.Join(' ', AppConfig.GraphScopes);

    public async Task<string?> GetAccessTokenAsync(bool interactive, CancellationToken cancellationToken = default)
    {
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            var token = await BrowserAuthInterop.GetAccessTokenAsync(AppConfig.ClientId, ScopeText);
            if (!string.IsNullOrWhiteSpace(token) || !interactive)
                return string.IsNullOrWhiteSpace(token) ? null : token;

            // Only an explicit interactive request may start a redirect. The callback is
            // consumed by getAccessToken() exactly once after the WASM app restarts.
            token = await BrowserAuthInterop.LoginAsync(AppConfig.ClientId, ScopeText);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    public bool TryHandleProtocolActivation(Uri uri) => false;

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await BrowserAuthInterop.LogoutAsync();
    }
}

[SupportedOSPlatform("browser")]
internal static partial class BrowserAuthInterop
{
    [JSImport("getAccessToken", "Hello1DriveAuth")]
    internal static partial Task<string> GetAccessTokenAsync(string clientId, string scopes);

    [JSImport("login", "Hello1DriveAuth")]
    internal static partial Task<string> LoginAsync(string clientId, string scopes);

    [JSImport("logout", "Hello1DriveAuth")]
    internal static partial Task LogoutAsync();
}
