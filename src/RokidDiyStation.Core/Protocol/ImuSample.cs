// SPDX-License-Identifier: MIT
//
// Parses the 64-byte Rokid Max HID report into a typed sensor sample.
//
// Layout (little-endian, byte offsets are absolute within the 64-byte
// report buffer; byte 0 is the HID report ID):
//
//   [ 0]  u8     report ID                — always 0x11
//   [ 1]  u64    timestamp ns             — device clock, since boot
//   [ 9]  f32[3] accelerometer (m/s²)
//   [21]  f32[3] gyroscope    (rad/s)
//   [33]  f32[3] magnetometer (µT)
//   [45..47] u8[3]  keys / proximity (last byte = proximity / "worn" bit)
//   [48]  u64    timestamp #2 (host clock) — ~10.95 ms earlier than timestamp #1
//
// See docs/PROTOCOL.md for the full spec and references.

namespace RokidDiyStation.Core.Protocol;

/// <summary>
/// One decoded Rokid Max HID report.
///
/// Coordinate axes in device frame (per PROTCOL.md's PROTOCOL.md empirical mapping):
///   X = pitch (nodding)
///   Y = yaw   (turning), also carries +9.58 m/s² gravity when worn
///   Z = roll  (tilting)
/// </summary>
/// <param name="TimestampNs">Nanoseconds since the glasses powered on.</param>
/// <param name="Accel">Accelerometer reading, m/s², device frame.</param>
/// <param name="Gyro">Gyroscope reading, rad/s, device frame.</param>
/// <param name="Mag">Magnetometer reading, µT, device frame. Unreliable indoors.</param>
/// <param name="Worn">True while the proximity sensor says the glasses are being worn.</param>
/// <param name="HostTimestampNs">Second timestamp embedded in the packet, host clock.</param>
public readonly record struct ImuSample(
    ulong TimestampNs,
    System.Numerics.Vector3 Accel,
    System.Numerics.Vector3 Gyro,
    System.Numerics.Vector3 Mag,
    bool Worn,
    ulong HostTimestampNs);