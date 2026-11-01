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

## M5 — Spatial anchoring (pin a window in space)
- [x] Mag correction enabled by default (was opt-in in M3)
- [x] Gyro bias estimator (1024-sample sliding window, populated only on stationary samples)
- [x] Stationary detector (|gyro|<0.04 rad/s & |a-G|<0.6 m/s² for 150ms)
- [x] Position integrator (double-integrate accel in world frame + ZUPT)
- [x] SpatialAnchor state machine (Booting -> Calibrating -> Running)
- [x] SetAnchorHere() rebases world origin to current pose
- [x] CLI hotkeys (Space=anchor, R=reset, S=status, Q=quit)
- [x] FreePIE UDP emit extended to ship raw position + orientation (flags=0x03)
- [x] 52/52 unit tests pass
- [x] docs/M5-SPATIAL-ANCHORING.md with calibration recipe
- [x] Real-device verification: # streaming from Rokid Max (VID=0x04D2 PID=0x162F) ✓

## M6 — Tray app
- [x] H.NotifyIcon.Wpf (TaskbarIcon) + context menu (Open / Set anchor / Reset / Quit)
- [x] Settings window (host/port, smoothing, FOV, mag, autostart, hotkey labels, log)
- [x] JSON settings persistence in %APPDATA%\RokidDiyStation\settings.json (atomic write)
- [x] Single-instance via named Mutex + named-pipe "show" signal
- [x] Autostart via HKCU\…\Run (verified enable/disable round-trip)
- [x] Global hotkeys via Win32 RegisterHotKey (Space=anchor, R=reset, C=recenter)
- [x] TrackingService owns SpatialAnchor + emitter on a background thread; 5 Hz UI status
- [x] OrientationSmoother (EMA) plumbed between anchor and emitter
- [x] OrientationSmoother, AppSettings, SettingsService, AxisRemap tests
- [x] Smoke tests: --discover, --reset, tray start/stop, settings.json persist, single-instance
- [x] docs/M6-TRAY-APP.md
- [ ] MSIX / installer (deferred — the exe runs from the build output)
- [ ] Icon (.ico) — currently uses SystemIcons.Information as a placeholder

## M7 — Spatial anchoring on the glasses' display (pin a window in 3D)

The product goal: a background tray app that reads the Rokid Max's
3 DoF IMU and **pins a real desktop window in space** on the glasses'
Micro-OLED display, so the window stays put as you move your head.

- [x] `DisplayLocator` finds the Rokid Max's display (`\\.\DISPLAY5`,
      Lontium `LBT4753` bridge) among the machine's monitors
- [x] `WindowCapture` — Windows.Graphics.Capture of any top-level window
- [x] `PanelRenderer` — D3D11 renderer: borderless topmost quad on the
      glasses' display, DWM-composition-free (flip model), 60 FPS
- [x] `SpatialAnchor` drives the panel's world pose from the fused IMU
- [x] Anchor actions differentiated:
  - [x] `Ctrl+Space` — re-pin the panel at the current gaze
  - [x] `Ctrl+R` — forget the panel (drop the reference, keep calibration)
  - [x] `Ctrl+C` — recenter the world origin **and** re-pin
- [x] `DisplayGuard` — hide the panel the instant the glasses are
      unplugged, so DWM cannot relocate it over the user's screen
- [x] HLSL shaders compiled to DXBC at build time and embedded
- [x] `tools/SpatialSelfTest` — live-hardware verification (IMU feed,
      panel coverage %, FPS, error count)
- [x] `tools/PanelSnapshot` — writes the actual backbuffer to a PNG so
      mirroring / flipping / clipping can be checked by eye and by a
      vision model
- [x] Unit tests: projection math, anchor actions, display guard
- [x] Real-device verification: panel dead-centre at 48.1% × 48.5% of
      the display, 23.13% backbuffer coverage, 60 FPS, zero errors
- [x] Vision-model verification of the backbuffer: not mirrored, not
      flipped, no clipping, no UV seam, crisp legible text

## M8 — Tray app polish
- [x] H.NotifyIcon.Wpf (TaskbarIcon) + context menu (Open / Set anchor / Reset / Quit)
- [x] Settings window (host/port, smoothing, FOV, mag, autostart, hotkey labels, log)
- [x] JSON settings persistence in %APPDATA%\RokidDiyStation\settings.json (atomic write)
- [x] Single-instance via named Mutex + named-pipe "show" signal
- [x] Autostart via HKCU\…\Run (verified enable/disable round-trip)
- [x] Global hotkeys via Win32 RegisterHotKey (Space=anchor, R=reset, C=recenter)
- [x] TrackingService owns SpatialAnchor + emitter on a background thread; 5 Hz UI status
- [x] OrientationSmoother (EMA) plumbed between anchor and emitter
- [x] OrientationSmoother, AppSettings, SettingsService, AxisRemap tests
- [x] Smoke tests: --discover, --reset, tray start/stop, settings.json persist, single-instance
- [x] docs/M6-TRAY-APP.md
- [x] **Hotkeys register against a private message-only window** — the
      settings window may never have been shown (the app starts
      minimised), and an unshown WPF window has no HWND, so every
      `RegisterHotKey` used to fail silently
- [x] **Hotkey fallback chain** — another app may own a combination
      (ERROR_HOTKEY_ALREADY_REGISTERED); try the next candidate instead
      of losing the feature
- [x] **"Pin foreground window" actually works** — there is no
      `GetCursorWindow` Win32 API; the old P/Invoke threw
      `EntryPointNotFoundException` and the handler swallowed it.
      Replaced with `GetCursorPos` + `WindowFromPoint` + `GetAncestor`
- [x] Regression tests for all three of the above
- [ ] MSIX / installer (deferred — the exe runs from the build output)
- [x] Icon (.ico) — generated at build time by `generate-icon.ps1`

## M10 — Input layer stack
The bridge from the physical mouse and keyboard to whatever is projected.

- [x] `PointerInput` — `WH_MOUSE_LL` hook; swallows only the events over
      the glasses' display, so the user's other displays are untouched
- [x] `KeyboardInput` — `WH_KEYBOARD_LL` hook with `ToUnicodeEx`
      translation, so a Vietnamese/German layout needs no table in our code
- [x] `SpatialInputCoordinator` — a worker thread owns the CDP round trip,
      so the render thread never blocks on a WebSocket
- [x] Multi-port target discovery (9222 browser, 9223 Electron) with
      candidate scoring, so an open user tab is never hijacked
- [x] `AppLauncher` — starts VS Code/Notion/Discord/Obsidian/Slack with a
      DevTools port and a dedicated profile, then pins the window
- [x] Tray menu entry: "Open an interactive app…"
- [x] Unit tests: 60 green (`tests/RokidDiyStation.App.Tests/KeyboardMappingTests.cs`)

## M11 — Custom hardware cursor (the virtual 3D pointer)
A physical mouse drives a cursor drawn *in the 3D scene*, and the click
lands where that cursor is.

- [x] `WM_SETCURSOR` → `SetCursor(NULL)`: the OS arrow vanishes over the
      glasses while the mouse stays fully tracked
- [x] `VirtualCursor` — the cursor's position as `(u, v)` on the panel
- [x] Cursor quad drawn 1.5 mm in front of the panel (no z-fighting), once
      per eye for the side-by-side stereo mode
- [x] Differential tracking — one mouse count moves the cursor one content
      pixel, the familiar 1:1 feel of an ordinary monitor
- [x] Re-seed by ray-cast when the pointer enters the glasses' display, so
      the cursor starts exactly where the mouse is pointing
- [x] CDP input: move, click, wheel, modifiers, drag
- [x] The keystroke must not reach the projected app twice — the physical
      key is swallowed when it is genuinely being delivered over the protocol
- [x] Keyboard shaping rules (a release is always `keyUp`; Ctrl/Alt suppress
      the character; the text field is absent, never empty, for a
      non-printable key)
- [x] **Hardware verified**: the click landed at CSS `(400, 299)` for a
      target of `(400, 300)` — a 1 CSS px error — and the workbench of real
      VS Code received exactly one `keydown/keypress/keyup` for one physical
      keystroke, all `isTrusted=true`

## M9 — Niceties
- [ ] WebSocket emitter (browser/Three.js demo)
- [ ] CSV/JSON logging of the live pose for offline analysis
- [ ] Drift visualisation tool (read CSV → plot)
- [ ] Virtual mouse (smooth-pan) output
- [ ] Low-pass filter presets (game / sim / cinematic)
- [ ] Fullscreen immersive mode (panel fills the whole Micro-OLED)

## Backlog
- Support **Rokid Air** (same IMU? different HID report?)
- Linux/Wine compatibility
- Telemetry overlay (HUD-style)
- Anti-cheat-friendly mode (no mouse output, only FreeTrack/UDP)
