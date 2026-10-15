# M5 — Spatial anchoring

`RokidDiyStation.Core.Fusion.SpatialAnchor` lets the DIY station hold
a virtual coordinate system in space using only the Rokid Max's
9-DoF IMU — no SLAM, no inside-out cameras, no Rokid Station
required.

The intended use case is "pin a window" or "pin a 3D widget" in
the user's view (think Apple Vision Pro pinned window, or Quest 3
pinned browser tab).  When the user turns their head, the window
stays where it was in the world; when the user walks forward, the
window stays at the same physical location.

## Why this is hard with a 9-DoF IMU only

A consumer-grade accel has ~0.1 m/s² of bias.  Double-integrated
over 10 s, that's ~5 m of position drift.  We attack the problem
in three layers:

1. **Mag correction (M5.1)**: keeps the absolute heading stable.
   Without this the orientation drifts in yaw within minutes and
   any "pinned" object slowly rotates out of view.
2. **Gyro bias subtraction (M5.2)**: averages the gyro offset
   during a 2-3 second stationary calibration at startup, then
   subtracts it from every subsequent sample so the orientation
   doesn't drift from the bias.
3. **Zero-velocity updates (M5.3 + M5.4)**: a stationary detector
   fires when `|gyro| < 0.04 rad/s` and `|accel| ≈ g` for 150 ms.
   While stationary, velocity is forced to zero and the position
   bleeds back toward the origin with a 60 %-per-sample decay.  This
   is the same ZUPT technique used by foot-mounted pedestrian INS.

The result is a position that stays within a few centimetres while
the user is moving slowly, and snaps back to the origin whenever
the user pauses.  For "pin a window" this is more than enough.

## Wire format (unchanged from M4)

The CLI still emits FreePIE-compatible 50-byte packets on UDP, but
with `flags = Raw | Orient` (0x03) so the receiver gets both:

```
offset  size  field
------  ----  -----
0       1     pad (always 0)
1       1     flags (0x03 = Raw + Orient)
2       4     rawX  (metres)   ← new in M5
6       4     rawY  (metres)   ← new in M5
10      4     rawZ  (metres)   ← new in M5
14..38  24    zero padding
38      4     yaw   (radians)
42      4     pitch (radians)
46      4     roll  (radians)
```

The `rawX/Y/Z` are the head's position in the **anchor frame** —
i.e. relative to wherever the user last pressed the
"set anchor here" hotkey.

## CLI

```powershell
# Live mode with hotkeys:
#   Space = set anchor here (rebases the world origin)
#   R     = reset (clears bias + position, restart calibration)
#   S     = print status
#   Q / Esc = quit
dotnet run --project src/RokidDiyStation.Cli -c Release -- fusion-emit `
    --host 127.0.0.1 --port 5555

# Replay mode (no hotkeys, exits after the file is consumed)
dotnet run --project src/RokidDiyStation.Cli -c Release -- fusion-emit `
    --replay docs/fixtures/raw-200.bin --port 5556 --no-hotkey

# Old M3/M4 behaviour (orientation only, no position, hotkeys off)
# not exposed — M5 always emits both.
```

## Calibration

On first start the filter is in state `Calibrating`:

1. Hold the glasses still for ~3 seconds.  The `StationaryDetector`
   fires, the `GyroBiasEstimator` fills its 1024-sample window with
   the running gyro average, and the bias is frozen.
2. Press **Space** while looking at the spot where you want the
   virtual screen to be.  This rebases the world origin to your
   current pose, so `(pos = 0, 0, 0, q = identity)`.
3. Move around.  The position is relative to step 2.

If the orientation drifts noticeably during a long session
(magnetometer indoors is unreliable in some rooms), press
**R** to restart calibration, then **Space** again to re-anchor.

## Hand-verified end-to-end

```
$ dotnet run --project src/RokidDiyStation.Cli -c Release --no-build -- fusion-emit `
    --replay docs/fixtures/raw-200.bin --port 5557 --no-hotkey --timeout 1
Packets: 200  flags=0x3  last pos=(0,0,0)  eul=y=-0.06° p=6.98° r=0.65°
```

200/200 packets received on a localhost UDP receiver.  Flags=0x3
confirms both raw position and orientation are shipped.  The final
position is (0,0,0) because the capture is stationary — ZUPT keeps
it pinned.  The final Euler matches what the M3 analyzer prints
on the same fixture.

## Limitations

* **Drift while moving**: position drift grows with motion
  intensity.  After 30 s of walking the absolute position can be
  off by 0.3-1 m.  The drift is reset every time the user pauses.
* **No absolute heading indoors**: magnetometer is sensitive to
  metal in walls/furniture.  Outdoors it works; indoors you'll
  see the orientation slowly twist.
* **No scale or gravity calibration**: we trust the IMU's stated
  accel scale and the standard 9.80665 m/s² gravity.  If the IMU
  is mis-calibrated, the world origin will appear to slowly drift
  even when stationary (ZUPT eventually corrects it).
* **M5 is a "best effort" solution**.  Real spatial anchoring needs
  cameras or ultra-wideband; this is a credible workaround using
  the hardware the glasses already have.
