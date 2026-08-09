using Hello1Drive.Services;
using LibVLCSharp.Shared;

namespace Hello1Drive.Desktop.Media;

/// <summary>
/// Free/open-source desktop media backend based on LibVLCSharp.
/// One LibVLC instance is shared for the application lifetime, as recommended
/// by the LibVLCSharp maintainers.
/// </summary>
public sealed class DesktopEmbeddedMediaPlayerFactory : IEmbeddedMediaPlayerFactory
{
    private readonly object _gate = new();
    private LibVLC? _libVlc;
    private bool _initializationFailed;

    public IEmbeddedMediaPlayerSession? TryCreate(string localFilePath)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
            return null;

        try
        {
            var libVlc = GetOrCreateLibVlc();
            return libVlc is null ? null : new VlcMediaPreviewSession(libVlc, localFilePath);
        }
        catch
        {
            return null;
        }
    }

    private LibVLC? GetOrCreateLibVlc()
    {
        lock (_gate)
        {
            if (_initializationFailed)
                return null;
            if (_libVlc is not null)
                return _libVlc;

            try
            {
                LibVLCSharp.Shared.Core.Initialize();
                _libVlc = new LibVLC("--no-video-title-show", "--quiet");
                return _libVlc;
            }
            catch
            {
                _initializationFailed = true;
                return null;
            }
        }
    }
}
