using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;

namespace Hello1Drive.Services;

internal sealed class TransferRateLimiter
{
    private readonly long? _bytesPerSecond;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _bytes;

    public TransferRateLimiter(long? bytesPerSecond)
    {
        _bytesPerSecond = bytesPerSecond is > 0 ? bytesPerSecond : null;
    }

    public async ValueTask ThrottleAsync(int transferredBytes, CancellationToken cancellationToken)
    {
        if (_bytesPerSecond is not > 0 || transferredBytes <= 0)
            return;

        _bytes += transferredBytes;
        var expected = TimeSpan.FromSeconds((double)_bytes / _bytesPerSecond.Value);
        var delay = expected - _clock.Elapsed;
        if (delay > TimeSpan.FromMilliseconds(2))
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    public void Throttle(int transferredBytes)
    {
        if (_bytesPerSecond is not > 0 || transferredBytes <= 0)
            return;

        _bytes += transferredBytes;
        var expected = TimeSpan.FromSeconds((double)_bytes / _bytesPerSecond.Value);
        var delay = expected - _clock.Elapsed;
        if (delay > TimeSpan.FromMilliseconds(2))
            Thread.Sleep(delay);
    }
}

internal sealed class RateLimitedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly TransferRateLimiter _limiter;
    private readonly IProgress<double>? _progress;
    private readonly long? _length;
    private long _read;

    public RateLimitedReadStream(Stream inner, long? bytesPerSecond, IProgress<double>? progress = null)
    {
        _inner = inner;
        _limiter = new TransferRateLimiter(bytesPerSecond);
        _progress = progress;
        _length = inner.CanSeek ? inner.Length : null;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        _limiter.Throttle(read);
        Report(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        await _limiter.ThrottleAsync(read, cancellationToken);
        Report(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        await _limiter.ThrottleAsync(read, cancellationToken);
        Report(read);
        return read;
    }

    private void Report(int bytes)
    {
        if (bytes <= 0)
            return;
        _read += bytes;
        if (_length is > 0)
            _progress?.Report(Math.Clamp((double)_read / _length.Value, 0, 1));
    }

    protected override void Dispose(bool disposing)
    {
        // The caller owns the source stream. StreamContent disposes this wrapper, not the source.
        base.Dispose(disposing);
    }
}


internal sealed class ProgressStreamContent : HttpContent
{
    private const int BufferSize = 128 * 1024;

    private readonly Stream _source;
    private readonly TransferRateLimiter _limiter;
    private readonly IProgress<double>? _progress;
    private readonly long? _length;
    private readonly long _startPosition;

    public ProgressStreamContent(
        Stream source,
        long? bytesPerSecond,
        IProgress<double>? progress,
        string contentType)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _limiter = new TransferRateLimiter(bytesPerSecond);
        _progress = progress;
        _startPosition = source.CanSeek ? source.Position : 0;
        _length = source.CanSeek ? Math.Max(0, source.Length - _startPosition) : null;
        Headers.ContentType = new MediaTypeHeaderValue(contentType);
    }

    protected override bool TryComputeLength(out long length)
    {
        if (_length is long known)
        {
            length = known;
            return true;
        }

        length = 0;
        return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeCoreAsync(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken) =>
        SerializeCoreAsync(stream, cancellationToken);

    private async Task SerializeCoreAsync(Stream destination, CancellationToken cancellationToken)
    {
        if (_source.CanSeek)
            _source.Position = _startPosition;

        var buffer = new byte[BufferSize];
        long sent = 0;

        while (true)
        {
            var read = await _source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            sent += read;

            if (_length is > 0)
                _progress?.Report(Math.Clamp((double)sent / _length.Value, 0, 1));

            await _limiter.ThrottleAsync(read, cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        _progress?.Report(1.0);
    }

    protected override void Dispose(bool disposing)
    {
        // The caller owns _source; disposing HttpContent must not close a picker stream
        // that is also retained for retry handling by the transfer queue.
        base.Dispose(disposing);
    }
}


internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public InlineProgress(Action<T> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public void Report(T value) => _handler(value);
}
