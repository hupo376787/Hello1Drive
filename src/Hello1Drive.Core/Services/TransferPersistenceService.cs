using System.Text.Json;
using Hello1Drive.Models;

namespace Hello1Drive.Services;

public sealed class TransferPersistenceService
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string TransferStatePath { get; }

    public TransferPersistenceService()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;

        var directory = Path.Combine(root, "Hello1Drive");
        Directory.CreateDirectory(directory);
        TransferStatePath = Path.Combine(directory, "transfers.json");
    }

    public IReadOnlyList<PersistedTransferRecord> Load()
    {
        try
        {
            if (!File.Exists(TransferStatePath))
                return [];

            var json = File.ReadAllText(TransferStatePath);
            // Completed rows are session history only. They stay visible while the app is
            // running, but must not come back after the next launch.
            return (JsonSerializer.Deserialize<List<PersistedTransferRecord>>(json, _jsonOptions) ?? [])
                .Where(static x => x.State != TransferState.Completed)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public async Task SaveAsync(IEnumerable<TransferItemModel> transfers, CancellationToken cancellationToken = default)
    {
        // Do not persist completed transfers. This keeps the current-session history visible,
        // while reopening Hello1Drive starts without rows that already finished successfully.
        var snapshot = transfers
            .Where(static x => x.State != TransferState.Completed)
            .Select(PersistedTransferRecord.FromModel)
            .ToArray();

        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(TransferStatePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            if (snapshot.Length == 0)
            {
                if (File.Exists(TransferStatePath))
                    File.Delete(TransferStatePath);
                return;
            }

            var temporaryPath = TransferStatePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, _jsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, TransferStatePath, true);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}

public sealed class PersistedTransferRecord
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public TransferDirection Direction { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public double Progress { get; set; }
    public TransferState State { get; set; }
    public string Message { get; set; } = string.Empty;
    public TransferResumeInfo? ResumeInfo { get; set; }

    public static PersistedTransferRecord FromModel(TransferItemModel item) => new()
    {
        Id = item.Id,
        FileName = item.FileName,
        Direction = item.Direction,
        StartedAt = item.StartedAt,
        Progress = item.Progress,
        State = item.State,
        Message = item.Message,
        ResumeInfo = item.ResumeInfo
    };
}
