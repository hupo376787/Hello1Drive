# Local metadata index and cloud synchronization

Hello1Drive treats a OneDrive folder as a stable logical list instead of a list that grows only when the user reaches the bottom.

## Goals

- keep the mobile scroll extent stable for folders containing thousands of children;
- remove scroll-triggered `LoadMore` work from the touch/fling path;
- reopen previously indexed folders immediately from local metadata;
- synchronize cloud changes in the background with Microsoft Graph delta;
- keep thumbnails/file bodies out of the metadata index so scrolling is not tied to image I/O.

## First visit to a folder

When the parent `DriveItem` already provides `folder.childCount`, mobile creates that many lightweight `VirtualDriveItemSlot` objects immediately. Parent-folder enumerations also persist each child folder's `childCount`, so even an unvisited child can establish its logical extent from the local index. Only the first Graph children page is awaited for first paint. The rest of the slots contain no `DriveItemModel` yet and render as fixed-size, non-animated placeholders.

The remaining `@odata.nextLink` pages are then enumerated continuously in the background. Each completed page fills the existing slot range in place. The collection count and the `ScrollViewer` extent therefore do not grow page by page.

If `childCount` was not known (most notably some root/startup paths), Hello1Drive asks for folder metadata in the background and reconciles only the placeholder tail when the count arrives.

## Scroll hot path

`ScrollChanged` no longer starts Graph pagination. On mobile it only updates scroll/chrome state and the throttled near-visible thumbnail window. Metadata enumeration is owned by the folder synchronization task, not by the scroll gesture.

During a fling:

- already-realized slots remain stable;
- background metadata pages can fill slots without adding/removing the collection;
- uncached network thumbnail work remains deferred;
- local-index persistence waits until touch scrolling becomes idle.

## Persistent local index

`LocalDriveIndexService` stores metadata only, one index document per Microsoft account under the app's local-data directory. The index contains item identity, parent identity, file/folder metadata, version tokens, known folder counts and the last Graph delta link. It does not contain file content or decoded thumbnails.

A previously indexed folder can therefore be materialized without waiting for the children endpoint. The normal flow becomes:

1. show local metadata immediately;
2. let the user scroll/select/open cached metadata immediately;
3. enumerate the current folder quietly when its exact server order needs validation;
4. update the drive-wide index with Graph delta in the background.

The current implementation intentionally uses the existing .NET JSON/runtime stack instead of introducing a native SQLite dependency, keeping the Core project usable by Desktop, Android, iOS and Browser heads. The in-memory representation is dictionary/index based; durable writes are atomic temp-file replacements. If a platform does not expose a writable local-data directory, the service degrades to an in-memory-only index instead of preventing app startup. On mobile the separate startup snapshot now stores only the first metadata page plus the total count; the persistent index owns the full directory, avoiding a second full-folder serialization just for startup paint. The Settings **Clear cache** command also clears the local metadata index before rebuilding the active folder from Graph.

## Drive-wide Graph delta

After sign-in, once current-folder work has priority, Hello1Drive starts a drive-wide delta synchronization. While the session stays open, a low-priority five-minute timer requests another delta pass only when no previous drive-index pass is still running. The delta enumerator yields between pages and before local-index persistence whenever the user is flinging or a current-folder metadata enumeration is active, so drive-wide maintenance does not become part of the scroll critical path.

- With no saved delta link, it performs an initial `root/delta` enumeration and follows every `@odata.nextLink` to the final `@odata.deltaLink`.
- If the same item is returned multiple times during the enumeration, the latest occurrence wins.
- Deleted items are applied after non-deleted changes from the same delta set; any descendants still under a deleted folder are then pruned, while children moved elsewhere in the same set are preserved.
- Moved/renamed/changed items are updated by item ID and `parentReference.id`.
- The final delta link is persisted and reused on later runs so subsequent synchronization normally contains only changes.
- A delta token that requires resynchronization falls back to a fresh complete delta pass.
- When an incremental delta pass actually contains changes, the visible current folder is quietly revalidated (unless the user is scrolling, selecting, or another folder sync is already active), so cloud changes can reach the current view without a manual reload.

Folder children enumeration remains useful even with a complete drive delta index because the children endpoint provides the folder's exact server order for the active sort and lets the UI fill first-visit placeholders progressively.

## Sorting

The slot index always represents the active final order. A first visit requests the children endpoint using the active Graph `$orderby` and fills pages sequentially into slots. Hello1Drive never sorts a partially loaded prefix after each new page, because doing so would move already-visible files while the user scrolls.

For a folder restored from the local index, name/date/size rules can be reproduced from indexed metadata. System/default order is kept when that folder has been fully enumerated through the children endpoint; otherwise a deterministic local fallback is used until exact server order is learned.

## Selection and refresh safety

A completed background refresh is not allowed to replace the visible collection while Android touch inertia is active. If nothing relevant changed, the existing model/slot objects are retained. If the user currently has a mobile multi-selection, a changed background snapshot is stored in the local index but is not applied underneath the active selection.

## Why slots are lightweight

A `VirtualDriveItemSlot` only owns an index and an optional reference to a real `DriveItemModel`. Creating 2,000 or 10,000 such data objects is intentionally much cheaper than creating the same number of visual controls. `ItemsRepeater` still virtualizes the visual tree, so only the viewport and its layout buffer are realized.
