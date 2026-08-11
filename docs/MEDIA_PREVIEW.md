# Media preview

Hello1Drive now exposes media playback through an optional platform service (`IEmbeddedMediaPlayerFactory`). This keeps the shared Avalonia Core independent from one concrete native media backend.

## Desktop: embedded LibVLCSharp player

The Desktop head registers an in-app player based on `LibVLCSharp.Avalonia` 3.10.0. The player is hosted directly inside the normal Hello1Drive preview overlay and provides:

- local cached-file playback;
- play / pause;
- seek bar and elapsed / total time;
- volume and mute;
- Space to play/pause and Left/Right to seek 5 seconds;
- replay from the beginning after natural end-of-media (the play button explicitly resets/re-attaches the media instead of calling `Play()` on the ended pipeline);
- automatic fallback to the operating-system player if LibVLC cannot be initialized.

The Windows desktop project also references `VideoLAN.LibVLC.Windows`, so published Windows builds carry the native LibVLC runtime. Linux and macOS builds use LibVLCSharp but currently expect a compatible system LibVLC/VLC installation; if it is unavailable, Hello1Drive keeps the existing system-player fallback.

Because preview files already pass through `FileCacheService`, the embedded player reads the local cache instead of requesting the OneDrive body a second time. Reopening an unchanged cached video therefore avoids another file download.

## Why not the official Avalonia MediaPlayerControl?

Avalonia has an official `Avalonia.Controls.MediaPlayer` / `MediaPlayerControl` implementation, but it is part of Avalonia Pro or higher and requires an Avalonia license key. Hello1Drive keeps the default repository usable without a commercial Avalonia UI license, so the current desktop implementation uses the open-source LibVLCSharp backend instead.

If an Avalonia Pro license is available later, `IEmbeddedMediaPlayerFactory` makes it possible to add a second backend without changing the OneDrive or preview view-model code.

## Android: native VideoView

The Android head registers `AndroidEmbeddedMediaPlayerFactory`. It embeds Android's native `VideoView` in Avalonia through `NativeControlHost` / `AndroidViewControlHandle`. Hello1Drive supplies a compact custom transport row with one stateful play/pause button and a draggable seek bar on the same line; the stock Android `MediaController` is intentionally not used, so no rewind/fast-forward buttons are injected. Playback uses the same local `FileCacheService` path as desktop, so the video is not downloaded a second time. Codec/container support follows the Android device's native media stack.

## iOS and Browser

The shared Core continues to fall back to the platform launcher on heads that do not register an embedded player. iOS can register an AVPlayer-backed implementation behind the same interface; Browser/WASM can use a browser-native video/audio host.


## Viewer preference

Settings contains **Use built-in viewer**, enabled by default. When disabled, opening a file first
places it in Hello1Drive's persistent file cache and then asks Avalonia `ILauncher` to open the local
file with the operating system default application. If the platform cannot launch that type, the
preview remains open with an unsupported message and a **Use system app** retry action.

The Android embedded video surface uses a custom transport row rather than Android `MediaController`:
one stateful play/pause button appears before the seek bar, with no rewind/fast-forward buttons.

## Mobile preview action panel

On Android/iOS the preview overflow button uses an in-page, centered dark action panel rather than an
Avalonia `ContextMenu`. Android Back dismisses this panel before closing the preview. A stationary
long-press on an image opens the same panel. Pointer movement cancels the hold before the timer fires,
and pinch/scroll gestures cancel it immediately, so horizontal Carousel paging and zoomed-image panning
remain direct manipulation gestures instead of being mistaken for a long press.
