# Mobile thumbnail diagnostics

The mobile build writes a small rolling diagnostic log for the thumbnail pipeline to:

`LocalApplicationData/Hello1Drive/logs/thumbnail-diagnostics.log`

The logger is asynchronous: scroll/thumbnail code only enqueues short lines and a single background writer persists them. The active log is capped at roughly 2 MB and one previous file is retained.

## Reproduce and collect

1. Open Settings and tap **Clear** beside **Thumbnail diagnostics log**.
2. Open a folder with many images.
3. Fling from near the top to several hundred items below, then do not touch the screen for about 2 seconds.
4. Confirm the visible names/sizes are present while some thumbnails are still missing.
5. Open Settings and tap **Copy log**.
6. Paste the copied text into the bug report/chat.

## Important markers

- `GESTURE`: Avalonia scroll, inertia-start, and inertia-ended lifecycle.
- `SCROLL`: ScrollViewer offsets and the ViewModel scrolling flag.
- `IDLE`: when Hello1Drive considers the fling fully idle.
- `REALVIEW`: count of actual realized/visible ItemsRepeater elements.
- `REALIZED`: exact visible item indices and whether network thumbnail work was blocked because the ViewModel still considered the list scrolling.
- `WINDOW`: approximate offset-derived thumbnail window.
- `SCHEDULE`: items accepted/rejected by the thumbnail scheduler.
- `WORKER`: per-item lifecycle and drop/cancel reason.
- `CACHE`: persistent thumbnail cache hit/miss.
- `GRAPH`: direct thumbnail URL / authenticated Graph fallback status and returned byte count.
- `DECODE`: bitmap decode start/success/retry.
- `UI`: whether the decoded bitmap was applied or discarded because the item was no longer current/visible.

This log intentionally records only shortened OneDrive item IDs and truncated file names; it does not record access tokens, authorization headers, or thumbnail URLs.
