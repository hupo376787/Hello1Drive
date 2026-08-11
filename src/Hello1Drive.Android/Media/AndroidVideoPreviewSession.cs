using Avalonia.Controls;
using Hello1Drive.Services;

namespace Hello1Drive.Android.Media;

internal sealed class AndroidVideoPreviewSession : IEmbeddedMediaPlayerSession, IEmbeddedMediaOverlayController
{
    private readonly AndroidVideoPlayerControl _view;
    private bool _disposed;

    public AndroidVideoPreviewSession(string localFilePath)
    {
        _view = new AndroidVideoPlayerControl(localFilePath);
    }

    public Control View => _view;

    public void SetNativeOverlayVisible(bool visible)
    {
        if (!_disposed)
            _view.SetNativeOverlayVisible(visible);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _view.Shutdown();
    }
}
