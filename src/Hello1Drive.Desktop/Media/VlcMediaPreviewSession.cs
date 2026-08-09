using Avalonia.Controls;
using Hello1Drive.Services;
using LibVLCSharp.Shared;

namespace Hello1Drive.Desktop.Media;

internal sealed class VlcMediaPreviewSession : IEmbeddedMediaPlayerSession
{
    private readonly LibVLCSharp.Shared.MediaPlayer _mediaPlayer;
    private readonly LibVLCSharp.Shared.Media _media;
    private readonly EmbeddedMediaPlayerControl _view;
    private bool _disposed;

    public VlcMediaPreviewSession(LibVLC libVlc, string localFilePath)
    {
        _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(libVlc)
        {
            Volume = 80
        };
        _media = new LibVLCSharp.Shared.Media(libVlc, localFilePath, FromType.FromPath);
        _view = new EmbeddedMediaPlayerControl(_mediaPlayer, _media, Path.GetFileName(localFilePath));
    }

    public Control View => _view;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _view.Shutdown();
        try { _mediaPlayer.Stop(); } catch { }
        _media.Dispose();
        _mediaPlayer.Dispose();
    }
}
