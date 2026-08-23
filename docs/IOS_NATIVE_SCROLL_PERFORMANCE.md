# iOS native file-list performance path

The iOS main file surface uses UIKit `UICollectionView` hosted through Avalonia `NativeControlHost`.
The Avalonia `ItemsRepeater` mobile fallback is not active on iOS when the native factory is configured.

Performance rules:

- The existing `VirtualDriveItemSlot` collection supplies logical item count and metadata, but UIKit only creates/reuses visible collection cells.
- Detail view uses fixed-height native rows; large and extra-large modes use `UICollectionViewFlowLayout` grids.
- A fling cancels thumbnail generation for the old viewport.
- No network thumbnail request is started while the collection is actively dragging/decelerating.
- Visible thumbnails restart shortly after scrolling becomes idle.
- Thumbnail decode uses native `UIImage`; the native memory cache is bounded to 96 entries.
- Pull-to-refresh uses `UIRefreshControl`.
- Native cell tap/long-press and selection state bridge back to `NativeMobileFileListHost` so app behavior remains shared with Android/Core.

The native folder glyph is intentionally identical to the original Avalonia folder path/colors.
