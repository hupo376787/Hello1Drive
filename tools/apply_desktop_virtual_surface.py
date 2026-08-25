from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one match, got {count}")
    return text.replace(old, new, 1)


# 1) The common desktop details header must collapse in icon views.
xaml_path = ROOT / "src/Hello1Drive.Core/Views/MainView.axaml"
xaml = xaml_path.read_text(encoding="utf-8")
xaml = replace_once(
    xaml,
    '<Border x:Name="DesktopDetailsHeaderBorder" Grid.Row="0" Padding="{OnFormFactor Desktop=\'8,4\', Mobile=\'6,4\'}"',
    '<Border x:Name="DesktopDetailsHeaderBorder" Grid.Row="0" IsVisible="{Binding IsDetailsView}" Padding="{OnFormFactor Desktop=\'8,4\', Mobile=\'6,4\'}"',
    "desktop details header visibility",
)
xaml_path.write_text(xaml, encoding="utf-8")


# 2) ScrollViewer.Viewport already excludes its vertical scrollbar. Do not reserve another 18px.
#    Keep the FormattedText cache bounded to several retained scenes instead of thousands of rows.
surface_path = ROOT / "src/Hello1Drive.Core/Controls/DesktopVirtualFileSurface.cs"
surface = surface_path.read_text(encoding="utf-8")
surface = replace_once(
    surface,
    "        var usable = Math.Max(minWidth, width - 18);",
    "        var usable = Math.Max(minWidth, width);",
    "desktop grid usable width",
)
surface = replace_once(
    surface,
    "            while (_textCacheOrder.Count > 6000)",
    "            while (_textCacheOrder.Count > 2048)",
    "desktop text cache bound",
)

# The base one-shot workflow still contains its two compile-fix post-processors. Recreate the
# exact pre-fix local briefly so that workflow can remain deterministic and put it back again.
surface = replace_once(
    surface,
    "            var detailIndex = (int)Math.Floor(point.Y / DetailsRowHeight);\n            return detailIndex >= 0 && detailIndex < vm.VirtualItems.Count ? detailIndex : -1;",
    "            var index = (int)Math.Floor(point.Y / DetailsRowHeight);\n            return index >= 0 && index < vm.VirtualItems.Count ? index : -1;",
    "workflow detail-index handoff",
)
surface_path.write_text(surface, encoding="utf-8")


# 3) A single desktop ScrollViewer must not carry a pixel offset from one layout geometry into
#    another. Reset to the top whenever the desktop view mode changes, then remeasure and prefetch.
view_path = ROOT / "src/Hello1Drive.Core/Views/MainView.axaml.cs"
view = view_path.read_text(encoding="utf-8")
old = '''        if (e.PropertyName == nameof(MainViewModel.ViewMode) && IsMobilePlatform)
        {
            if (UsesNativeMobileFileList)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateNativeMobileFileListGeometry(vm);
                    _nativeMobileFileListHost?.RefreshNativePresentation();
                }, DispatcherPriority.Loaded);
                return;
            }

            _lastMobileScrollViewer = null;
            Dispatcher.UIThread.Post(() =>
            {
                var scroll = GetActiveScrollViewer(vm);
                if (scroll is null)
                    return;
                _lastMobileScrollViewer = scroll;
                QueueVisibleMobileThumbnails(scroll, vm);
            }, DispatcherPriority.Loaded);
            return;
        }'''
new = '''        if (e.PropertyName == nameof(MainViewModel.ViewMode))
        {
            if (!IsMobilePlatform)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    DesktopVirtualScrollViewer.Offset = new Vector(0, 0);
                    DesktopFileSurface.InvalidateMeasure();
                    SyncDesktopVirtualSurfaceViewport(DesktopVirtualScrollViewer);
                    if (!vm.IsDesktopListScrolling)
                        QueueRealizedDesktopThumbnails(DesktopVirtualScrollViewer, vm, allowNetwork: true);
                }, DispatcherPriority.Loaded);
                return;
            }

            if (UsesNativeMobileFileList)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateNativeMobileFileListGeometry(vm);
                    _nativeMobileFileListHost?.RefreshNativePresentation();
                }, DispatcherPriority.Loaded);
                return;
            }

            _lastMobileScrollViewer = null;
            Dispatcher.UIThread.Post(() =>
            {
                var scroll = GetActiveScrollViewer(vm);
                if (scroll is null)
                    return;
                _lastMobileScrollViewer = scroll;
                QueueVisibleMobileThumbnails(scroll, vm);
            }, DispatcherPriority.Loaded);
            return;
        }'''
view = replace_once(view, old, new, "desktop view-mode geometry reset")

# Same compatibility handoff for the base one-shot workflow's stale-semicolon cleanup.
marker = "\n\n    private static bool ShouldSuppressMarqueeStart"
if marker not in view:
    raise RuntimeError("workflow semicolon handoff marker not found")
view = view.replace(marker, "\n;\n\n    private static bool ShouldSuppressMarqueeStart", 1)
view_path.write_text(view, encoding="utf-8")
