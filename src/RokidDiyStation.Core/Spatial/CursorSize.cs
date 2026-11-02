// SPDX-License-Identifier: MIT
//
// The size of the virtual 3D cursor.
//
// WHY THIS IS ITS OWN CLASS
// -------------------------
// The cursor used to be two hard-coded constants in the renderer:
//
//     CursorWidthMetres  = 0.020f
//     CursorHeightMetres = 0.026f
//
// which meant the user could not change it.  But the right size for
// the arrow depends on something only the user knows: how big it looks
// to THEM, on THEIR face, at THEIR reading distance.
//
// Two users wearing the same glasses can want very different arrows.
// One may be looking at a spreadsheet and wants a small, precise
// pointer; another is reading large text in a browser and wants a big
// obvious one.  Neither is "wrong" - it is a matter of taste and of
// what they are doing.
//
// The unit of adjustment is a SCALE FACTOR, not a size in metres,
// because a size in metres would mean something different at every
// panel distance.  The panel's width is fixed by the FOV, so the
// panel's HEIGHT changes with the content's aspect ratio - and a
// cursor that is a fixed fraction of the panel's height would then
// change size when the user resized the virtual display, which is
// exactly the kind of surprise the "the panel recentres itself" bug
// was about.
//
// So the scale is relative to the DEFAULT cursor size, and the
// default is a fraction of the panel's HEIGHT.  That keeps the arrow
// the same size on screen no matter what the content's shape is.
//
// THE DEFAULT
// -----------
// A standard Windows arrow subtends roughly 1 degree of visual angle
// at reading distance.  At the panel's default distance of 2 m, 1
// degree is about 35 mm.  The default cursor is a little smaller than
// that, so it does not dominate the content it is pointing at.
//
// The aspect ratio (width : height) is that of the arrow's own
// texture, so the arrow is never stretched - it is scaled uniformly.

using System.Numerics;

namespace RokidDiyStation.Core.Spatial;

/// <summary>
/// The virtual cursor's size, as a scale factor on the default arrow.
/// </summary>
public static class CursorSize
{
    /// <summary>The default cursor's width in metres.</summary>
    public const float DefaultWidthMetres = 0.020f;

    /// <summary>The default cursor's height in metres.</summary>
    public const float DefaultHeightMetres = 0.026f;

    /// <summary>
    /// The smallest scale the user may choose.
    /// </summary>
    /// <remarks>
    /// Below this the arrow's tip is too small to hit accurately with
    /// a mouse - at some point the user is better off with the OS
    /// cursor on the glasses' own display.
    /// </remarks>
    public const float MinScale = 0.25f;

    /// <summary>
    /// The largest scale the user may choose.
    /// </summary>
    /// <remarks>
    /// At this size the arrow is a quarter of the panel's height and
    /// would cover the content it is pointing at.  Larger than this
    /// stops being a pointer and starts being an obstruction.
    /// </remarks>
    public const float MaxScale = 4.0f;

    /// <summary>The scale used when the user has expressed no preference.</summary>
    public const float DefaultScale = 1.0f;

    /// <summary>
    /// Clamp a requested scale into the supported range.
    /// </summary>
    /// <remarks>
    /// This is applied everywhere a scale is read, not only in the UI,
    /// so that a hand-edited settings file cannot produce a zero-sized
    /// or absurdly large cursor.
    /// </remarks>
    /// <param name="scale">The requested scale.</param>
    public static float Clamp(float scale)
    {
        if (!float.IsFinite(scale)) return DefaultScale;
        return Math.Clamp(scale, MinScale, MaxScale);
    }

    /// <summary>
    /// The cursor's width in metres, for a given scale.
    /// </summary>
    /// <param name="scale">The scale factor; clamped into range.</param>
    public static float WidthMetres(float scale)
        => DefaultWidthMetres * Clamp(scale);

    /// <summary>
    /// The cursor's height in metres, for a given scale.
    /// </summary>
    /// <param name="scale">The scale factor; clamped into range.</param>
    public static float HeightMetres(float scale)
        => DefaultHeightMetres * Clamp(scale);

    /// <summary>
    /// The cursor's size in metres, for a given scale.
    /// </summary>
    /// <param name="scale">The scale factor; clamped into range.</param>
    public static (float Width, float Height) SizeMetres(float scale)
        => (WidthMetres(scale), HeightMetres(scale));

    /// <summary>
    /// The half-extents of the cursor quad, for a given scale.
    /// </summary>
    /// <remarks>
    /// This is what the renderer actually needs: the quad is built from
    /// half-widths along the viewer's right axis and half-heights
    /// along the panel's up axis.
    /// </remarks>
    /// <param name="scale">The scale factor; clamped into range.</param>
    public static (Vector3 HalfWidth, Vector3 HalfHeight) HalfExtents(float scale)
    {
        var (w, h) = SizeMetres(scale);
        return (new Vector3(w * 0.5f, 0f, 0f), new Vector3(0f, h * 0.5f, 0f));
    }
}
