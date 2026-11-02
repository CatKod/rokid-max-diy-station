// SPDX-License-Identifier: MIT
//
// The virtual panel's anchor: where the panel lives in the world, and
// how the head's pose relates to it.
//
// This is the object that makes "a screen pinned in space" real on
// 3DoF hardware.  The Rokid Max has no positional tracking and no
// cameras, so we can only recover the head's ROTATION.  A pinned panel
// therefore cannot be a rigid object at a fixed world position — there
// is nothing to anchor it to.  What we do instead is anchor the panel
// to a fixed world DIRECTION and DISTANCE, measured from the eye at
// the moment the user set the anchor.  The panel then stays put in the
// room while the user looks around it, which is the behaviour that
// reads as "spatial" rather than "head-locked".
//
// The panel's world pose is RIGID: once anchored, its centre, normal
// and up axis never change.  That is deliberate, and it is the whole
// point.  A real screen hanging on a wall keeps its orientation when
// you turn your head; you see it at an angle.  If we instead kept
// re-aiming the panel's normal at the eye every frame, the panel
// would behave like a billboard — it would always face you dead-on,
// and the illusion of depth would collapse.
//
// The one thing that does move is the eye, via the kinematic neck
// model: the head pivots about the cervical spine, not about the eye,
// so rotating the head moves the eye through a small arc.  That arc
// is what gives the panel its correct parallax as the user tilts and
// turns their head.
//
// See .research/c2/findings.md for the full derivation, the size table
// and the sources for the neck offset.
using System.Numerics;

namespace RokidDiyStation.Core.Spatial;

/// <summary>
/// A world-anchored virtual panel with a rigid pose.
/// </summary>
public sealed class PinnedPanel
{
    private readonly NeckModelCamera _camera;

    /// <summary>
    /// The direction from the eye to the panel, in world coordinates.
    /// This is the anchor's defining property: it never changes.
    /// </summary>
    public Vector3 WorldDirection { get; private set; }

    /// <summary>The distance from the anchor's eye position to the panel, in metres.</summary>
    public float Distance { get; }

    /// <summary>The panel's centre in world coordinates, fixed at anchor time.</summary>
    public Vector3 Center { get; private set; }

    /// <summary>
    /// The panel's face normal (pointing back towards the user at anchor
    /// time).  Rigid: it does not follow the eye.
    /// </summary>
    public Vector3 Normal { get; private set; }

    /// <summary>The panel's up axis, locked to world gravity.  Rigid.</summary>
    public Vector3 Up { get; private set; }

    /// <summary>Panel width in metres.</summary>
    public float Width { get; }

    /// <summary>Panel height in metres.</summary>
    public float Height { get; }

    /// <summary>
    /// The eye's world position at anchor time.
    /// </summary>
    /// <remarks>
    /// Recovered from the panel's centre and direction.  It is the
    /// pivot the lazy-follow rotation turns around, so that a panel
    /// that follows the user's gaze swings around the point where it
    /// was pinned rather than teleporting.
    /// </remarks>
    public Vector3 EyeAtAnchor => Center - WorldDirection * Distance;

    /// <summary>
    /// Create a panel anchored at the given eye pose and gaze direction.
    /// </summary>
    /// <param name="eyePosition">The eye's world position at anchor time.</param>
    /// <param name="gazeDirection">
    /// The direction the user is looking, in world coordinates.  This is
    /// the direction the panel is pinned to.
    /// </param>
    /// <param name="distance">How far away the panel sits, in metres.</param>
    /// <param name="width">Panel width in metres.</param>
    /// <param name="height">Panel height in metres.</param>
    /// <param name="camera">The neck-model camera used to resolve the eye pose.</param>
    /// <param name="fallbackUp">
    /// Up axis to use when the panel faces straight up or down, in which
    /// case gravity cannot define the panel's up.
    /// </param>
    public PinnedPanel(
        Vector3 eyePosition,
        Vector3 gazeDirection,
        float distance,
        float width,
        float height,
        NeckModelCamera? camera = null,
        Vector3? fallbackUp = null)
    {
        _camera = camera ?? new NeckModelCamera();

        WorldDirection = NormalizeOr(gazeDirection, -Vector3.UnitZ);
        Distance = MathF.Max(0.05f, distance);
        Width = MathF.Max(0.01f, width);
        Height = MathF.Max(0.01f, height);

        Center = eyePosition + WorldDirection * Distance;
        Normal = -WorldDirection;
        Up = RokidDisplayProfile.GravityLockedUp(Normal, fallbackUp ?? Vector3.UnitY);
    }

    /// <summary>
    /// Re-anchor the panel to the eye's current pose and gaze direction.
    /// The panel's size and distance are preserved.
    /// </summary>
    /// <param name="eyePosition">The eye's current world position.</param>
    /// <param name="gazeDirection">The direction the user is currently looking.</param>
    public void Reanchor(Vector3 eyePosition, Vector3 gazeDirection)
    {
        WorldDirection = NormalizeOr(gazeDirection, -Vector3.UnitZ);
        Center = eyePosition + WorldDirection * Distance;
        Normal = -WorldDirection;
        Up = RokidDisplayProfile.GravityLockedUp(Normal, Vector3.UnitY);
    }

    /// <summary>
    /// A copy of this panel with a different size, keeping the same
    /// world pose.
    /// </summary>
    /// <remarks>
    /// Used when the CONTENT's shape changes - the user resized the
    /// virtual display - and the panel has to follow it.
    ///
    /// The pose is preserved EXACTLY: the panel stays where it is in
    /// the room and only changes size.  Re-anchoring instead would move
    /// the panel to wherever the user happens to be looking, which is
    /// the same "it recentres itself" complaint in a different guise.
    ///
    /// The constructor derives the centre from the eye and the
    /// direction, so handing it the anchor-time eye reproduces the same
    /// centre exactly.
    /// </remarks>
    /// <param name="width">The new panel width, in metres.</param>
    /// <param name="height">The new panel height, in metres.</param>
    public PinnedPanel WithSize(float width, float height)
        => new(
            EyeAtAnchor,
            WorldDirection,
            Distance,
            MathF.Max(0.01f, width),
            MathF.Max(0.01f, height),
            _camera);

    /// <summary>
    /// The eye's world position for the given head orientation.
    /// </summary>
    public Vector3 EyePosition(in Quaternion headOrientation)
    {
        _camera.Solve(headOrientation, out var pos, out _);
        return pos;
    }

    /// <summary>
    /// The direction the eye is looking, for the given head orientation.
    /// </summary>
    public Vector3 GazeDirection(in Quaternion headOrientation)
        => _camera.LookDirection(headOrientation);

    /// <summary>
    /// The panel's pose.  Rigid — independent of the head's orientation.
    /// </summary>
    public PanelPose Pose => new(Center, Normal, Up, Width, Height);

    /// <summary>
    /// The panel's four corners in world coordinates.
    /// Order: bottom-left, bottom-right, top-right, top-left as seen
    /// from the user's side at anchor time.
    /// </summary>
    public Vector3[] Corners() => Pose.Corners();

    /// <summary>
    /// The panel's pose, but with its facing direction replaced.
    /// </summary>
    /// <remarks>
    /// The panel's true pose is immutable — that is what makes it a
    /// world-anchored object rather than a billboard.  The lazy follow
    /// needs to rotate the panel slightly towards the user, though,
    /// and it must do so about the point where the panel was pinned,
    /// not about its own centre.  Otherwise the panel would slide
    /// sideways across the room as it follows, which reads as the
    /// screen sliding rather than the screen being nudged around.
    ///
    /// So: the panel's centre is recomputed as
    ///
    ///     centre = eye_at_anchor + direction * distance
    ///
    /// and the up axis is re-derived from gravity against the new
    /// normal, so the panel stays level in the room.
    /// </remarks>
    /// <param name="direction">The panel's new facing direction (world).</param>
    public PanelPose PoseWithDirection(Vector3 direction)
    {
        var dir = NormalizeOr(direction, WorldDirection);
        var center = EyeAtAnchor + dir * Distance;
        var normal = -dir;
        var up = RokidDisplayProfile.GravityLockedUp(normal, Up);
        return new PanelPose(center, normal, up, Width, Height);
    }

    private static Vector3 NormalizeOr(Vector3 v, Vector3 fallback)
        => v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : fallback;
}
