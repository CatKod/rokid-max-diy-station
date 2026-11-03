// SPDX-License-Identifier: MIT
//
// How much of the Rokid Max's field of view the spatial panel covers.
//
// WHY THE PANEL'S SCALE IS A SETTING
// ----------------------------------
// The panel's angular size used to be a hard-coded 50 % of the
// glasses' horizontal field of view.  That number was baked into the
// anchor service's default options and into the arrow's reference
// size, and there was no way to change it.
//
// It is a setting because the right value is personal:
//
//  * A reader who wants a book-sized page wants a small panel, so the
//    text is not double-stretched across their whole view.
//
//  * Someone using the glasses as a second monitor for a wide
//    spreadsheet wants a large panel.
//
//  * And the comfortable fraction is not the same for everyone: the
//    glasses' own optics have a fixed virtual image distance, and the
//    usable field of view depends on how the user holds them.
//
// WHY THE RANGE IS 25 % TO 100 %
// -------------------------------
// 25 % is the practical floor: below it the panel is small enough
// that reading text on it becomes uncomfortable, and it stops being
// useful as a reading surface.
//
// 100 % is the ceiling: the panel may cover the ENTIRE horizontal
// field of view, which means its edges sit exactly on the bezel.  It
// cannot go above 100 %, because the panel would have to extend past
// the edge of what the glasses can show - the content would simply be
// clipped off, and the user would be looking at a cropped picture
// while believing they could see all of it.
//
// WHY THE STEP IS 5 %
// -------------------
// A 1 % step would give a slider that is fiddly to aim at and moves
// the panel in barely-perceptible steps.  A 10 % step would be too
// coarse: the user would have to settle for a size that is visibly
// wrong rather than one that is nearly right.  5 % is small enough
// that "just a bit bigger" is reachable, and large enough that every
// step is a visible change.
//
// HOW THE FRACTION REACHES THE PANEL
// ----------------------------------
// The fraction is the panel's ANGULAR width: the panel subtends this
// fraction of the glasses' horizontal field of view.  It is not a
// size in metres and it does not depend on the panel's distance -
// see <see cref="RokidDisplayProfile.PanelWidth"/>, where the width
// grows with the distance so the ANGLE stays constant.
//
// So changing this setting changes the panel's size and nothing else:
// its distance, its centre and its world pose are untouched.  The
// anchor service re-sizes the panel IN PLACE for exactly this reason,
// so the panel does not teleport when the user drags the slider.
namespace RokidDiyStation.Core.Spatial;

/// <summary>
/// The panel's angular size, as a fraction of the glasses' field of
/// view.
/// </summary>
public static class PanelScale
{
    /// <summary>The smallest fraction the user may choose.</summary>
    public const float MinFraction = 0.25f;

    /// <summary>The largest fraction the user may choose.</summary>
    public const float MaxFraction = 1.0f;

    /// <summary>The fraction used when the user has expressed no preference.</summary>
    public const float DefaultFraction = 0.5f;

    /// <summary>
    /// The step between the slider's ticks, as a fraction.
    /// </summary>
    public const float StepFraction = 0.05f;

    /// <summary>The smallest fraction the user may choose, in percent.</summary>
    public const double MinPercent = MinFraction * 100.0;

    /// <summary>The largest fraction the user may choose, in percent.</summary>
    public const double MaxPercent = MaxFraction * 100.0;

    /// <summary>The fraction used when the user has expressed no preference, in percent.</summary>
    public const double DefaultPercent = DefaultFraction * 100.0;

    /// <summary>
    /// The step between the slider's ticks, in percent.
    /// </summary>
    public const double StepPercent = StepFraction * 100.0;

    /// <summary>
    /// Clamp a requested fraction into the supported range.
    /// </summary>
    /// <remarks>
    /// Applied everywhere the fraction is read, not only in the UI, so
    /// that a hand-edited settings file cannot produce a panel that
    /// covers more of the field of view than the glasses can show.
    /// </remarks>
    /// <param name="fraction">The requested fraction.</param>
    public static float Clamp(float fraction)
    {
        if (!float.IsFinite(fraction)) return DefaultFraction;
        return Math.Clamp(fraction, MinFraction, MaxFraction);
    }

    /// <summary>
    /// Snap a fraction to the nearest supported step.
    /// </summary>
    /// <remarks>
    /// The slider snaps to its ticks already, but a value that comes
    /// from the settings file is not snapped, and the panel would then
    /// sit at an awkward size that the slider cannot even represent.
    /// Snapping on the way in keeps the slider and the panel in
    /// agreement.
    /// </remarks>
    /// <param name="fraction">The requested fraction.</param>
    public static float Snap(float fraction)
    {
        var clamped = Clamp(fraction);
        var steps = MathF.Round((clamped - MinFraction) / StepFraction);
        return Clamp(MinFraction + steps * StepFraction);
    }

    /// <summary>
    /// The fraction for a percentage, snapped and clamped.
    /// </summary>
    /// <param name="percent">The percentage, e.g. 50 for 50 %.</param>
    public static float FromPercent(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent))
            return DefaultFraction;
        return Snap((float)(percent / 100.0));
    }

    /// <summary>
    /// The percentage for a fraction.
    /// </summary>
    /// <param name="fraction">The fraction, e.g. 0.5 for 50 %.</param>
    public static double ToPercent(float fraction) => Clamp(fraction) * 100.0;

    /// <summary>
    /// Resolve the effective fraction from the two persisted forms.
    /// </summary>
    /// <remarks>
    /// The settings file carries the fraction (the original form) and
    /// the percentage (the form the slider works in).  Only one of
    /// them can be authoritative, and the precedence has to be
    /// well-defined or the two would silently fight each other.
    ///
    /// The PERCENTAGE wins whenever it differs from its default.  That
    /// is the value the user chose on the slider, and it is the one
    /// that was snapped to a 5 % step.
    ///
    /// Otherwise the FRACTION is honoured, which is what an older
    /// settings file - written before the percentage existed - carries.
    /// It is snapped to the step on the way in, so a hand-edited value
    /// of, say, 0.37 becomes a size the slider can actually represent.
    ///
    /// When both are at their defaults, the default wins, which is the
    /// same 50 % as before.
    /// </remarks>
    /// <param name="fraction">The persisted fraction, or null when absent.</param>
    /// <param name="percent">The persisted percentage, or null when absent.</param>
    public static float Resolve(float? fraction, double? percent)
    {
        if (percent is not null && !double.IsNaN(percent.Value) && !double.IsInfinity(percent.Value))
        {
            var fromPercent = FromPercent(percent.Value);
            // Differing from the default means the user chose it.
            if (Math.Abs(fromPercent - DefaultFraction) > 1e-4f)
                return fromPercent;
        }

        if (fraction is not null)
            return Snap(fraction.Value);

        return DefaultFraction;
    }
}
