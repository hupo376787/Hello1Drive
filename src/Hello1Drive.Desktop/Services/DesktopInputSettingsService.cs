using System.Runtime.InteropServices;
using Hello1Drive.Services;

namespace Hello1Drive.Desktop.Services;

internal sealed class DesktopInputSettingsService : IDesktopInputSettingsService
{
    private const uint SpiGetWheelScrollLines = 0x0068;
    private const int DefaultWindowsWheelLines = 3;

    // Read the Windows preference once while the desktop services are created. Mouse-wheel input
    // must never synchronously cross the user32 boundary after the user has started scrolling.
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
