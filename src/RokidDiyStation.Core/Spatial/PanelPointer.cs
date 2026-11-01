// SPDX-License-Identifier: MIT
//
// The geometry behind the virtual 3D cursor.
//
// A physical mouse lives in the DISPLAY frame (it is head-locked: the
// Windows cursor sits at a fixed pixel on whichever output the
// pointer is over).  The pinned panel lives in the WORLD frame (it is
// world-locked: it stays where the user put it).  To drive a cursor on
// the panel we have to move between those two frames, and this class
// is the whole of that conversion.
//
// Three steps:
//   1. a display pixel -> a ray in world space (unprojection),
//   2. a ray -> the point where it meets the panel's plane,
//   3. that point -> the panel's texture coordinates (u, v).
//
// Conventions (matching the rest of the codebase)
// -----------------------------------------------
//  * System.Numerics is ROW-vector: v' = v * M, and M1 * M2 applies
//    M1 first.  The world -> clip chain is therefore
//    v_clip = v_world * view * projection.
//  * NDC: x in [-1, 1] left to right, y in [-1, 1] bottom to top,
//    z in [0, 1].
//  * Display pixels: origin at the top-left of the glasses' screen.
//
// The u/v signs are the ones derived from the renderer's vertex
// layout, and they are worth stating explicitly because they are easy
// to get backwards:
//
//     u = 0 is the viewer's LEFT   (and the content's left edge)
//     v = 0 is the viewer's TOP    (and the content's top edge)
//
// The panel's "right" axis, as defined by <see cref="PanelPose.Right"/>,
// points towards the viewer's LEFT (it is cross(normal, up)), so a
// point to the viewer's left has a POSITIVE projection onto it.  That
// is why u = 0.5 - (d . right) / width and not the other way round.
using System.Numerics;

namespace RokidDiyStation.Core.Spatial;

/// <summary>
/// Conversions between display pixels, world rays, and the panel's
/// texture coordinates.
/// </summary>
public static class PanelPointer
{
    /// <summary>
    /// Turn a pixel in the glasses' display into a world-space ray.
    /// </summary>
    /// <param name="pixelX">Pixel column, 0 to width-1.</param>
    /// <param name="pixelY">Pixel row, 0 to height-1, origin top-left.</param>
    /// <param name="viewportWidth">The display's width in pixels.</param>
    /// <param name="viewportHeight">The display's height in pixels.</param>
    /// <param name="view">The camera's view matrix (world -> eye).</param>
    /// <param name="projection">The projection matrix (eye -> clip).</param>
    /// <param name="origin">Where the ray starts (on the near plane).</param>
    /// <param name="direction">The ray's unit direction.</param>
    /// <returns>False when the matrices are degenerate.</returns>
    public static bool TryUnproject(
        float pixelX, float pixelY,
        int viewportWidth, int viewportHeight,
        Matrix4x4 view, Matrix4x4 projection,
        out Vector3 origin, out Vector3 direction)
    {
        origin = Vector3.Zero;
        direction = Vector3.UnitZ;

        if (viewportWidth <= 0 || viewportHeight <= 0) return false;

        // The same chain the renderer uses: world -> eye -> clip.
        var worldToClip = view * projection;
        if (!Matrix4x4.Invert(worldToClip, out var clipToWorld))
            return false;

        var ndcX = 2f * pixelX / viewportWidth - 1f;
        var ndcY = 1f - 2f * pixelY / viewportHeight;

        var nearClip = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), clipToWorld);
        var farClip = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), clipToWorld);

        // The perspective divide.  A w of zero means the point is at
        // infinity, which happens when the ray is parallel to the
        // projection plane - reject it rather than produce NaNs.
        if (MathF.Abs(nearClip.W) < 1e-6f || MathF.Abs(farClip.W) < 1e-6f)
            return false;

        var nearPoint = new Vector3(nearClip.X / nearClip.W, nearClip.Y / nearClip.W, nearClip.Z / nearClip.W);
        var farPoint = new Vector3(farClip.X / farClip.W, farClip.Y / farClip.W, farClip.Z / farClip.W);

        var delta = farPoint - nearPoint;
        if (delta.LengthSquared() < 1e-12f) return false;

        origin = nearPoint;
        direction = Vector3.Normalize(delta);
        return true;
    }

    /// <summary>
    /// Intersect a ray with the panel's plane.
    /// </summary>
    /// <param name="origin">The ray's origin.</param>
    /// <param name="direction">The ray's unit direction.</param>
    /// <param name="planePoint">Any point on the plane.</param>
    /// <param name="planeNormal">The plane's unit normal.</param>
    /// <param name="hit">The intersection point, when there is one.</param>
    /// <returns>
    /// False when the ray is parallel to the plane, or when the plane is
    /// behind the ray's origin.
    /// </returns>
    public static bool TryIntersectPlane(
        Vector3 origin, Vector3 direction,
        Vector3 planePoint, Vector3 planeNormal,
        out Vector3 hit)
    {
        hit = Vector3.Zero;

        var denominator = Vector3.Dot(direction, planeNormal);
        if (MathF.Abs(denominator) < 1e-6f) return false;

        var t = Vector3.Dot(planePoint - origin, planeNormal) / denominator;
        if (t <= 1e-4f) return false;

        hit = origin + direction * t;
        return true;
    }

    /// <summary>
    /// The panel's texture coordinates for a point on its plane.
    /// </summary>
    /// <remarks>
    /// The result is NOT clamped: a point outside the panel legitimately
    /// produces a u or v outside [0, 1], which is how the caller learns
    /// that the ray missed.
    /// </remarks>
    public static Vector2 ToUv(Vector3 point, PanelPose pose)
    {
        var delta = point - pose.Center;
        var alongRight = Vector3.Dot(delta, pose.Right);
        var alongUp = Vector3.Dot(delta, Vector3.Normalize(pose.Up));

        var u = 0.5f - alongRight / pose.Width;
        var v = 0.5f - alongUp / pose.Height;
        return new Vector2(u, v);
    }

    /// <summary>
    /// The world position of a point given in the panel's texture
    /// coordinates.
    /// </summary>
    /// <param name="u">Horizontal texture coordinate, 0 at the viewer's left.</param>
    /// <param name="v">Vertical texture coordinate, 0 at the viewer's top.</param>
    /// <param name="pose">The panel's pose.</param>
    /// <param name="normalOffset">
    /// How far to lift the point off the panel's surface, in metres,
    /// along the normal (towards the viewer).  A small positive value
    /// keeps the cursor from z-fighting with the panel.
    /// </param>
    public static Vector3 FromUv(float u, float v, PanelPose pose, float normalOffset = 0f)
    {
        var right = pose.Right;
        var up = Vector3.Normalize(pose.Up);

        return pose.Center
             + right * ((0.5f - u) * pose.Width)
             + up * ((0.5f - v) * pose.Height)
             + pose.Normal * normalOffset;
    }

    /// <summary>
    /// The size of the panel's content, in the captured window's own
    /// pixels.
    /// </summary>
    /// <remarks>
    /// This is the denominator of the differential mapping: one source
    /// pixel of mouse travel moves the cursor by one source pixel of
    /// content.  Using the SOURCE size rather than the panel's
    /// on-screen size is what gives the pointer the familiar 1:1 feel
    /// of an ordinary monitor.
    /// </remarks>
    public static float ContentWidth(int sourceWidth) => Math.Max(1, sourceWidth);

    /// <summary>Height of the content in source pixels.</summary>
    public static float ContentHeight(int sourceHeight) => Math.Max(1, sourceHeight);
}
