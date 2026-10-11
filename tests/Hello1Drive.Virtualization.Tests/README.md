# Desktop virtualization regression checks

Run without a display server or Microsoft account:

```sh
dotnet run --project tests/Hello1Drive.Virtualization.Tests -c Release
```

The headless checks exercise details, large icons and extra-large icons with 1,000,
5,000 and 10,000 items. They verify bounded realization, container recycling,
bottom scrolling, geometry hit testing, marquee ranges, resize/view-mode changes,
fixed-slot metadata hydration, append, source replacement and overlay stability.
They also drive the real MainView with headless pointer and keyboard events to
check Ctrl/Shift selection, arrow/End navigation and the transfer panel.

On Windows, also check mouse wheel settings, Ctrl/Shift selection, keyboard
navigation, right-click menus, previews, and cold/warm thumbnail caches. Headless
layout checks do not measure GPU frame times or validate Windows pointer input.
