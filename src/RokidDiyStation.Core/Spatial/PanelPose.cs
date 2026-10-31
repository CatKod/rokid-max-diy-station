// SPDX-License-Identifier: MIT
//
// Spatial frame definitions for the 3DoF "pinned panel" renderer.
//
// Coordinate frames
// ----------------
//  * World frame (W): +X = right, +Y = up, +Z = towards the viewer.
//    This is the frame the anchor lives in.  It is right-handed.
//
//  * Device frame (D): the glasses' own IMU frame.  +X = right,
//    +Y = up, +Z = backwards.  See <see cref="DeviceFrame"/>.
//
//  * Eye frame (E): the virtual camera.  Differs from the device frame
//    only by the neck offset — the head's pivot is not the eye.
//
// The Rokid Max has no positional tracking, so the only thing we can
// recover is the ROTATION of the device frame relative to the world
// frame.  Everything else here is built on top of that single
// rotation.
//
// Why the neck model matters
// --------------------------
// A real eye orbits the cervical spine, it does not rotate in place.  If
// we use the device rotation directly as the camera rotation, then when
// the user tilts their head the virtual panel appears to swing sideways
// around the eye, and the panel's edges move much more than the head
// actually moved.  The neck model translates the rotation centre from the
// eye to the neck, which makes the panel's motion match what the user
// expects from a real object at a real distance.
//
//   p_eye = R * d_neck
//
// where d_neck is the vector from the neck pivot to the eye in the
// device's rest frame.  This is the same formulation used by Cardboard,
// OpenVR and OpenTrack.
using System.Numerics;

namespace RokidDiyStation.Core.Spatial;

/// <summary>
/// Where the virtual panel lives in the world, expressed in world
/// coordinates.  The panel is defined by its centre, the direction it
/// faces, its up vector, and its physical size.
/// </summary>
/// <param name="Center">World-space position of the panel's centre.</param>
/// <param name="Normal">Unit vector pointing out of the panel's face (towards the user).</param>
/// <param name="Up">Unit vector along the panel's vertical edge.</param>
/// <param name="Width">Panel width in metres.</param>
/// <param name="Height">Panel height in metres.</param>
public readonly record struct PanelPose(
    Vector3 Center,
    Vector3 Normal,
    Vector3 Up,
    float Width,
    float Height)
{
    /// <summary>
    /// A panel centred <paramref name="distance"/> metres in front of the
    /// viewer, facing back at them, with the given world size.
    /// </summary>
    public static PanelPose InFrontOf(Vector3 eye, Vector3 forward, Vector3 up, float distance, float width, float height)
        => new(eye + forward * distance, -forward, up, width, height);

    /// <summary>
    /// The panel's right edge direction.
    /// </summary>
    /// <remarks>
    /// For a right-handed world frame with the normal pointing out of
    /// the panel's face and "up" along its vertical edge, the right
    /// direction is cross(normal, up).  Check: with the normal at +Z
    /// (towards the viewer) and up at +Y, cross(+Z, +Y) = -X, which
    /// points to the viewer's LEFT — and a viewer facing a panel with
    /// its face towards them sees the panel's own right on their left.
    /// That is correct.
    /// </remarks>
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Normal, Up));

    /// <summary>The four corners, in order as seen from the front.</summary>
    public Vector3[] Corners()
    {
        var r = Right;
        var u = Vector3.Normalize(Up);
        var hx = r * (Width * 0.5f);
        var hy = u * (Height * 0.5f);
        return new[]
        {
            Center - hx - hy,
            Center + hx - hy,
            Center + hx + hy,
            Center - hx + hy,
        };
    }
}
