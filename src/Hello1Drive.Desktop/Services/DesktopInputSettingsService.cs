using System.Runtime.InteropServices;
using Hello1Drive.Services;

namespace Hello1Drive.Desktop.Services;

internal sealed class DesktopInputSettingsService : IDesktopInputSettingsService
{
    private const uint SpiGetWheelScrollLines = 0x0068;
    private const int DefaultWindowsWheelLines = 3;
    private const long RefreshIntervalMilliseconds = 1000;

    private int _cachedWheelLines = DefaultWindowsWheelLines;
    private long _lastRefreshTick = long.MinValue;

    public int GetMouseWheelScrollLines()
    {
        if (!OperatingSystem.IsWindows())
            return DesktopScrollSettings.UseFrameworkDefault;

        var now = Environment.TickCount64;
        if (_lastRefreshTick != long.MinValue && now - _lastRefreshTick < RefreshIntervalMilliseconds)
            return _cachedWheelLines;

        _lastRefreshTick = now;
        if (!SystemParametersInfo(SpiGetWheelScrollLines, 0, out var lines, 0))
            return _cachedWheelLines;

        _cachedWheelLines = lines == uint.MaxValue
            ? DesktopScrollSettings.ScrollByPage
            : (int)Math.Min(lines, 100u);
        return _cachedWheelLines;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out uint pvParam, uint fWinIni);
}
