# RokidDiyStation.App — Windows tray shell

The Win32/WPF shell for the DIY head-tracking station.

- Hosts `H.NotifyIcon` tray icon
- Owns the lifetime of `RokidDiyStation.Core.RokidStation`
- Provides a small settings window (FOV curve, smoothing, axis remap, autostart)
- Single-instance via named `Mutex`
- Optional auto-start on Windows login (HKCU\...\Run)

Planned entry points:

- `Program.Main` — composition root
- `TrayApplicationContext` — tray menu, hotkeys
- `MainWindow` — settings UI

> Code will be added in a follow-up commit. This README documents the contract.
