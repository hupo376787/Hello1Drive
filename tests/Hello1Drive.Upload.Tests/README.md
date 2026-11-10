# Transfer and Android regression checks

Run without a device, display server or Microsoft account:

```sh
dotnet run --project tests/Hello1Drive.Upload.Tests -c Release
```

Scripted HTTP responses cover non-seekable picker streams, Android pipes reporting
zero length, small upload Content-Length, aligned large-file chunks, remaining
stream bytes, lost acknowledgements, native socket closed/connection abort/timeouts,
HTTP 416, expired/completed sessions, final 202 responses, premature EOF, quota
rejection, cancellation and temporary-file cleanup. Headless
MainView/ViewModel checks cover pending server confirmation, refresh exceptions
and Android-style stream-close exceptions after a successful upload.
The background-state check confirms that a retry keeps the task/service active
until it finishes, without an intermediate Failed transition.
The watchdog test cancels a stalled socket and checks that the next attempt queries
the original session instead of starting the entire file again.

Additional checks cover OneDrive QuickXor checksum vectors, same-name/same-size
files with different content, absent server checksums, truncated/oversized downloads,
slow download limits, thumbnail version changes during I/O and cancellation releasing
the next cache request. A lost upload acknowledgement is accepted only after the
remote content checksum matches. If the session is gone and verification remains
unavailable, the result is reported as unconfirmed rather than replayed automatically.

Android device checks after building this change:

1. Scroll quickly and change folders/view modes while offline, then reconnect. Old
   thumbnail requests must release their slots; current images must load without
   requiring a second scroll and must match the current file version.
2. Open/close settings, preview and folder screens repeatedly. Check for native
   disposed-view exceptions and obsolete list subscriptions retaining old views.
3. Play and pause a video, detach/recreate its native surface (including activity
   transitions), and verify position, play/pause controls and temporary speed boost.
   Closing the preview must stop playback and timer work.
4. Interrupt a download, resume it and compare the complete file with OneDrive.
   A short EOF must not yield an 已下载 row; cancellation must stop retrying.

The automated checks run without Android UI/runtime and do not validate native
video playback, RecyclerView rendering, or OEM lock-screen behavior on a device.

On Android, upload a local image and a >10 MiB video selected from Documents and
from a provider that returns a pipe. Repeat with a slow connection/speed limit
(one 5 MiB chunk should take >30 seconds), then lock/unlock the screen. Allow the
notification and battery-optimization exemption prompts. Check that the transfer
notification stays present after Home/lock and disappears after all queued jobs
finish. Temporarily disable/re-enable Wi-Fi and test Wi-Fi/mobile-data switching.
Compare
the uploaded file size/content in OneDrive and ensure only one remote copy exists.
The row should stay at 99% while confirmation is pending and become 已上传 only
after OneDrive confirms success. A real quota/permission rejection must remain a
failure and show its error. These checks do not replace device/network testing.

For deep-idle testing on a development phone:

```sh
adb shell dumpsys battery unplug
adb shell dumpsys deviceidle force-idle
# Observe transfer progress/content; then restore the test-only system changes:
adb shell dumpsys deviceidle unforce
adb shell dumpsys battery reset
```

Battery exemption and OEM background permission affect deep-idle network access.
Android can still stop the process/FGS for user stop, resource pressure or dataSync
runtime limits; unfinished transfers remain persisted for recovery on next launch.

Protocol reference:
https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0
