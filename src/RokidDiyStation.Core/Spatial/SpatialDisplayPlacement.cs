// SPDX-License-Identifier: MIT
//
// Where the spatial display is placed on the desktop.
//
// THE BUG THIS FIXES
// ------------------
// The virtual display used to be placed at a fixed coordinate,
// (-1920, 0), on the assumption that it always sits immediately to
// the left of the primary display.
//
// That assumption breaks as soon as the desktop layout is different
// from the one the constant was written for:
//
//   * with one or two physical monitors, (-1920, 0) can land ON a
//     physical display, and Windows forbids overlapping monitor
//     rectangles outright - so the attach fails with 87
//     (ERROR_INVALID_PARAMETER);
//   * with three, it happened to be free, which is why the bug only
//     showed up on some of the user's test configurations.
//
// It is the same class of mistake as identifying the virtual display
// by its position instead of by its EDID: a hardcoded coordinate is
// a guess about the machine's layout, and the layout is exactly the
// thing that changes.
//
// THE RULE
// --------
// The virtual display goes to the LEFT of the leftmost display that
// is already on the desktop, at the same height.
//
//   * LEFT of everything, because it is the spatial canvas and it
//     must not overlap any physical display - Windows forbids it, and
//     overlapping the Rokid Max would additionally mean our own
//     topmost AR panel occludes the capture.
//
//   * At the same TOP as the leftmost display, so it lines up with
//     the display it is anchored to.
//
//   * The virtual display itself is EXCLUDED from the calculation.
//     It may still be listed from a previous session, and anchoring
//     to itself would make the position depend on where it used to
//     be.
namespace RokidDiyStation.Core.Spatial;

/// <summary>
/// A display rectangle in virtual-desktop pixels.
/// </summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Right">The right edge.</param>
/// <param name="Bottom">The bottom edge.</param>
public readonly record struct DesktopRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>The rectangle's width.</summary>
    public int Width => Right - Left;

    /// <summary>The rectangle's height.</summary>
    public int Height => Bottom - Top;
}

/// <summary>
/// Computes where the spatial display goes on the desktop.
/// </summary>
public static class SpatialDisplayPlacement
{
    /// <summary>
    /// The position for the spatial display.
    /// </summary>
    /// <param name="displays">
    /// The displays that are already on the desktop.  The virtual
    /// display itself must NOT be in this list.
    /// </param>
    /// <param name="width">The spatial display's width.</param>
    /// <param name="height">The spatial display's height.</param>
    /// <param name="fallbackX">
    /// The position to use when there is nothing on the desktop at
    /// all.
    /// </param>
    /// <param name="fallbackY">
    /// The position to use when there is nothing on the desktop at
    /// all.
    /// </param>
    /// <returns>The top-left corner of the spatial display.</returns>
    public static (int X, int Y) Compute(
        IReadOnlyList<DesktopRect> displays,
        int width,
        int height,
        int fallbackX,
        int fallbackY)
    {
        if (displays is null || displays.Count == 0) return (fallbackX, fallbackY);

        int leftmost = int.MaxValue;
        int top = 0;
        bool found = false;

        foreach (var r in displays)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;

            if (r.Left < leftmost)
            {
                leftmost = r.Left;
                top = r.Top;
                found = true;
            }
        }

        if (!found) return (fallbackX, fallbackY);

        // The virtual display sits immediately to the left of the
        // leftmost display.  There is no gap: the two are adjacent,
        // which is what Windows expects of a well-formed desktop, and
        // a gap would leave a dead strip the mouse can travel into.
        return (leftmost - width, top);
    }
}
