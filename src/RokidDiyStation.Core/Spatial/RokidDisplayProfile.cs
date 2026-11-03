// SPDX-License-Identifier: MIT
//
// Optical profile of the Rokid Max display.
//
// The glasses are a plain 1920x1080 monitor from the PC's point of view:
// a flat image sent over DisplayPort Alt Mode, viewed through Micro-OLED
// panels with a birdbath optical system.  The PC has to know how wide the
// image appears in the user's field of view, because that is what lets us
// place a virtual panel at a believable size and distance.
//
// Everything else in the renderer works in "what fraction of the display
// does this panel cover" terms, so the FOV only matters at two points:
//  * sizing a panel for a given distance, and
//  * building the projection matrix, so that the anchor lands where the
//    user is actually looking.
//
// Sources
// -------
// The 50-degree figure is the Rokid Max's published *diagonal* field of
// view.  For a 16:9 image the horizontal and vertical angles follow from
// the diagonal by trigonometry — see the derivation in the class comment
// of <see cref="HorizontalFovDegrees"/>.
//
// The diagonal is the right number to start from, but it is worth knowing
// that the figure is a vendor spec measured with lenses in place, so it
// carries a couple of degrees of uncertainty.  The FOV is therefore a
// user-adjustable setting: it scales the apparent size of the panel and
// shifts how far away the panel "feels", without breaking anything.
using System.Numerics;

namespace RokidDiyStation.Core.Spatial;

/// <summary>
/// Optical constants of the Rokid Max display, plus the helpers that
/// convert between a panel's physical size, its distance, and the
/// fraction of the field of view it covers.
/// </summary>
public static class RokidDisplayProfile
{
    /// <summary>Display resolution as seen by the host (both eyes, 2D mode).</summary>
    public const int Width = 1920;

    /// <summary>Display resolution as seen by the host (both eyes, 2D mode).</summary>
    public const int Height = 1080;

    /// <summary>Aspect ratio (width / height).</summary>
    public const float Aspect = 16f / 9f;

    /// <summary>
    /// The width of ONE eye's panel, in pixels.
    /// </summary>
    /// <remarks>
    /// The Rokid Max has two 1920x1080 Micro-OLED panels.  In the
    /// side-by-side 3D mode the host sees a single 3840x1080 monitor,
    /// and the bridge chip inside the glasses splits it at x = 1920,
    /// sending the left half to the left eye and the right half to the
    /// right eye.  So one eye's panel is always 1920x1080, whichever
    /// mode the glasses are in.
    /// </remarks>
    public const int EyeWidth = 1920;

    /// <summary>The height of ONE eye's panel, in pixels.</summary>
    public const int EyeHeight = 1080;

    /// <summary>
    /// True when the given back-buffer size is the side-by-side 3D
    /// layout: two 1920x1080 eyes packed side by side.
    /// </summary>
    /// <remarks>
    /// The test is on the ASPECT, not on the exact size, so it keeps
    /// working if the driver offers the mode at a different width.
    /// A 32:9 buffer (3840x1080) is the only thing the glasses
    /// produce that is not 16:9.
    /// </remarks>
    public static bool IsSideBySide(int width, int height)
        => width > EyeWidth && Math.Abs((float)width / height - 32f / 9f) < 0.05f;

    /// <summary>
    /// The width of the viewport that ONE eye receives.
    /// </summary>
    /// <remarks>
    /// In side-by-side mode each eye gets half the buffer; in the
    /// plain 2D mode the whole buffer goes to both eyes (the bridge
    /// clones it), so the eye's viewport is the whole thing.
    /// </remarks>
    public static int EyeViewportWidth(int backBufferWidth)
        => IsSideBySide(backBufferWidth, EyeHeight) ? backBufferWidth / 2 : backBufferWidth;

    /// <summary>
    /// The published diagonal field of view, in degrees.  See the class
    /// comment for the sources and the caveats.
    /// </summary>
    public const float DiagonalFovDegrees = 50f;

    /// <summary>
    /// Horizontal field of view in degrees, derived from the diagonal.
    /// </summary>
    /// <remarks>
    /// For a 16:9 rectangle with half-width w, half-height h and
    /// half-diagonal d = sqrt(w^2 + h^2), the diagonal half-angle theta_d
    /// satisfies tan(theta_d) = d / z for some eye distance z.  With
    /// w : h = 16 : 9 we have w = 16k, h = 9k, d = sqrt(337)k = 18.358k,
    /// so z = d / tan(theta_d) and
    ///
    ///     tan(theta_h / 2) = w / z = 16 / 18.358 * tan(theta_d)
    ///     tan(theta_v / 2) = h / z =  9 / 18.358 * tan(theta_d)
    ///
    /// With theta_d = 25 degrees that gives theta_h = 44.24 degrees and
    /// theta_v = 25.76 degrees.
    /// </remarks>
    public static float HorizontalFovDegrees(float diagonalFovDegrees = DiagonalFovDegrees)
    {
        var halfDiag = MathF.Tan(ToRadians(diagonalFovDegrees) * 0.5f);
        // w / d = 16 / sqrt(16^2 + 9^2)
        const float wOverD = 16f / 18.35755975f;
        return ToDegrees(MathF.Atan(wOverD * halfDiag) * 2f);
    }

    /// <summary>
    /// Vertical field of view in degrees, derived from the diagonal.
    /// </summary>
    public static float VerticalFovDegrees(float diagonalFovDegrees = DiagonalFovDegrees)
    {
        var halfDiag = MathF.Tan(ToRadians(diagonalFovDegrees) * 0.5f);
        const float hOverD = 9f / 18.35755975f;
        return ToDegrees(MathF.Atan(hOverD * halfDiag) * 2f);
    }

    /// <summary>
    /// The width, in metres, of a flat panel that is perpendicular to the
    /// line of sight at <paramref name="distance"/> metres and subtends
    /// <paramref name="fovFraction"/> of the horizontal field of view.
    /// </summary>
    /// <remarks>
    /// From the right triangle formed by the eye, the panel's centre and
    /// the panel's edge:
    ///
    ///     tan(theta_p / 2) = (W / 2) / D      =>      W = 2 D tan(theta_p / 2)
    ///
    /// where theta_p is the subtended angle.  With theta_p = f * theta_h
    /// this becomes W = 2 D tan(f * theta_h / 2).
    ///
    /// NOTE that the width does NOT depend on the distance in ANGULAR
    /// terms: the width grows with the distance in exactly the ratio
    /// that keeps the subtended angle constant.  That is what makes
    /// the panel's scale an angular setting - see
    /// <see cref="PanelScale"/>.
    ///
    /// The fraction is clamped to the range the glasses can actually
    /// show, so the panel can never be wider than the field of view.
    /// </remarks>
    public static float PanelWidth(float distance, float fovFraction, float diagonalFovDegrees = DiagonalFovDegrees)
    {
        var thetaH = ToRadians(HorizontalFovDegrees(diagonalFovDegrees));
        return 2f * distance * MathF.Tan(thetaH * PanelScale.Clamp(fovFraction) * 0.5f);
    }

    /// <summary>
    /// The height, in metres, of a 16:9 panel with the given width.
    /// </summary>
    public static float PanelHeight(float width) => width / Aspect;

    /// <summary>
    /// The height, in metres, of a panel with the given width and the
    /// given CONTENT aspect ratio.
    /// </summary>
    /// <param name="width">The panel's width, in metres.</param>
    /// <param name="contentAspect">The captured content's width / height.</param>
    /// <remarks>
    /// The panel's shape must match the CONTENT's shape, or the content
    /// is stretched.
    ///
    /// The PROJECTION is a 16:9 frustum because that is ONE EYE's
    /// viewport, but the PANEL - the quad in the world - does not have
    /// to be 16:9.  The captured window can be any shape: a browser
    /// in --app mode is whatever size the user made it, and a window
    /// captured whole includes its frame.
    ///
    /// Measured on the hardware (2026-11-01): the probe's 1200x800
    /// browser window (3:2) rendered on a 16:9 panel came out with a
    /// marker aspect of 2.385 instead of 2.0 - stretched by exactly
    /// (16/9) / (3/2) = 1.185.
    ///
    /// So the panel's height is derived from the content's own aspect:
    /// height = width / contentAspect.
    /// </remarks>
    public static float PanelHeight(float width, float contentAspect)
        => contentAspect > 1e-4f ? width / contentAspect : PanelHeight(width);

    /// <summary>
    /// The subtended horizontal angle, in degrees, of a panel of the given
    /// width at the given distance.
    /// </summary>
    public static float SubtendedAngleDegrees(float width, float distance)
    {
        if (distance <= 0f) return 0f;
        return ToDegrees(2f * MathF.Atan((width * 0.5f) / distance));
    }

    /// <summary>
    /// The distance at which a panel of the given width subtends the
    /// given fraction of the horizontal field of view.
    /// </summary>
    public static float DistanceForFraction(float width, float fovFraction, float diagonalFovDegrees = DiagonalFovDegrees)
    {
        var thetaH = ToRadians(HorizontalFovDegrees(diagonalFovDegrees));
        var halfAngle = thetaH * fovFraction * 0.5f;
        var t = MathF.Tan(halfAngle);
        return t <= 1e-6f ? 0f : (width * 0.5f) / t;
    }

    private static float ToRadians(float degrees) => degrees * MathF.PI / 180f;
    private static float ToDegrees(float radians) => radians * 180f / MathF.PI;

    /// <summary>
    /// A world-space up vector for the panel, derived from the eye's
    /// forward direction and gravity.
    /// </summary>
    /// <remarks>
    /// The panel's up axis is locked to world gravity, which keeps a
    /// tilted head from producing a crooked screen.  The construction
    /// below is the standard one: take the world up vector, remove the
    /// component along the panel's normal, and normalise what is left.
    /// If the panel faces exactly up or down that vector degenerates, and
    /// we fall back to the eye's own up axis.
    /// </remarks>
    /// <param name="panelNormal">Unit vector pointing out of the panel's face.</param>
    /// <param name="fallbackUp">Up axis to use when gravity is parallel to the normal.</param>
    public static Vector3 GravityLockedUp(Vector3 panelNormal, Vector3 fallbackUp)
    {
        var up = Vector3.UnitY - panelNormal * Vector3.Dot(Vector3.UnitY, panelNormal);
        if (up.LengthSquared() < 1e-6f)
        {
            up = fallbackUp - panelNormal * Vector3.Dot(fallbackUp, panelNormal);
            if (up.LengthSquared() < 1e-6f) up = Vector3.UnitX;
        }
        return Vector3.Normalize(up);
    }
}
