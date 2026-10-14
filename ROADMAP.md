# 🛣 Roadmap

A living plan. PRs that move items forward get a fast-review promise.

## M0 — Bootstrap (now)
- [x] Create repository skeleton
- [x] Add solution + 3-project structure (`App`, `Core`, `Cli`)
- [x] Add Windows GitHub Actions build
- [x] Issue templates (bug / feature / capture)
- [ ] Setup project tracker (Milestones + Projects board)

## M1 — Reverse the protocol
- [ ] Capture USB traffic from Rokid Max with USBPcap (with & without Station)
- [ ] Pin down VID/PID, interface class, report ID, packet length
- [ ] Document byte layout for the IMU frames
- [ ] Save first capture as a release artifact

## M2 — First read
- [ ] `IHidCapture` in `Core` enumerates and opens Rokid Max
- [ ] `PacketParser` decodes a single frame to (gyr, acc)
- [ ] `rokid-station-cli stream` dumps frames to stdout
- [ ] Unit tests for the parser (synthetic bytes)

## M3 — Fusion
- [ ] Madgwick filter implementation in `Core.Fusion`
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
