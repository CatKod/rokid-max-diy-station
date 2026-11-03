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
// THE DEFAULT
// -----------
// A standard Windows arrow subtends roughly 1 degree of visual angle
// at reading distance.  At the panel's default distance of 2 m, 1
// degree is about 35 mm; the default cursor is a little smaller than
// that, so it does not dominate the content it is pointing at.
//
// The aspect ratio (width : height) is that of the arrow's own
// texture, so the arrow is never stretched - it is scaled uniformly.
//
// WHY THE SIZE IS A FRACTION OF THE PANEL'S WIDTH
// ------------------------------------------------
// The obvious implementation - a fixed size in metres - has a hidden
// assumption baked into it: that the PANEL never changes size.  The
// panel's angular size is the user's "panel scale" setting (25 % to
// 100 % of the glasses' field of view), and a fixed-size arrow would
// silently change its apparent size relative to the content when that
// setting moved.  At 100 % the arrow would be half the relative size
// it was at 50 %; at 25 % it would be twice as big.  The user would
// have to compensate with the cursor-size slider every time they
// changed the panel scale, and the two settings would silently fight
// each other.
//
// So the arrow's size is expressed as a FRACTION OF THE PANEL'S WIDTH.
//
//  * Changing the panel's scale keeps the arrow the same size RELATIVE
//    to the content, which is what "the arrow is a pointer into this
//    picture" means.
//
//  * Changing the panel's DISTANCE does not change the arrow at all,
//    because the panel's angular size does not depend on the distance
//    (see RokidDisplayProfile.PanelWidth): a panel twice as far away
//    is twice as big in metres and subtends the same angle.  Scaling
//    the arrow with the panel keeps its ANGULAR size constant, so the
//    user sees no change - which is correct.
//
//  * Changing the CONTENT's aspect ratio does not change the arrow
//    either, because the panel's WIDTH does not depend on it.  This is
//    why the fraction is of the width and not of the height.
//
// The reference numbers below describe the arrow at the reference
// panel - the app's default of 2 m and 50 % of the field of view - so
// the default appearance is bit-for-bit what it has always been.
using System.Numerics;

namespace RokidDiyStation.Core.Spatial;

/// <summary>
/// The virtual cursor's size, as a scale factor on the default arrow.
/// </summary>
public static class CursorSize
{
    /// <summary>
    /// The default cursor's width in metres, at the reference panel.
    /// </summary>
    public const float DefaultWidthMetres = 0.020f;

    /// <summary>
    /// The default cursor's height in metres, at the reference panel.
    /// </summary>
    public const float DefaultHeightMetres = 0.026f;

    /// <summary>
    /// The panel distance the default arrow was sized against.
    /// </summary>
    /// <remarks>
    /// This is the app's default panel distance (see
    /// <c>AppSettings.PanelDistanceMetres</c>).  It only matters
    /// because the arrow's size is anchored to the reference panel.
    /// </remarks>
    public const float ReferencePanelDistanceMetres = 2.0f;

    /// <summary>
    /// The fraction of the field of view the reference panel covered.
    /// </summary>
    /// <remarks>
    /// This is the app's default panel scale - 50 % of the glasses'
    /// horizontal field of view - and the value the hard-coded arrow
    /// size was originally measured against.
    /// </remarks>
    public const float ReferencePanelFovFraction = 0.5f;

    /// <summary>
    /// The reference panel's width, in metres.
    /// </summary>
    /// <remarks>
    /// Computed from the same profile the anchor service uses, so the
    /// arrow's reference and the panel it is drawn on cannot drift
    /// apart.
    /// </remarks>
    public static readonly float ReferencePanelWidthMetres =
        RokidDisplayProfile.PanelWidth(
            ReferencePanelDistanceMetres, ReferencePanelFovFraction);

    /// <summary>
    /// The default arrow's width as a fraction of the panel's width.
    /// </summary>
    public static float DefaultWidthFraction => DefaultWidthMetres / ReferencePanelWidthMetres;

    /// <summary>
    /// The default arrow's height as a fraction of the panel's width.
    /// </summary>
    public static float DefaultHeightFraction => DefaultHeightMetres / ReferencePanelWidthMetres;

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
    /// The cursor's width in metres, for a given scale, on the
    /// reference panel.
    /// </summary>
    /// <param name="scale">The scale factor; clamped into range.</param>
    public static float WidthMetres(float scale) => WidthMetres(scale, ReferencePanelWidthMetres);

    /// <summary>
    /// The cursor's width in metres, for a given scale, on a panel of
    /// the given width.
    /// </summary>
    /// <param name="scale">The scale factor; clamped into range.</param>
    /// <param name="panelWidthMetres">The panel's width, in metres.</param>
    public static float WidthMetres(float scale, float panelWidthMetres)
        => SafeWidth(panelWidthMetres) * DefaultWidthFraction * Clamp(scale);

    /// <summary>
    /// The cursor's height in metres, for a given scale, on the
    /// reference panel.
    /// </summary>
    /// <param name="scale">The scale factor; clamped into range.</param>
    public static float HeightMetres(float scale) => HeightMetres(scale, ReferencePanelWidthMetres);

    /// <summary>
    /// The cursor's height in metres, for a given scale, on a panel of
    /// the given width.
    /// </summary>
    /// <param name="scale">The scale factor; clamped into range.</param>
    /// <param name="panelWidthMetres">The panel's width, in metres.</param>
    public static float HeightMetres(float scale, float panelWidthMetres)
        => SafeWidth(panelWidthMetres) * DefaultHeightFraction * Clamp(scale);

    /// <summary>
    /// The cursor's size in metres, for a given scale, on the
    /// reference panel.
    /// </summary>
    /// <param name="scale">The scale factor; clamped into range.</param>
    public static (float Width, float Height) SizeMetres(float scale)
        => SizeMetres(scale, ReferencePanelWidthMetres);

    /// <summary>
    /// The cursor's size in metres, for a given scale, on a panel of
    /// the given width.
    /// </summary>
    /// <remarks>
    /// This is the form the renderer uses.  The panel's width is what
    /// the arrow's size is measured against, so the arrow keeps its
    /// size relative to the content no matter how the user scales the
    /// panel.
    /// </remarks>
    /// <param name="scale">The scale factor; clamped into range.</param>
    /// <param name="panelWidthMetres">The panel's width, in metres.</param>
    public static (float Width, float Height) SizeMetres(float scale, float panelWidthMetres)
    {
        var width = SafeWidth(panelWidthMetres);
        return (width * DefaultWidthFraction * Clamp(scale),
                width * DefaultHeightFraction * Clamp(scale));
    }

    /// <summary>
    /// The half-extents of the cursor quad, for a given scale, on the
    /// reference panel.
    /// </summary>
    /// <param name="scale">The scale factor; clamped into range.</param>
    public static (Vector3 HalfWidth, Vector3 HalfHeight) HalfExtents(float scale)
        => HalfExtents(scale, ReferencePanelWidthMetres);

    /// <summary>
    /// The half-extents of the cursor quad, for a given scale, on a
    /// panel of the given width.
    /// </summary>
    /// <remarks>
    /// This is what the renderer actually needs: the quad is built from
    /// half-widths along the viewer's right axis and half-heights
    /// along the panel's up axis.
    /// </remarks>
    /// <param name="scale">The scale factor; clamped into range.</param>
    /// <param name="panelWidthMetres">The panel's width, in metres.</param>
    public static (Vector3 HalfWidth, Vector3 HalfHeight) HalfExtents(float scale, float panelWidthMetres)
    {
        var (w, h) = SizeMetres(scale, panelWidthMetres);
        return (new Vector3(w * 0.5f, 0f, 0f), new Vector3(0f, h * 0.5f, 0f));
    }

    /// <summary>
    /// A panel width that is safe to divide by.
    /// </summary>
    /// <remarks>
    /// The panel's width is never zero in practice, but the renderer
    /// reads the pose from a struct that is default-initialised before
    /// the first anchor, and a zero width must not produce a
    /// zero-sized - that is, invisible - cursor.
    /// </remarks>
    private static float SafeWidth(float panelWidthMetres)
        => Math.Max(0.001f, panelWidthMetres);
}
