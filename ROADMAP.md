# 🛣 Roadmap

A living plan. PRs that move items forward get a fast-review promise.

## M0 — Bootstrap (now)
- [x] Create repository skeleton
- [x] Add solution + 3-project structure (`App`, `Core`, `Cli`)
- [x] Add Windows GitHub Actions build
- [x] Issue templates (bug / feature / capture)
- [x] Setup project tracker (Milestones + Projects board)
- [x] Add `global.json` pinning .NET 8 SDK
- [x] Add Core test project (`xUnit`) wired into solution

## M1 — Reverse the protocol
- [x] Pin down VID/PID, interface class, report ID, packet length
- [x] Document byte layout for the IMU frames (see `docs/PROTOCOL.md`)
- [x] Confirm layout against two upstream RE projects (see `docs/PROTOCOL-REFERENCES.md`)
- [ ] Capture USB traffic from a *physical* Rokid Max with USBPcap — second-source confirmation only
- [ ] Save first capture as a release artifact (optional, depends on hardware access)

## M2 — First read
- [x] `IHidCapture` in `Core` enumerates and opens Rokid Max
- [x] `PacketParser` decodes a 64-byte frame to `(ts, accel, gyro, mag, worn)`
- [x] `rokid-station-cli parse <hex>` decodes a fixture
- [x] `rokid-station-cli stream` dumps live frames to stdout
- [x] Unit tests for the parser (canonical PROTOCOL.md fixture + edge cases)

## M3 — Fusion
- [ ] Mahony filter implementation in `Core.Fusion` (design lifted from `angkaworn-dotcom/rokid-spatial`'s Fusion.swift — drop magnetometer, gyro bias estimator, explicit 6 s calibration, recenter / look-ahead)
- [ ] Quaternion → Euler helpers
- [ ] Microbenchmark in `rokid-station-cli fusion`

## M4 — FreeTrack emit
- [ ] UDP client sending FreeTrack 2.x frames on port 5555
- [ ] OpenTrack receives orientation
- [ ] Verify in Star Citizen / DCS World / IL-2

## M5 — Tray app
- [ ] `H.NotifyIcon` tray shell
- [ ] Settings window: smoothing, axis remap, FOV curve, autostart
- [ ] Single-instance via named Mutex
- [ ] MSIX / installer

## M6 — Niceties
- [ ] Virtual mouse (smooth-pan) output
- [ ] WebSocket emitter for browser/Three.js demos
- [ ] Recenter button + hotkey
- [ ] Low-pass filter presets (game / sim / cinematic)

## Backlog
- Support **Rokid Air** (same IMU? different HID report?)
- Linux/Wine compatibility
- Telemetry overlay (HUD-style)
- Anti-cheat-friendly mode (no mouse output, only FreeTrack/UDP)
