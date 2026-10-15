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
- [x] **HidSharp backend** (workaround for HidLibrary `OpenDevice=false` on this host)
  - `rokid-station-cli discover-sharp` / `diagnose-sharp` / `stream-sharp`
  - See `docs/diag-hid-open-failure.md`

## M3 — Fusion
- [x] `MahonyFusion` in `Core.Fusion` (quaternion output, 437 Hz tested on real capture)
  - Optional magnetometer correction (off by default — Rokid Max mag is noisy indoors)
  - First-sample seeding from accel (no 90° startup transient)
  - Invalid-input rejection (NaN, zero accel, bad dt, free-fall)
- [x] `SyntheticImuSource` for reproducible trajectory tests (roll/pitch/yaw → accel+gyro+mag)
- [x] `tools/ConvertFixture` + `tools/AnalyzeCapture` for offline data work
- [x] Quaternion → Euler helpers (Z-Y-X intrinsic, in `SyntheticImuSource.ToEulerZYX`)
- [x] Unit tests: 7 new tests, 29/29 green (incl. end-to-end run on `docs/fixtures/raw-full.bin`)
- [ ] Microbenchmark in `rokid-station-cli fusion` (skipped — low priority)

## M4 — FreeTrack emit
- [x] UDP client sending FreePIE-compatible 50-byte packets on port 5555
  - `Core.Output.FreePieEmitter` (non-blocking socket, packet-reuse buffer)
  - `Core.Output.FreePiePacket` (50-byte struct builder, end-to-end tested)
  - CLI: `rokid-station-cli fusion-emit [--replay FILE] [--host H] [--port N]`
  - End-to-end test on `docs/fixtures/raw-200.bin`: 200/200 packets received
- [ ] OpenTrack receives orientation (verified by docs/M4-FREETRACK-EMIT.md
      recipe; not yet tested with a live game)
- [ ] Verify in Star Citizen / DCS World / IL-2 (out of scope — needs the games)

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
