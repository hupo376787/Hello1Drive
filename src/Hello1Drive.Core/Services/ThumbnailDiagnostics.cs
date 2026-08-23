using System.Collections.Concurrent;
using System.Text;

namespace Hello1Drive.Services;

/// <summary>
/// Lightweight, release-build-safe diagnostics for the mobile thumbnail pipeline.
/// Calls are intentionally non-blocking on the UI/scroll thread: lines are queued in memory
/// and a single background writer appends them to a small rolling text file.
/// </summary>
public static class ThumbnailDiagnostics
{
    private const long MaxLogBytes = 2L * 1024 * 1024;
    private const int MaxQueuedLines = 6000;
    private const int MaxCopiedBytes = 420 * 1024;

    private static readonly ConcurrentQueue<string> PendingLines = new();
    private static readonly SemaphoreSlim PendingSignal = new(0);
    private static readonly SemaphoreSlim FileGate = new(1, 1);
    private static readonly string _logPath = ResolveLogPath();
    private static int _queuedLineCount;
    private static int _writerStarted;

    public static string LogPath => _logPath;

    public static void Log(string category, string message)
    {
        try
        {
            if (Interlocked.Increment(ref _queuedLineCount) > MaxQueuedLines)
            {
                Interlocked.Decrement(ref _queuedLineCount);
                return;
            }

            var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId:00}] {category,-9} {message}";
            PendingLines.Enqueue(line);
            EnsureWriterStarted();
            PendingSignal.Release();
        }
        catch
        {
            // Diagnostics must never affect scrolling or thumbnail loading.
        }
    }

    public static void LogItem(string category, string action, string itemId, string? itemName, string details = "")
    {
        var shortId = string.IsNullOrWhiteSpace(itemId)
            ? "-"
            : itemId.Length <= 12 ? itemId : itemId[^12..];
        var safeName = (itemName ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        if (safeName.Length > 48)
            safeName = safeName[..45] + "...";

        Log(category, $"{action} id=…{shortId} name=\"{safeName}\"{(string.IsNullOrWhiteSpace(details) ? string.Empty : " " + details)}");
    }

    public static async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        EnsureWriterStarted();
        for (var i = 0; i < 30 && Volatile.Read(ref _queuedLineCount) > 0; i++)
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);

        await FileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileGate.Release();
    }

    public static async Task<string> ReadForClipboardAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_logPath))
                return $"Thumbnail diagnostics log is empty.\nPath: {_logPath}";

            await FileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var take = (int)Math.Min(stream.Length, MaxCopiedBytes);
                stream.Seek(-take, SeekOrigin.End);
                var bytes = new byte[take];
                var read = 0;
                while (read < take)
                {
                    var n = await stream.ReadAsync(bytes.AsMemory(read, take - read), cancellationToken).ConfigureAwait(false);
                    if (n <= 0)
                        break;
                    read += n;
                }

                var body = Encoding.UTF8.GetString(bytes, 0, read);
                var firstNewLine = body.IndexOf('\n');
                if (stream.Length > MaxCopiedBytes && firstNewLine >= 0)
                    body = body[(firstNewLine + 1)..];

                return $"Hello1Drive thumbnail diagnostics\nPath: {_logPath}\n--- latest log ---\n{body}";
            }
            finally
            {
                FileGate.Release();
            }
        }
        catch (Exception ex)
        {
            return $"Unable to read thumbnail diagnostics: {ex.GetType().Name}: {ex.Message}\nPath: {_logPath}";
        }
    }

    public static async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        while (PendingLines.TryDequeue(out _))
            Interlocked.Decrement(ref _queuedLineCount);

        await FileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_logPath))
                File.Delete(_logPath);
            var previous = _logPath + ".1";
            if (File.Exists(previous))
                File.Delete(previous);
        }
        catch
        {
            // Best effort only.
        }
        finally
        {
            FileGate.Release();
        }

        Log("DIAG", "log cleared");
    }

    private static void EnsureWriterStarted()
    {
        if (Interlocked.Exchange(ref _writerStarted, 1) != 0)
            return;

        _ = Task.Run(WriterLoopAsync);
    }

    private static async Task WriterLoopAsync()
    {
        var batch = new List<string>(128);
        while (true)
        {
            try
            {
                await PendingSignal.WaitAsync().ConfigureAwait(false);
                batch.Clear();
                while (batch.Count < 128 && PendingLines.TryDequeue(out var line))
                {
                    Interlocked.Decrement(ref _queuedLineCount);
                    batch.Add(line);
                }

                if (batch.Count == 0)
                    continue;

                await FileGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var directory = Path.GetDirectoryName(_logPath);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);

                    RollIfNeeded();
                    await using var writer = new StreamWriter(_logPath, append: true, Encoding.UTF8);
                    foreach (var line in batch)
                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
                finally
                {
                    FileGate.Release();
                }
            }
            catch
            {
                // Keep the writer alive after storage errors; a later run may become writable.
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }

    private static void RollIfNeeded()
    {
        try
        {
            if (!File.Exists(_logPath) || new FileInfo(_logPath).Length < MaxLogBytes)
                return;

            var previous = _logPath + ".1";
            if (File.Exists(previous))
                File.Delete(previous);
            File.Move(_logPath, previous);
        }
        catch
        {
            // Rolling is optional; append can still continue.
        }
    }

    private static string ResolveLogPath()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = Path.GetTempPath();
            return Path.Combine(root, "Hello1Drive", "logs", "thumbnail-diagnostics.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "Hello1Drive", "logs", "thumbnail-diagnostics.log");
        }
    }
}
