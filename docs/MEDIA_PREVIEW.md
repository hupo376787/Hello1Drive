# Media preview

Hello1Drive now exposes media playback through an optional platform service (`IEmbeddedMediaPlayerFactory`). This keeps the shared Avalonia Core independent from one concrete native media backend.

## Desktop: embedded LibVLCSharp player

The Desktop head registers an in-app player based on `LibVLCSharp.Avalonia` 3.10.0. The player is hosted directly inside the normal Hello1Drive preview overlay and provides:

- local cached-file playback;
- play / pause;
- seek bar and elapsed / total time;
- volume and mute;
- Space to play/pause and Left/Right to seek 5 seconds;
- automatic fallback to the operating-system player if LibVLC cannot be initialized.

The Windows desktop project also references `VideoLAN.LibVLC.Windows`, so published Windows builds carry the native LibVLC runtime. Linux and macOS builds use LibVLCSharp but currently expect a compatible system LibVLC/VLC installation; if it is unavailable, Hello1Drive keeps the existing system-player fallback.

Because preview files already pass through `FileCacheService`, the embedded player reads the local cache instead of requesting the OneDrive body a second time. Reopening an unchanged cached video therefore avoids another file download.

## Why not the official Avalonia MediaPlayerControl?

Avalonia has an official `Avalonia.Controls.MediaPlayer` / `MediaPlayerControl` implementation, but it is part of Avalonia Pro or higher and requires an Avalonia license key. Hello1Drive keeps the default repository usable without a commercial Avalonia UI license, so the current desktop implementation uses the open-source LibVLCSharp backend instead.

If an Avalonia Pro license is available later, `IEmbeddedMediaPlayerFactory` makes it possible to add a second backend without changing the OneDrive or preview view-model code.

## Mobile and Browser

The shared Core continues to fall back to the platform launcher on heads that do not register an embedded player. Android/iOS can later register a native media implementation behind the same interface; Browser/WASM can use a browser-native video/audio host.
