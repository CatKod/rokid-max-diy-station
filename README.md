# Rokid Max · DIY Station

> A lightweight **Windows background app** that unlocks the **3 DoF head tracking** built into the **Rokid Max** AR glasses — without needing the official Rokid Station.

The Rokid Max already has an onboard 3-axis IMU. On Rokid Station that data is consumed by their closed-source runtime. Existing third-party tools are unreliable, slow, or broken — so this project builds a **clean, native, open replacement**.

---

## ✨ What this does

- Talks to the **Rokid Max over USB** and reads the onboard IMU
- Exposes the orientation stream as a virtual head-tracker
- Works with games, sims, and flight/space titles via standard protocols

The app is designed to live quietly in the **system tray** and run in the background — no extra hardware, no phone, no station box.

## 🎯 Goals (v1)

- ✅ Enumerate Rokid Max on USB (vendor / interface / endpoints)
- ✅ Read raw IMU packets (gyro + accel)
- ✅ Run a sensor-fusion filter (Madgwick / Mahony / complementary)
- ✅ Output 3 DoF orientation as:
  - **FreeTrack-compatible** UDP stream (so OpenTrack and almost every sim picks it up)
  - **Virtual mouse / face-tracking** output (e.g. mouse emulator / VSeeFace protocol)
- ✅ Run as a **Windows tray app** (autostart on login optional)
- ✅ Settings UI for smoothing, axis remap, FOV curve

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

> **Bootstrapping.** Repo just created — greenfield, no code yet.
> Tracking happens in Issues and Projects.

## 🤝 Contributing

Issues, IMU captures, protocol dumps, and PRs are all welcome — especially anything you learn while sniffing the Rokid Max USB traffic.

## 📜 License

MIT — see [LICENSE](LICENSE).
