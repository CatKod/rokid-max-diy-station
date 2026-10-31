// SPDX-License-Identifier: MIT
//
// The Rokid Max's device (IMU) frame.
//
// This is the frame the raw accelerometer, gyroscope and magnetometer
// readings arrive in, and every piece of spatial code has to agree on
// what it means.
//
// The convention is "RUB":
//
//     +X = right   (across the lenses, towards the wearer's right ear)
//     +Y = up      (along the temples)
//     +Z = backwards (towards the back of the wearer's head)
//
// which is right-handed, and which matches the Android sensor
// coordinate system that AR glasses drivers conventionally use
// (ar-drivers-rs documents it as "RUB" for exactly this device).
//
// Three consequences matter downstream:
//
//  * The lenses point along -Z, not +Z.  Every "where is the user
//    looking" calculation needs this.
//  * The accelerometer reads the REACTION to gravity, so a device
//    held level reports about +9.8 m/s^2 on Y.
//  * Rotation about Y is yaw (turning the head), about X is pitch
//    (nodding) and about Z is roll (tilting the head sideways).
//
// Verified against the real 10 s capture in
// docs/fixtures/raw-10s-capture.txt: the accelerometer's mean is
// (0.19, 9.45, 1.27) m/s^2 — gravity on Y, as this says it should be
// — and the gyro's largest excursions are on Y during the turn
// segments and on X during the up/down segments.
using System.Numerics;

namespace RokidDiyStation.Core.Spatial;

public static class DeviceFrame
{
    /// <summary>Right, in the device frame.</summary>
    public static readonly Vector3 Right = new(1f, 0f, 0f);

    /// <summary>Up, in the device frame.</summary>
    public static readonly Vector3 Up = new(0f, 1f, 0f);

    /// <summary>Backwards, in the device frame.</summary>
    public static readonly Vector3 Backward = new(0f, 0f, 1f);

    /// <summary>
    /// The direction the lenses point, in the device frame.
    /// </summary>
    /// <remarks>
    /// +Z points backwards, so the viewing direction is -Z.  This is
    /// the same convention OpenGL and OpenXR use for a camera.
    /// </remarks>
    public static readonly Vector3 Forward = new(0f, 0f, -1f);
}
