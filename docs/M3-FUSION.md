# M3 — Mahony AHRS fusion

`RokidDiyStation.Core.Fusion.MahonyFusion` is a port of the Mahony
complementary filter (Mahony et al. 2008) in the same form Madgwick uses
in [his reference implementation](https://x-io.co.uk/open-source-imu-and-ahrs-algorithms/).
It produces a **quaternion orientation** of the device frame relative to
its first-sample rest pose.

## What it consumes

A stream of `ImuSample` records (rad/s gyro, m/s² accel, optional µT
magnetometer).  The Rokid Max parser produces these at the device's
native rate (≈ 437 Hz, not the 90 Hz mentioned in the descriptor).

## What it emits

A `Quaternion Orientation` (w + xi + yj + zk).  Identity means the
device is in the orientation it had at the first sample.  Use the
`SyntheticImuSource.ToEulerZYX` helper to get roll / pitch / yaw in
degrees (Z-Y-X intrinsic, in Rokid's device frame: roll = Z, pitch = X,
yaw = Y).

## Design notes

* **Magnetometer is off by default.**  Indoors the Rokid Max mag is
  dominated by the metallic chassis; enabling it (`new MahonyFusion(useMagnetometer: true)`)
  makes heading behave more like a compass but trades off stability.
* **First-sample seeding.**  The first sample's accelerometer reading
  is used to initialise the quaternion so we don't get a 90° startup
  transient when the device isn't perfectly upright.
* **Bias estimation via integral term.**  The Ki gain drives a slow
  integral that absorbs gyro bias.  We treat the filter as "warm" after
  `MinSamplesForStableEstimate` (200) samples — about 0.5 s at 437 Hz.
* **Input validation.**  NaN accel/gyro, zero accel (free-fall), and
  out-of-range dt all return `FusionResult.Rejected` and leave state
  untouched.

## Tuning

`MahonyTuning.Default` uses `KpAccel=2.0`, `KiAccel=0.01`.  These are
the same values used in Madgwick's reference code; they work on the
Rokid Max without further adjustment.  Magnetometer gains (when
enabled) are `KpMag=1.0`, `KiMag=0.005`.

## Test fixtures

* `docs/fixtures/raw-10s-capture.txt` — 10 s, 4364 samples, taken on a
  real Rokid Max while wearing the glasses and rotating the head
  (left → right → up → down).
* `docs/fixtures/raw-200.bin` / `raw-full.bin` — same data, packed as
  little-endian `int64 ts + 3×f32 accel + 3×f32 gyro + 3×f32 mag +
  byte worn` (45 B per sample).  Copied into the test output directory
  by the test csproj.

## Sanity-checked behaviour (from the real 10 s capture)

```
t=1968925 ms  roll=  0.6°  pitch=  7.0°  yaw=   0.0°   ← stationary
t=1971414 ms  roll=-39.4°  pitch=  7.2°  yaw=   2.7°   ← turning left
t=1973405 ms  roll= 44.3°  pitch=  3.2°  yaw=   4.7°   ← turning right
t=1978888 ms  roll= -5.7°  pitch=  7.4°  yaw= -29.4°   ← final tilt
```

These match the described head motion.  The 7° forward pitch is the
rest orientation of the glasses on a human head.  The -29° yaw drift
at the end reflects the lack of mag correction during that final
window — expected and bounded by the 10 s capture length.
