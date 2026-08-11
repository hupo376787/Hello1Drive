# Startup performance

Hello1Drive now uses a cache-first startup path after the user has signed in successfully once.

## Startup sequence

1. Read `startup-cache.json` from the app's local data directory.
2. Immediately restore the previous folder metadata, breadcrumb, item count, quota text, and cached thumbnail bindings.
3. Run MSAL silent authentication.
4. Request `/me` and `/me/drive` in parallel.
5. Revalidate the visible OneDrive folder in the background without covering the cached list with the global busy overlay.
6. Load the profile photo in the background.
7. Load URL/local-folder/OneDrive decorative backgrounds after the OneDrive startup path is released.

The startup snapshot is metadata only. Full file contents are not duplicated into the snapshot. Existing file and thumbnail caches keep their own storage rules.

## Access-token hot cache

Desktop, Android and iOS authentication services keep the most recent MSAL access token in memory until two minutes before expiry. Graph calls made during one app session therefore do not repeat `GetAccountsAsync` + `AcquireTokenSilent` for every request. MSAL's persistent cache is still the source of truth across app restarts.

## Safety

- The snapshot is associated with the previous Microsoft account id.
- After silent authentication, if the real account differs, the restored metadata is discarded before remote browsing continues.
- Sign out deletes the startup snapshot.
- Disabling "remember last folder" removes a non-root startup snapshot.
- Network or cache failures never block normal OneDrive operation; the snapshot is only a startup optimization.
