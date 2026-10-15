# Rokid Max — USB protocol notes

Reverse-engineered payload layout for the **Rokid Max** AR glasses. This project
treats these values as the authoritative byte-level spec; any drift observed
on new hardware is treated as a bug, not a feature.

## Device identity

| | |
|---|---|
| Vendor ID | `0x04D2` ("Rokid Corporation Ltd.") |
| Product ID (Rokid Max) | `0x162F` |
| Transport | USB HID, `bInterfaceClass = 3` |
| Usage page | `0xFF00` (vendor-defined) |
| Report interval | 2000 µs (nominal 500 Hz; ~436 Hz observed in practice) |

Both product ID 0x162F (Rokid Max) and 0x2002 (Rokid Max 2) are accepted by the
enumerator; other PIDs are listed but filtered out at open time.

## HID report descriptor

```
0600ff 0901 a101
  8501 0901 15c5 25ff 7508 953f 9182   ; report 1  — OUTPUT, 63 bytes
  8502 0901 1500 25ff 7508 953f 8182   ; report 2  — INPUT,  63 bytes
  8503 0901 1500 25ff 7508 953f 8182   ; report 3  — INPUT,  63 bytes
  8504 0901 1500 25ff 7508 953f 8182   ; report 4  — INPUT,  63 bytes
  8511 0901 1500 25ff 7508 953f 8182   ; report 17 — INPUT,  63 bytes
c0
```

Report 17 is the only one that fires under normal use; reports 2/3/4 are
defined but stay silent. Their presence suggests Rokid's own SDK subscribes to
event types we are not consuming here.

## Report 17 — combined IMU packet

The report-ID byte is **byte 0** of the 64-byte buffer; it is also delivered
as the `reportID` argument of the HID callback, so either source can be used
to validate it.

| Offset | Type | Field | Notes |
|---:|---|---|---|
| 0 | u8 | report ID | always `0x11` |
| 1–8 | u64 LE | timestamp | nanoseconds since device boot |
| 9–20 | f32[3] LE | accelerometer | m/s² |
| 21–32 | f32[3] LE | gyroscope | rad/s |
| 33–44 | f32[3] LE | magnetometer | µT |
| 45–47 | u8 | keys / proximity | `0` when idle; proximity = wear detection |
| 48–55 | u64 LE | timestamp #2 | ~10.95 ms earlier than the first |

There is **no quaternion** in the stream. Orientation must be derived locally
by sensor fusion (Mahony / Madgwick / complementary) — see `docs/FUSION.md`
(will land with M3).

### Locating the fields

Three candidate offsets (5, 9, 13) all yield an acceleration magnitude near
9.8, because consecutive float triples overlap and alias. Offset 9 is the
correct one, and gyroscope is what disambiguates: with the glasses held
still, only `accel@9` leaves `gyro@21 ≈ (0.0025, -0.0029, 0.0013)`. The
alternatives imply a stationary device rotating at 2.27 and 43.2 rad/s
respectively, which is nonsense.

### Sample packet (glasses stationary, worn)

```
11 ba8ad99166010000 802262be 63531941 581411c0 9040213b 06a83fbb 88edab3a
   cdcc2cc2 3333a642 000055c2 000000 5d72329166010000 000000503c000000
```

Decodes to:

- timestamp = 1 540 045 245 114 ns (25.7 min uptime)
- accel = (-0.221, 9.583, -2.267) m/s², |a| = 9.85
- gyro = (0.0025, -0.0029, 0.0013) rad/s
- mag = (-43.2, 83.1, -53.25) µT, |m| = 107.7
- proximity byte = 0 → glasses not worn

This fixture is used verbatim by `tests/RokidDiyStation.Core.Tests/PacketParserTests.cs`.

## Sensor axes

Determined empirically by moving the head one axis at a time and watching
which component of the relative orientation responded:

| Device axis | Meaning | Evidence |
|---|---|---|
| **X** | pitch (nodding) | 16–61° swing while gravity-derived yaw stayed under 3° |
| **Y** | yaw (turning), and "up" | tracked derived yaw to within 1–2% across a 63° turn; also carries the +9.58 m/s² gravity reading |
| **Z** | roll (tilting) | by elimination |

Cross-axis isolation is reassuring: nodding moved pitch by 61° while yaw
stayed at 2°, so the fusion is not leaking one axis into another.

## Drift

Pitch and roll do not drift — gravity pins them. Yaw has no such reference,
so it is at the mercy of how well the gyroscope bias is estimated. Empirically,
the bias sits near 0.0025 rad/s (≈8.6°/min blind drift), so a stationary
detector + slow bias adaptation + an explicit six-second calibration at
startup is required for any meaningful session length.

## Magnetometer is unreliable indoors

The device magnetometer reads ~108 µT indoors, well outside Earth's 25–65 µT
field. The chip is either uncalibrated or picking up local interference.
Trusting it would inject worse error than it removes, so the planned fusion
(M3) drops yaw correction from `mag` entirely and relies on gyro bias
estimation alone.

## References

See `docs/PROTOCOL-REFERENCES.md` for the upstream reverse-engineering
sources this spec was derived from.