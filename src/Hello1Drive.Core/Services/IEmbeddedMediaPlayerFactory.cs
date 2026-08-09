using Avalonia.Controls;

namespace Hello1Drive.Services;

/// <summary>
/// Optional platform integration for an in-app media player. Platform heads may
/// register an implementation; Core remains usable on targets without one.
/// </summary>
public interface IEmbeddedMediaPlayerFactory
{
    IEmbeddedMediaPlayerSession? TryCreate(string localFilePath);
}

public interface IEmbeddedMediaPlayerSession : IDisposable
{
    Control View { get; }
}
