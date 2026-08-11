# Android background transfers

Hello1Drive keeps user-initiated upload/download/cache work alive when the Android Activity moves to the background by running a `dataSync` foreground service while at least one transfer is `Waiting` or `Running`. Keeping the service alive across queued items prevents an Android 12+ background-start restriction from creating a gap between consecutive files.

## Behavior

- The existing Core transfer queue continues to own the actual HTTP/file I/O.
- Android starts `TransferForegroundService` as soon as the first user transfer is queued (`Waiting` or `Running`).
- A low-importance ongoing notification reports running upload/download/cache counts.
- When the last waiting/running transfer completes, fails, or is cancelled, the foreground service stops immediately.
- The service is `NotSticky`: if Android kills the entire process, Hello1Drive does not invent/restart a half-open stream. Existing `transfers.json` resume metadata is used the next time the app starts.

## Android permissions / service type

The Android head declares:

- `android.permission.FOREGROUND_SERVICE`
- `android.permission.FOREGROUND_SERVICE_DATA_SYNC`
- `android.permission.POST_NOTIFICATIONS`

The service is declared as foreground service type `dataSync`, which is the Android service type intended for user-visible cloud upload/download work.

Android 13+ does not require notification permission merely to launch a foreground service, but granting it allows the ongoing transfer notification to appear in the notification drawer. If denied, Android still surfaces active foreground services through its active-app/task-manager UI.

## Platform limitation

On Android 15+ for apps targeting API 35+, `dataSync` foreground-service time is limited by the OS (currently a total of six hours in a rolling 24-hour period while backgrounded). The service implements `OnTimeout` and stops promptly if Android reaches that limit.

Force-stop, OEM battery managers, or the user explicitly stopping the app can still terminate the process. A foreground service greatly improves normal app-switch/background/lock-screen continuity, but it is not an unconditional process-lifetime guarantee.
