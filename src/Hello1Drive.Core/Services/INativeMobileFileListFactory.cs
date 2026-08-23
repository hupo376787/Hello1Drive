using Avalonia.Platform;
using Hello1Drive.Controls;

namespace Hello1Drive.Services;

/// <summary>
/// Platform bridge for the high-performance phone file surface.
/// Android provides RecyclerView and iOS provides UICollectionView; other heads can leave this
/// unconfigured and continue using the Avalonia fallback surface.
/// </summary>
public interface INativeMobileFileListFactory
{
    IPlatformHandle CreateControl(IPlatformHandle parent, NativeMobileFileListHost host);

    void DestroyControl(IPlatformHandle control);
}
