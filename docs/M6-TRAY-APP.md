# M6 — Tray app

`RokidDiyStation.App` is the Windows shell for the DIY spatial-anchor
station.  It hosts an H.NotifyIcon (tray icon) and a small WPF
settings window, owns the lifetime of the M5 spatial-anchor pipeline,
and ships with the same hotkey behaviour as the M5 CLI (Space, R, C).

## Quick start

```powershell
# Build everything
dotnet build -c Release

# Run the tray app
src/RokidDiyStation.App/bin/Release/net8.0-windows10.0.19041.0/RokidDiyStation.exe
```

You'll see a tray icon in the system notification area.  Right-click
for the menu (Open settings, Set anchor, Reset, Quit) or left-click
to bring up the settings window.

## CLI flags

The exe also takes a few shell-friendly flags for scripting:

| Flag | Effect |
|---|---|
| (no flag) | Start the tray app.  Honours `StartMinimised` from settings. |
| `--minimised` | Start minimised to the tray (overrides the saved setting). |
| `--discover` | List attached Rokid devices and exit.  Useful for smoke tests. |
| `--reset` | Delete `%APPDATA%\RokidDiyStation\settings.json` and exit. |

## Single instance

A named `Mutex` (`Local\RokidDiyStation.SingleInstance`) ensures only
one tray app is running at a time.  A second invocation sends a
"show" message to the first over a named pipe and exits 0.

## Settings

Persisted to `%APPDATA%\RokidDiyStation\settings.json` (atomic
write — temp file then `File.Replace`).  All 12 fields:

| Field | Default | Notes |
|---|---|---|
| `UdpHost` | `127.0.0.1` | FreePIE UDP target host |
| `UdpPort` | `5555` | FreePIE UDP target port |
| `MaxPacketsPerSecond` | `250` | Rate cap (Core emitter) |
| `UseMagnetometer` | `true` | M5 spatial-anchor mag correction |
| `Smoothing` | `0.0` | 0..0.9 EMA alpha for orientation+position |
| `FovDegrees` | `60` | Output FOV hint (consumers may ignore) |
| `Autostart` | `false` | Write HKCU\…\Run value on save |
| `StartMinimised` | `true` | Start hidden in tray on launch |
| `AnchorHotkey` | `Space` | Display only — actual key hard-coded |
| `ResetHotkey` | `R` | Display only |
| `RecenterHotkey` | `C` | Display only |
| `AxisRemap` | `XYZ` | Permutation; future use |

The hotkey *labels* are stored for the UI but the actual hotkeys are
hard-coded to Space / R / C using the Win32 `RegisterHotKey` API on
a per-id basis.  Changing them in the settings file is a no-op
until the UI exposes remapping.

## Global hotkeys

Registered via P/Invoke on a hidden WPF message window after the
settings window loads:

| Key | Action |
|---|---|
| **Space** | `TrackingService.SetAnchorHere()` |
| **R** | `TrackingService.ResetAnchor()` |
| **C** | `MainWindow.Recenter()` (placeholder log line) |

If another app already owns one of these hotkeys, the registration
fails silently — the corresponding key does nothing.

## Autostart

On save with `Autostart = true` we write:

```
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
   "RokidDiyStation" = "\"C:\path\to\RokidDiyStation.exe\" --minimised"
```

Verified end-to-end:

```
Autostart before: off
After enable: on  (expected: on)
After disable: off  (expected: off)
OK
```

## Architecture

```
+----------------- WPF (UI thread) -----------------+
|  TaskbarIcon (H.NotifyIcon.Wpf)                   |
|  MainWindow (settings + live status)              |
|  HotkeyBinding (Win32 RegisterHotKey)             |
+----------------- | ------------------------------+
                  | StatusChanged event (5 Hz)
+----------------- v ------------------------------+
|  TrackingService (background thread)              |
|    SpatialAnchor  (Core)                          |
|    OrientationSmoother                            |
|    FreePieEmitter (Core, UDP)                     |
|    HidSharpEnumerator (Core)                      |
+---------------------------------------------------+
```

`TrackingService.Start()` runs the pipeline on its own thread so the
WPF dispatcher stays responsive.  Status changes are marshalled back
to the UI via the `StatusChanged` event (the receiver is responsible
for `Dispatcher.Invoke` if it touches controls — `MainWindow` does).

## Settings UI

The window has 6 sections (top to bottom):

1. **Status** — large state label, detailed line (Hz, position,
   Euler) updated 5×/s while streaming.
2. **Output (FreePIE UDP)** — host, port, max Hz, smoothing sliders.
3. **Tracking** — magnetometer checkbox, emit-raw-position checkbox,
   FOV slider.
4. **Hotkeys** — read-only labels (Space / R / C).
5. **System** — autostart, start-minimised, "Save settings" button.
6. **Status log** — ring buffer of 200 lines with timestamps.

Closing the window hides it (the tray app keeps running).  Use
**Quit** in the context menu to actually exit.

## Tests

69/69 pass (52 Core + 17 App).  App tests cover:

- `AppSettings` defaults and `with` immutability
- `SettingsService` round-trip, missing file, corrupt file, atomic save
- `AxisRemap` parsing and round-trip
- `OrientationSmoother` alpha=0 (raw) and seeded state
