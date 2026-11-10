namespace Hello1Drive.Services;

/// <summary>
/// Gives content-provider streams a known length and a stable position for HTTP framing and
/// resumable retries. Android providers may return pipes which advertise a file channel but
/// cannot actually seek (or report a zero length), so always stage picker input on Android.
/// </summary>
internal sealed class PreparedUploadSource(Stream stream, bool ownsStream, string? checksum = null) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;
    public string? ContentChecksum { get; } = checksum;

    public static async Task<PreparedUploadSource> PrepareAsync(
        Stream source, bool forceCopy, CancellationToken cancellationToken)
    {
        if (!forceCopy && source.CanSeek)
        {
            try
            {
                _ = source.Length;
                var position = source.Position;
                source.Position = position;
                return new PreparedUploadSource(source, ownsStream: false);
            }
            catch (NotSupportedException) { }
            catch (IOException) { }
        }

        var path = Path.Combine(Path.GetTempPath(), $"hello1drive-upload-{Guid.NewGuid():N}.tmp");
        var staged = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        try
        {
            var hash = new OneDriveContentHash();
            var buffer = new byte[128 * 1024];
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                hash.Append(buffer.AsSpan(0, count));
                await staged.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            await staged.FlushAsync(cancellationToken).ConfigureAwait(false);
            staged.Position = 0;
            return new PreparedUploadSource(staged, ownsStream: true, hash.GetValue());
        }
        catch
        {
            await staged.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => ownsStream ? Stream.DisposeAsync() : ValueTask.CompletedTask;
}
