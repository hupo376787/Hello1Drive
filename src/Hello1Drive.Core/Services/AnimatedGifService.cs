using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;

namespace Hello1Drive.Services;

public sealed class AnimatedGifData : IDisposable
{
    public List<AvaloniaBitmap> Frames { get; } = [];
    public List<TimeSpan> Delays { get; } = [];

    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }

    public void Dispose()
    {
        foreach (var frame in Frames)
            frame.Dispose();
        Frames.Clear();
        Delays.Clear();
    }
}

/// <summary>
/// Decodes animated GIFs into Avalonia bitmaps. ImageSharp performs the GIF frame
/// composition cross-platform; Avalonia only has to swap the already-rendered frames.
/// </summary>
public static class AnimatedGifService
{
    private const int MaxFrames = 300;

    public static async Task<AnimatedGifData?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
            return null;

        using var image = await Image.LoadAsync<Rgba32>(path, cancellationToken);
        if (image.Frames.Count <= 1)
            return null;

        var result = new AnimatedGifData
        {
            PixelWidth = image.Width,
            PixelHeight = image.Height
        };

        try
        {
            var count = Math.Min(image.Frames.Count, MaxFrames);
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var metadata = image.Frames[i].Metadata.GetGifMetadata();
                // GIF delays are hundredths of a second. Browsers commonly clamp tiny
                // values, and doing the same avoids a busy 0 ms dispatcher timer.
                var delayMs = Math.Max(20, metadata.FrameDelay * 10);

                using var frameImage = image.Frames.CloneFrame(i);
                await using var encoded = new MemoryStream();
                await frameImage.SaveAsPngAsync(encoded, cancellationToken);
                encoded.Position = 0;

                result.Frames.Add(new AvaloniaBitmap(encoded));
                result.Delays.Add(TimeSpan.FromMilliseconds(delayMs));
            }

            return result.Frames.Count > 1 ? result : null;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }
}
