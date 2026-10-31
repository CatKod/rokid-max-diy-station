# Rokid Max · DIY Station

> A lightweight **Windows background app** that unlocks the **3 DoF head tracking** built into the **Rokid Max** AR glasses — without needing the official Rokid Station.

The Rokid Max already has an onboard 3-axis IMU. On Rokid Station that data is consumed by their closed-source runtime. Existing third-party tools are unreliable, slow, or broken — so this project builds a **clean, native, open replacement**.

---

## ✨ What this does

- Talks to the **Rokid Max over USB** and reads the onboard IMU (gyro + accel + mag)
- Fuses the sensors into a stable 3 DoF head pose
- **Pins a real desktop window in 3D space on the glasses' display** —
  the window stays anchored to the world as you move your head
- Also exposes the orientation stream as a virtual head-tracker for
  games, sims, and flight/space titles

The app is designed to live quietly in the **system tray** and run in the background — no extra hardware, no phone, no station box.

## 🎯 Goals (v1)

- ✅ Enumerate Rokid Max on USB (vendor / interface / endpoints)
- ✅ Read raw IMU packets (gyro + accel + mag)
- ✅ Run a sensor-fusion filter (Madgwick / Mahony / complementary)
- ✅ Output 3 DoF orientation as:
  - **FreeTrack-compatible** UDP stream (so OpenTrack and almost every sim picks it up)
  - **Virtual mouse / face-tracking** output (e.g. mouse emulator / VSeeFace protocol)
- ✅ Run as a **Windows tray app** (autostart on login optional)
- ✅ Settings UI for smoothing, axis remap, FOV curve
- ✅ **Spatial anchoring** — capture any window and pin it on the
      glasses' Micro-OLED display as a 3D-anchored quad

## 🧩 Architecture

```
┌────────────┐     USB      ┌─────────────────────────┐
│ Rokid Max  │─────────────▶│   RokidDiyStation       │
│  (IMU)     │  HID / bulk  │   (Win background app) │
└────────────┘              │  • USB capture          │
                           │  • Sensor fusion        │
                           │  • Protocol multiplexer │
                           └─────────┬───────────────┘
                                     │
             ┌───────────────────────┼──────────────────────┐
             ▼                       ▼                      ▼
      FreeTrack UDP           Virtual Mouse           WebSocket
      (OpenTrack / sims)       (desktop apps)         (browser, custom)
```

## 🛠 Tech stack

- **C# .NET 8** (Win tray app via `H.NotifyIcon`)
- **HidLibrary** / **UsbDevices** for USB/HID access
- **Math.NET Numerics** or custom Madgwick for sensor fusion
- **FreeTrack 2.x** client protocol (UDP, port 5555) — broadest game compatibility
- Single-instance, low-CPU, < 30 MB RAM target

## 📦 Project layout

```
rokid-max-diy-station/
├── src/
│   ├── RokidDiyStation.App/          # Win tray app + UI
│   ├── RokidDiyStation.Core/         # IMU, fusion, protocols (no UI)
│   └── RokidDiyStation.Cli/          # headless testing tool
├── drivers/                          # notes on USB IDs / HID descriptors
├── docs/                             # protocol reverse-engineering notes
├── tools/                            # scripts (calibration, packet dumps)
├── tests/
├── .github/ISSUE_TEMPLATE/
├── LICENSE  (MIT)
└── README.md
```

## 🚧 Status

> **M1 – M6 shipped, M7 (spatial anchoring) landed and verified on
> real hardware.** The Rokid Max's 3 DoF IMU is read over USB HID,
> fused with a Mahony filter, and used to pin a captured desktop window
> as a quad on the glasses' Micro-OLED display. The panel tracks the
> head in 3D, stays dead-centre, and is hidden the moment the glasses
> are unplugged.
>
> Verified on the physical device: 60 FPS, 23.13% backbuffer coverage,
> 48.1% × 48.5% of the display, zero errors. The rendered backbuffer was
> checked by a vision model — not mirrored, not flipped, no clipping, no
> UV seam, crisp legible text.
>
> See `ROADMAP.md` for what is left.

## 🎮 Using it

1. Plug the Rokid Max into the PC over USB-C. The glasses' display
   appears as an extra monitor (`\\.\DISPLAY5`).
2. Start `RokidDiyStation.exe`. It lives in the **system tray** (cyan
   "R") — no main window unless you want one.
3. Hover the mouse over any window and press **`Ctrl+Alt+P`**
   ("pin foreground window"). The window is captured and pinned in
   space, right where you were looking.
4. Move your head. The panel stays anchored to the world, not to your
   eyes.
5. If it drifts or you want it elsewhere:
   - **`Ctrl+Space`** — re-pin the panel where you are looking now
   - **`Ctrl+R`** — forget the panel entirely
   - **`Ctrl+C`** — recenter the world origin and re-pin
6. **`Ctrl+Alt+H`** — show/hide the settings window.

If a hotkey is owned by another program, the app logs the fallback it
bound instead to `%LOCALAPPDATA%\RokidDiyStation\diag.log`.

## 🔬 Verifying it on real hardware

```bash
dotnet run --project tools/SpatialSelfTest   # live IMU + panel metrics
dotnet run --project tools/PanelSnapshot -- artifacts/panel.png
```

`PanelSnapshot` writes the exact backbuffer that reaches the Micro-OLED
to a PNG — that is how mirroring, flipping and clipping get caught,
because a "23% coverage" number reports all three as a pass.

## 🤝 Contributing

Issues, IMU captures, protocol dumps, and PRs are all welcome — especially anything you learn while sniffing the Rokid Max USB traffic.

## 📜 License

MIT — see [LICENSE](LICENSE).
