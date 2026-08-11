using Microsoft.Win32;
using Hello1Drive.Services;

namespace Hello1Drive.Desktop.Services;

internal sealed class WindowsStartupRegistrationService : IStartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Hello1Drive";
    private const string StartupArgument = "--tray";

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsEnabled
    {
        get
        {
            if (!IsSupported)
                return false;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                var command = key?.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(command) &&
                       command.Contains(StartupArgument, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("开机启动目前仅支持 Windows。" );

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 当前用户启动项。" );

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = ResolveExecutablePath();
        var command = $"\"{executablePath}\" {StartupArgument}";
        if (command.Length > 260)
            throw new InvalidOperationException("程序路径过长，无法写入 Windows Run 启动项。" );

        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static string ResolveExecutablePath()
    {
        // Published and normal Visual Studio runs use the apphost executable. Prefer the
        // predictable apphost path instead of a possible dotnet host process path.
        var appHost = Path.Combine(AppContext.BaseDirectory, "Hello1Drive.exe");
        if (File.Exists(appHost))
            return Path.GetFullPath(appHost);

        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) &&
            string.Equals(Path.GetExtension(Environment.ProcessPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Environment.ProcessPath);
        }

        throw new InvalidOperationException("无法确定 Hello1Drive.exe 的路径。请发布为 Windows 桌面应用后再启用开机启动。" );
    }
}
