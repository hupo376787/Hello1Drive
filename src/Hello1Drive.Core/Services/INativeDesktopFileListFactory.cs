using Avalonia.Platform;
using Hello1Drive.Controls;

namespace Hello1Drive.Services;

/// <summary>
/// Platform bridge for a native desktop file list. Windows provides a Win32 ListView-backed
/// implementation; unsupported desktop platforms leave this unconfigured and keep the Avalonia
/// virtual surface as a fallback.
/// </summary>
public interface INativeDesktopFileListFactory
{
    IPlatformHandle CreateControl(IPlatformHandle parent, NativeDesktopFileListHost host);

    void DestroyControl(IPlatformHandle control);
}
