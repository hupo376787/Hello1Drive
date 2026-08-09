namespace Hello1Drive.Services;

public interface IAuthenticationService
{
    Task<string?> GetAccessTokenAsync(bool interactive, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets a platform authentication implementation consume a custom URI activation
    /// (for example the iOS MSAL callback under Avalonia 12 scene lifecycle).
    /// </summary>
    bool TryHandleProtocolActivation(Uri uri);
}
