using System.Runtime.InteropServices;
using Hello1Drive.Services;

namespace Hello1Drive.Desktop.Services;

internal sealed class DesktopInputSettingsService : IDesktopInputSettingsService
{
    private const uint SpiGetWheelScrollLines = 0x0068;
    private const int DefaultWindowsWheelLines = 3;

    // Cache the Windows preference during desktop service initialization. Mouse-wheel input itself
    // never synchronously crosses the user32 boundary, so the first wheel frame stays input-only.
    private readonly int _wheelLines = ReadWheelLines();

    public int GetMouseWheelScrollLines() => _wheelLines;

    private static int ReadWheelLines()
    {
        if (!OperatingSystem.IsWindows())
            return DesktopScrollSettings.UseFrameworkDefault;

        if (!SystemParametersInfo(SpiGetWheelScrollLines, 0, out var lines, 0))
            return DefaultWindowsWheelLines;

        return lines == uint.MaxValue
            ? DesktopScrollSettings.ScrollByPage
            : (int)Math.Min(lines, 100u);
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out uint pvParam, uint fWinIni);
}
