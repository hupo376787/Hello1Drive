namespace Hello1Drive.Services;

/// <summary>
/// Optional platform service for registering the desktop app to start when the user signs in.
/// </summary>
public interface IStartupRegistrationService
{
    bool IsSupported { get; }
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}
