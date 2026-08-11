# Android scrolling / thumbnail performance notes

## What was causing placeholder flashes

The mobile list previously disposed `DriveItemModel.ThumbnailImage` as soon as an item left a small viewport window. When the user scrolled back, the model therefore had no decoded bitmap even when the encoded thumbnail was already present in the persistent disk cache. At the same time thumbnail decoding was intentionally paused while `_mobileListScrolling` was true, so a placeholder was guaranteed until the fling became idle.

The current implementation changes that behavior:

- decoded mobile thumbnails are kept in a bounded 360-entry LRU;
- items that return while still in the LRU draw immediately;
- when an item is no longer in memory but its encoded thumbnail is on disk, the disk hit is allowed to decode during the fling;
- network thumbnail work still waits for an idle/near-visible request path rather than flooding a fast fling;
- thumbnail decode is performed on a worker task and only the final `ThumbnailImage` property swap is dispatched to the UI thread;
- current-folder membership is checked with an O(1) item-id `HashSet` instead of `List.Contains` for every thumbnail completion;
- new thumbnail cache files include a version hash in the filename so cache validation can normally be a single `File.Exists()` instead of reading/parsing metadata JSON for every recycled item.

## Why Avalonia still may not match RecyclerView on Android

Hello1Drive currently renders its mobile list with Avalonia `ItemsRepeater` inside an Avalonia `ScrollViewer`. Virtualization keeps the number of realized controls bounded, but the realized item templates, layout, bindings, input handling and Skia rendering still run through Avalonia's rendering pipeline.

The separate `Avalonia.Controls.ItemsRepeater` repository is retired and recommends built-in Avalonia item controls when possible. Hello1Drive currently uses `Avalonia.Controls.ItemsRepeater 12.0.0` together with Avalonia 12.1.0.

AndroidX `RecyclerView` has a platform-native ViewHolder recycling and prefetch pipeline. Android's documentation explicitly describes prefetching views outside the viewport during idle time between frames to reduce scroll/fling jank. Therefore, if the optimized Avalonia path still does not meet the desired Android feel, the recommended next architectural step is an Android-only native `RecyclerView` hosted through Avalonia `NativeControlHost`, while retaining the shared OneDrive services and view-model state.

A native host has an important trade-off: Android native views are composed above Avalonia content, so Avalonia popups cannot be drawn over them. Any selection/action UI over a native file list should therefore either be native as well or temporarily hide/resize the native host, similar to the Android video preview handling already used by Hello1Drive.
