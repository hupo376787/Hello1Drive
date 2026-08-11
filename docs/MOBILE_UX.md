# Mobile UX / Performance

This build keeps the desktop interaction model unchanged and gives Android/iOS a separate mobile interaction path.

## File interaction

- Tap a file or folder: open it.
- Long-press: start selection mode and select the pressed item.
- While selection mode is active, tapping another item toggles that item without relying on `ListBox` native touch selection.
- Mobile file tiles suppress desktop context-menu gestures.

## Scrolling

- Android/iOS use `ItemsRepeater` for the details, large-icon and extra-large-icon views.
- Large icon views use `UniformGridLayout`, so only visible tiles are materialized instead of realizing the whole folder with `WrapPanel`.
- Thumbnail downloading may continue during a fling, but bitmap decoding and UI image replacement wait until the list has been idle briefly.
- Scrolling downward hides the mobile top/action bars and bottom status bar. Scrolling upward shows them again; reaching the top always shows them.

## Mobile pages

The following application-owned modal surfaces are full-screen pages on mobile while retaining the existing desktop presentation:

- Settings
- Transfer list
- Create/rename/delete prompts
- Logout confirmation
- Close confirmation
- Busy/loading surface
- Preview

Native OS file/folder pickers and compact menus remain platform controls.

## Status bar

The mobile/desktop bottom status bar now shows only the current displayed item count on the left. Account name and quota are removed from this bar.

## Pull to refresh and folder scroll isolation

- Each mobile file view is wrapped in Avalonia's `RefreshContainer`. When the active list is already at the top, pulling down and releasing refreshes the current OneDrive folder.
- Scroll offsets remain remembered independently per OneDrive folder.
- Folder navigation pulses `ScrollViewer.IsScrollInertiaEnabled` off before changing data and fences `ScrollChanged` while the destination offset is restored. This prevents a fling that began in a child folder from continuing to move the parent folder after Back navigation.

## Folder navigation and view memory

- A pending OneDrive folder request is cancellable. Pressing the system Back button while a child folder is still loading cancels that request immediately and restores the parent folder; it no longer waits for the child request to finish.
- Folder view mode is remembered per Microsoft account + OneDrive folder ID. Details, Large Icons, and Extra Large Icons are restored independently when revisiting each folder.
- The existing global `ViewMode` remains only as the fallback for a folder that has not been visited before.

## Account page and preview actions (2026-08)

- The mobile account page mirrors the same configurable background stack used by the file page.
- OneDrive quota is shown with a used/total label, percentage, and a colored progress bar; numeric quota values are also persisted in the startup snapshot.
- When Settings is opened from the mobile account page, Back returns to the account page before returning to the file list.
- The mobile preview action panel uses `下载` and `缓存` as separate actions. The same panel is opened from the More button and from a stationary image long-press.
- The mobile transfer page mirrors the same configurable app background rather than using a standalone opaque page color.
