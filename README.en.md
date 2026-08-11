# Hello1Drive

Hello1Drive is a cross-platform OneDrive client built with **Avalonia 12, .NET 10 and Microsoft Graph**. It uses a shared Core project plus Desktop, Android, iOS and Browser heads.

Accent color: `#FD6F71`  
Android application ID: `com.xiaowei.hello1drive`

## Highlights

- Microsoft personal-account sign-in/sign-out with profile avatar.
- OneDrive browsing, breadcrumbs, search, create folder, rename and delete, with in-memory folder caching for instant back/forward navigation, background validation, and optional last-folder restore on next launch.
- Multi-select upload, Ctrl/Shift or marquee selection, plus recursive folder download.
- Upload/download queue where the entire batch appears immediately, with waiting/running/completed/error states, per-item progress and retry for failures; the Desktop entry lives in the title bar immediately left of Settings.
- Resumable large-file upload sessions.
- Details / Large icons / Extra large icons views.
- Two-level sorting: Settings defines a global System/Date/Name/Size default (ascending or descending), and changing it clears all previous per-folder overrides. Individual folders can then override it again or return to the Settings default. Size ordering adds the OneDrive non-indexed-query `Prefer` compatibility header and safely gives only an unsupported folder a System-default override if its backend still rejects `SMTotalFileStreamSize`.
- Overlay previews that close when clicking outside, plus Desktop right-click and mobile long-press context menus for common file actions.
- Built-in text editor with save-back-to-OneDrive.
- Built-in image and animated-GIF preview that opens fitted to the viewport, has no scrollbars, keeps the mouse pointer as the zoom anchor, and shows the 1%–800% zoom ratio at the top center.
- Persistent opened-file cache validated against DriveItem version metadata; unchanged files reopen locally without downloading the body again. Image/video thumbnails now have a separate persistent disk cache as well (`cache/thumbnails` beside the executable on Desktop, app data on mobile), so unchanged thumbnails survive app restarts without another thumbnail download. Clearing cache removes both file and thumbnail caches, and explicit file/folder caching also warms thumbnail entries recursively.
- Built-in image/media preview with Desktop LibVLC and Android native `VideoView`. Desktop media can replay from the beginning after reaching the end. Android uses a compact custom transport row with one play/pause button before the seek bar and no rewind/fast-forward buttons. Mobile preview overflow and stationary image long-press share a centered action panel that Back dismisses first, without interfering with swipe/zoom gestures. A **Use built-in viewer** setting (enabled by default) can be disabled to cache files locally and open them with the OS default application; unsupported types remain on the preview page with a system-app retry action.
- Fully custom Desktop title bar with the primary file toolbar merged into it, transparent title/status surfaces, compact toolbar height and consistent caption glyphs.
- HelloV-style right-side acrylic settings panel using the current wallpaper copy, blur and translucent tint.
- Light/dark/system theme mode.
- Custom window backgrounds: color, local image, URL, local folder slideshow and OneDrive-folder slideshow, plus last-folder memory, draggable upload-button visibility/position and transparent file-item background settings.
- Persisted StorageProvider bookmarks for sandbox-friendly local background access.
- GitHub Actions workflows and PowerShell/Bash one-click publish scripts.

## Platforms

- Windows x64 / arm64
- Linux x64 / arm64
- macOS x64 / arm64
- Android (`net10.0-android36.0`)
- iOS / iPadOS (`net10.0-ios26.0`)
- Browser / WebAssembly (`net10.0-browser`)

See [`docs/ENTRA_SETUP.md`](docs/ENTRA_SETUP.md) before running mobile or browser targets.

## Run Desktop

```bash
dotnet run --project src/Hello1Drive.Desktop/Hello1Drive.Desktop.csproj
```

## One-click publish

```powershell
./scripts/one-click-publish.ps1
```

or:

```bash
./scripts/one-click-publish.sh all
```

Artifacts are written to `artifacts/`.

- Desktop now includes an in-app LibVLCSharp/Avalonia media player that plays the local Hello1Drive cache with play/pause, seeking, volume/mute and keyboard seeking. Windows publishes include the native LibVLC runtime; Linux/macOS fall back to the system player when system LibVLC is unavailable. Mobile/Browser currently keep the platform-player fallback. See `docs/MEDIA_PREVIEW.md`.


### Browser / WASM sign-in callback

The development SPA redirect URI is fixed to `http://localhost:5173/browser-auth`. Register it under the Microsoft Entra **Single-page application** platform. Desktop continues to use `http://localhost`.

## Folder navigation and view memory

- Folder navigation is cancellable: pressing system Back while a child folder is still loading immediately cancels that request and restores the parent folder.
- Details / Large Icons / Extra Large Icons are remembered independently for every OneDrive folder under each Microsoft account, including the root folder.
- An unvisited folder uses the existing global ViewMode as its initial fallback.

### Startup and loading UX

- Added a Hello1Drive startup splash without making restored folder snapshots wait for Graph synchronization.
- Unified indeterminate loading UI: circular activity ring above `加载中` on a transparent loading surface.
- Folder entry keeps only the first children page on the critical path; missing child counts refresh in the background and cached folders do not flash a busy overlay.
- Mobile Back at the OneDrive root opens a compact close confirmation. The Settings “download all OneDrive files” warning is also a compact dialog.

- Windows desktop supports launch-at-login with `--tray`, starting directly in the system tray.

### Android background transfers

Android upload, download, and cache work starts a `dataSync` foreground service while transfers are waiting/running, allowing user-initiated transfers to continue after switching to another app. The service stops when the queue becomes inactive. Android 15+ applies an OS time limit to `dataSync` foreground services; see `docs/ANDROID_BACKGROUND_TRANSFERS.md`.
