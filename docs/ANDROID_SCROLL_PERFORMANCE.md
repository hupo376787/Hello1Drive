# Android native file-list performance notes

## Android uses RecyclerView, not Avalonia ItemsRepeater

The Android phone file surface is now platform-native. `NativeMobileFileListHost` is an Avalonia `NativeControlHost`, while `Hello1Drive.Android` creates an AndroidX `SwipeRefreshLayout` containing a native AndroidX `RecyclerView`.

The old Avalonia mobile `ScrollViewer + ItemsRepeater` surfaces remain only as the iOS fallback. Desktop continues to use the Avalonia `ItemsRepeater` implementation.

This split is deliberate: on Android, scroll latency and fling smoothness take priority over sharing the final rendering layer with desktop.

## Fixed logical slots, bounded native views

Folder metadata still uses the same fixed `VirtualDriveItemSlot` model:

- when `folder.childCount` is known, the logical slot count is established immediately;
- local metadata index data can fill slots before cloud synchronization finishes;
- Graph continuation/delta work fills existing slots in the background;
- scrolling never triggers Graph pagination.

A folder containing 1,000 or 5,000 entries therefore has 1,000/5,000 lightweight logical slots, but RecyclerView only creates/binds a small set of native item views for the current viewport plus its recycle/cache pool.

Stable RecyclerView IDs are based on the logical slot position, not on whether metadata has arrived yet. A placeholder becoming a real file therefore does not change its RecyclerView identity.

## One native View per realized cell

Each realized file cell is a single custom Android `View`. It draws its thumbnail, folder/file badge, filename, size, selection affordance and placeholder directly to an Android `Canvas`.

There is no nested Android `TextView/ImageView` tree per item and no Avalonia binding/layout/rendering in the list hot path. The cell only requests a new measure when the view mode changes; normal RecyclerView rebinding during a fling only updates data and invalidates drawing.

RecyclerView has fixed-size optimization enabled, item animations disabled and an enlarged item-view cache. Details mode uses a native `LinearLayoutManager`; large and extra-large modes use native `GridLayoutManager`.

## Native thumbnail pipeline

Android list thumbnails no longer create Avalonia `Bitmap` objects.

- the native adapter owns list-thumbnail scheduling;
- scrolling/settling immediately cancels the previous thumbnail generation;
- network thumbnail work waits until RecyclerView is idle;
- only currently visible native holders become thumbnail candidates;
- up to four native thumbnail workers run concurrently;
- encoded thumbnails still use the shared persistent `ThumbnailCacheService`;
- decode uses Android `BitmapFactory` on worker threads;
- decoded Android bitmaps use a bounded native LRU;
- the holder/item ID is rechecked before a bitmap is applied.

Visible placeholder holders subscribe only to their own `VirtualDriveItemSlot`. When background metadata reaches that index, only that realized holder rebinds; the app does not walk or redraw thousands of hidden items.

## Selection stays O(selected)

Android selection state is maintained by item ID and a small selected-item dictionary. A tap/long-press updates only selected items and currently realized RecyclerView cells. It does not iterate every loaded file in a large folder.

Native long-press enters selection mode and uses Android haptic feedback. Pull-to-refresh is also native through `SwipeRefreshLayout`.

## NativeControlHost airspace rule

Android native views are hosted in a separate platform surface. Avalonia overlays cannot reliably render above that surface, and Avalonia `RenderTransform` cannot move the native child the same way as a normal Avalonia visual.

For correctness and performance:

- the Android file header/toolbars remain fixed rather than collapsing with the list fling;
- the native list is hidden while Avalonia full-screen/transient overlays are active (preview, settings, transfer page, profile, destination picker, sort/actions, confirmations, busy/prompt surfaces);
- the file-list region uses an opaque native surface instead of trying to composite an Avalonia wallpaper through every native cell.

This is an intentional platform-specific tradeoff in favor of predictable 60/120 Hz scrolling behavior.

## Desktop and iOS

Desktop remains on Avalonia `ItemsRepeater` with fixed logical slots, realized-viewport thumbnail priority and local-index/Graph background synchronization.

The iOS mobile fallback still uses the previous Avalonia `ItemsRepeater` path. The current native rewrite targets Android, including the Redmi K80 test device.
