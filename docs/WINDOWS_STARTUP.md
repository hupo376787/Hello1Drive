# Windows startup

The desktop settings page exposes **开机启动** on Windows only. When enabled,
Hello1Drive writes a per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
entry that launches `Hello1Drive.exe --tray`. The `--tray` startup path hides the main
window, removes it from the taskbar, and exposes a tray icon with **打开 Hello1Drive**
and **退出**. Disabling the option removes the Run value. No administrator permission is required.
