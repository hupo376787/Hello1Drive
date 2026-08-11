using Hello1Drive.Services;

namespace Hello1Drive.Android.Media;

/// <summary>
/// Android in-app media backend. The actual player is Android's native VideoView,
/// embedded into Avalonia through NativeControlHost.
/// </summary>
public sealed class AndroidEmbeddedMediaPlayerFactory : IEmbeddedMediaPlayerFactory
{
    public IEmbeddedMediaPlayerSession? TryCreate(string localFilePath)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
            return null;

        return new AndroidVideoPreviewSession(localFilePath);
    }
}
