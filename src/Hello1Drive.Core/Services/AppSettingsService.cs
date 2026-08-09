using System.Text.Json;
using Hello1Drive.Models;

namespace Hello1Drive.Services;

public sealed class AppSettingsService
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public AppSettings Current { get; private set; } = new();
    public string SettingsPath { get; }

    public AppSettingsService()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;

        var directory = Path.Combine(root, "Hello1Drive");
        Directory.CreateDirectory(directory);
        SettingsPath = Path.Combine(directory, "settings.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                Current = new AppSettings();
                return;
            }

            var json = File.ReadAllText(SettingsPath);
            Current = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
            Normalize();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            Normalize();
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await using var stream = File.Create(SettingsPath);
            await JsonSerializer.SerializeAsync(stream, Current, _jsonOptions, cancellationToken);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void Normalize()
    {
        if (double.IsNaN(Current.BackgroundIntervalMinutes) ||
            double.IsInfinity(Current.BackgroundIntervalMinutes) ||
            Current.BackgroundIntervalMinutes < 0.1)
        {
            Current.BackgroundIntervalMinutes = 0.1;
        }

        Current.AcrylicBlurPercent = double.IsFinite(Current.AcrylicBlurPercent)
            ? Math.Clamp(Current.AcrylicBlurPercent, 0, 100)
            : 50;

        Current.LastFolderBreadcrumbs ??= [];
        Current.SlideshowIntervalSeconds = NormalizePositive(Current.SlideshowIntervalSeconds, 5, 1, 3600);
        Current.DownloadSpeedLimitKBps = NormalizePositive(Current.DownloadSpeedLimitKBps, 1024, 1, 1024 * 1024);
        Current.UploadSpeedLimitKBps = NormalizePositive(Current.UploadSpeedLimitKBps, 1024, 1, 1024 * 1024);
        Current.FloatingUploadX = double.IsFinite(Current.FloatingUploadX)
            ? Math.Clamp(Current.FloatingUploadX, 0, 1)
            : 0.94;
        Current.FloatingUploadY = double.IsFinite(Current.FloatingUploadY)
            ? Math.Clamp(Current.FloatingUploadY, 0, 1)
            : 0.90;
    }

    private static double NormalizePositive(double value, double fallback, double min, double max)
    {
        if (!double.IsFinite(value) || value <= 0)
            return fallback;
        return Math.Clamp(value, min, max);
    }
}
