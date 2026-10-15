# Protocol reverse-engineering — sources and credits

The byte layout in `docs/PROTOCOL.md` was not derived independently — it is the
distilled consensus of two existing reverse-engineering efforts. Both are
credited below; both are MIT-licensed; this project is happy to stand on their
shoulders so the community can move on to the actually-hard part (good
fusion, good UX).

## Upstream projects

### `xXJSONDeruloXx/rokid-spatial-macos`
- URL: https://github.com/xXJSONDeruloXx/rokid-spatial-macos
- License: MIT
- Platform: macOS (Python + `hid` / hidapi)
- What we reused:
  - VID/PID mapping table (`constants.py`)
  - HID report-ID 0x11 as the IMU stream marker
  - 64-byte packet layout (timestamp, accel, gyro, mag) — verified in test
    fixtures shipped with the project
- What we did **not** reuse: the macOS-specific `hid.Device` wrapper. Our
  equivalent in `src/RokidDiyStation.Core/HidCapture/RokidHidDevice.cs`
  targets Mike O'Brien's `HidLibrary` (Windows-only).

### `angkaworn-dotcom/rokid-spatial`
- URL: https://github.com/angkaworn-dotcom/rokid-spatial
- License: MIT
- Platform: macOS (Swift + IOKit)
- What we reused:
  - The `PROTOCOL.md` writeup — used as the primary source of field offsets
    and the canonical stationary+worn hex fixture.
  - The accelerometer/gyroscope/magnetometer coordinate mapping.
  - The "drop the magnetometer indoors" observation (verbatim reasoning, with
    slight rewording).
  - The fusion design intuition from `Sources/RokidKit/Fusion.swift` — the
    Mahony-style complementary filter with stationary-detected bias
    estimation, an explicit 6-second calibration, and a recenter/look-ahead
    API. We have not ported this yet (M3 work).
- What we did **not** reuse: the macOS-only `IOHIDManager` plumbing and any
  display-driver code (CG virtual display, etc.) — those are out of scope.

## Other projects consulted

- `badicsalex/ar-drivers-rs` (MIT, Rust) — referenced in the upstream
  `PROTOCOL.md` for the USB control-transfer shape used by display-mode
  commands (`bmRequestType=vendor`, `bRequest=0x01`). Not yet ported here —
  M4/M5 work if/when we add display control.
- `wheaney/XRLinuxDriver` (MIT) and `wheaney/breezy-desktop` (MIT) — Linux
  userspace driver for similar AR glasses, mentioned as architectural
  inspiration only.

## How to verify locally

The `tests/RokidDiyStation.Core.Tests/PacketParserTests.cs` file contains the
exact hex bytes from the `angkaworn-dotcom/rokid-spatial` PROTOCOL.md sample
packet as a C# byte fixture. If the test fails after a future protocol
update, both upstream projects should be re-checked first; the Rokid Max
firmware has historically been stable on the IMU interface.

## License compliance

Both upstream projects are MIT. Their LICENSE texts are not redistributed
with this repository; the links above are sufficient under MIT terms.
Substantial code reuse is limited to numerical constants (VID/PID/offsets)
and the single canonical sample packet, neither of which is copyrightable
expression.