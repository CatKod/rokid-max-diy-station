// SPDX-License-Identifier: MIT
//
// The virtual camera: turning the head's rotation into an eye pose.
//
// A real eye orbits the cervical spine; it does not rotate in place.
// If we use the head's rotation directly as the camera's rotation,
// then when the user tilts their head the virtual panel appears to
// swing sideways around the eye, and the panel's edges travel much
// further than the head actually moved.  The result reads as a screen
// glued to the user's face — the exact opposite of the illusion we
// want.
//
// The kinematic neck model fixes this.  It keeps the pivot at the
// neck and moves the eye along an arc:
//
//     p_eye = R * d_neck
//
// where d_neck is the vector from the neck pivot to the eye, in the
// device's rest frame.
//
// The offset values are the ones Google Cardboard and Oculus LibOVR
// use, and they are documented in .research/c2/findings.md:
//
//     Cardboard (Google VR SDK, HeadTracker.java)
//         horizontal 0.080 m, vertical 0.075 m
//     LibOVR (OVR_CAPI.h)
//         OVR_DEFAULT_NECK_TO_EYE_HORIZONTAL 0.0805 m
//         OVR_DEFAULT_NECK_TO_EYE_VERTICAL   0.075 m
//
// Note the sign convention: in those SDKs "forward" is -Z, so the
// neck offset's forward component is NEGATIVE — the eye sits BEHIND
// the pivot, which is anatomically right (the neck is behind the
// eyes).  The Rokid device frame has the same layout (+Z backwards),
// so the same signed offset applies unchanged.
//
// The magnitude is small — about 11 cm of total travel — but that is
// enough.  At a 1.5 m panel distance, an 11 cm eye displacement
// changes the viewing angle by roughly 4 degrees, which is exactly
// the parallax that makes a real screen at that distance feel solid.
using System.Numerics;

namespace RokidDiyStation.Core.Spatial;

/// <summary>
/// The virtual camera, including the neck offset that turns the head's
/// rotation into the eye's position and orientation.
/// </summary>
public sealed class NeckModelCamera
{
    /// <summary>
    /// The neck offset used by Cardboard and LibOVR, expressed in the
    /// Rokid device frame: 7.5 cm up, 8 cm backwards from the pivot.
    /// </summary>
    public static readonly Vector3 DefaultNeckOffset = new(0f, 0.075f, 0.080f);

    private readonly Vector3 _neckOffset;

    /// <param name="neckOffset">
    /// Vector from the neck pivot to the eye, in the device's rest
    /// frame.  Defaults to <see cref="DefaultNeckOffset"/>.
    /// </param>
    /// <param name="scale">
    /// How much of the neck offset to apply, in 0..1.  0 means the
    /// camera rotates about the eye itself — pure rotation, no
    /// parallax.  See <see cref="SpatialAnchorOptions.NeckModelScale"/>
    /// for why a damped value is better than the full offset on 3DoF
    /// hardware.
    /// </param>
    public NeckModelCamera(Vector3? neckOffset = null, float scale = 1f)
    {
        var offset = neckOffset ?? DefaultNeckOffset;
        var clamped = Math.Clamp(scale, 0f, 1f);
        _neckOffset = offset * clamped;
        Scale = clamped;
    }

    /// <summary>The attenuation factor applied to the neck offset.</summary>
    public float Scale { get; }

    /// <summary>The neck offset in use, for display in the settings UI.</summary>
    public Vector3 NeckOffset => _neckOffset;

    /// <summary>
    /// Given the head orientation, return the eye's world position and
    /// the eye's viewing orientation.
    /// </summary>
    /// <param name="headOrientation">
    /// Rotation of the device frame relative to the world frame.
    /// </param>
    /// <param name="position">The eye's world position.</param>
    /// <param name="orientation">
    /// The eye's orientation — the device rotation, since the eye's
    /// optical axis is rigidly attached to the head.
    /// </param>
    public void Solve(in Quaternion headOrientation, out Vector3 position, out Quaternion orientation)
    {
        // The eye orbits the neck pivot: p_eye = pivot + R * d_neck.
        // With the pivot at the world origin this is just R * d_neck.
        position = Vector3.Transform(_neckOffset, headOrientation);
        orientation = headOrientation;
    }

    /// <summary>
    /// The direction the eye is looking, in world coordinates.
    /// </summary>
    /// <remarks>
    /// The device's forward axis is -Z (see <see cref="DeviceFrame"/>).
    /// The quaternion maps device vectors into the world frame, so the
    /// world's forward direction is q * (-Z).
    /// </remarks>
    public static Vector3 Forward(in Quaternion orientation)
        => Vector3.Transform(DeviceFrame.Forward, orientation);

    /// <summary>
    /// The direction the eye is looking, in world coordinates.
    /// </summary>
    public Vector3 LookDirection(in Quaternion orientation) => Forward(orientation);

    /// <summary>
    /// The direction the top of the head points, in world coordinates.
    /// </summary>
    public static Vector3 Up(in Quaternion orientation)
        => Vector3.Transform(DeviceFrame.Up, orientation);
}
