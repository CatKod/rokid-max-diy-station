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

    /// <summary>
    /// Whether this rectangle intersects another one.
    /// </summary>
    /// <remarks>
    /// Windows forbids overlapping monitor rectangles outright: a
    /// configuration whose displays overlap is rejected with 87
    /// (ERROR_INVALID_PARAMETER), and there is no way to apply it.
    /// That is why this predicate exists - it is the condition the
    /// whole layout-repair logic is built around.
    ///
    /// Touching edges do NOT count as an overlap.  Two displays that
    /// share a border are the normal case - that is how Windows lays
    /// a desktop out.
    /// </remarks>
    public bool Intersects(DesktopRect other)
        => Left < other.Right && other.Left < Right
        && Top < other.Bottom && other.Top < Bottom;
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

    /// <summary>
    /// Repair a layout whose display rectangles overlap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS
    /// --------------
    /// Windows refuses a configuration whose display rectangles
    /// overlap: SetDisplayConfig answers 87 (ERROR_INVALID_PARAMETER)
    /// and nothing can be applied.  That is the rule.
    /// </para>
    /// <para>
    /// The user's machine runs into it after a monitor is unplugged.
    /// The display-configuration DATABASE keeps one record per
    /// topology the machine has ever been in, and each record has its
    /// own positions.  When the Rokid Max was unplugged, the layout
    /// was rebuilt from a record that put the virtual display at
    /// (1915, -1080) - which overlaps the XP-Pen artist display at
    /// (1920, 0).  The rebuilt desktop therefore contained an
    /// overlap, and every attempt to change it - the attach and the
    /// detach alike - was rejected with 87, so the virtual display
    /// stayed on the desktop.
    /// </para>
    /// <para>
    /// The repair keeps the FIRST display where it is and lays the
    /// others out to its right, in the order they were given.  That
    /// preserves the user's arrangement as far as possible: the
    /// display the user is looking at does not move.
    /// </para>
    /// <para>
    /// BUT A DISPLAY ON ANOTHER ROW IS NOT MOVED AT ALL
    /// -------------------------------------------------
    /// The user's own machine holds a record that puts the virtual
    /// display at (1915, -1080) - a whole row ABOVE the physical
    /// monitors, which is where Windows had put it when the Rokid Max
    /// was still attached.  A display that is above the others is not
    /// overlapping them: it is a display on its own row, and that is a
    /// perfectly valid desktop.
    /// </para>
    /// <para>
    /// The old repair could not tell the two apart, because it only
    /// ever compared rectangles.  So it dragged the virtual display
    /// DOWN onto the row of the physical monitors - and, when the
    /// display it moved was the XP-Pen, it dragged a PHYSICAL monitor
    /// with it.  A repair that moves the user's own displays is worse
    /// than the overlap it was meant to fix.
    /// </para>
    /// <para>
    /// So the repair is done PER ROW: the displays are grouped by the
    /// row they are on, and only the displays that share a row are
    /// rearranged among themselves.  A display on a row of its own is
    /// left exactly where it is.
    /// </para>
    /// </remarks>
    /// <param name="displays">The displays, in the order they should be laid out.</param>
    /// <returns>
    /// The repaired rectangles, in the same order.  The list is
    /// returned unchanged when there is nothing to repair.
    /// </returns>
    public static IReadOnlyList<DesktopRect> RepairOverlaps(
        IReadOnlyList<DesktopRect> displays)
    {
        if (displays is null || displays.Count == 0)
            return Array.Empty<DesktopRect>();

        // Is anything wrong at all?  The common case is a clean
        // layout, and this keeps the common case free of allocations.
        bool broken = false;
        for (int i = 0; i < displays.Count && !broken; i++)
        {
            for (int j = i + 1; j < displays.Count; j++)
            {
                if (!displays[i].Intersects(displays[j])) continue;
                broken = true;
                break;
            }
        }
        if (!broken) return displays;

        // The repair is done PER ROW.
        //
        // The displays are grouped by the row they are on - two
        // displays share a row when their vertical ranges overlap -
        // and only the displays within a row are rearranged among
        // themselves.  A display on a row of its own is left exactly
        // where it is.
        //
        // The old repair put everything on ONE row, which dragged the
        // virtual display down from (1915, -1080) onto the physical
        // monitors' row - and, when the display it moved happened to
        // be a physical one, dragged the user's own screen with it.
        var result = new List<DesktopRect>(displays.Count);

        foreach (var r in displays)
        {
            if (r.Width <= 0 || r.Height <= 0)
            {
                // A degenerate rectangle is not a display; it is kept
                // as it is so the caller can still match it up.
                result.Add(r);
                continue;
            }

            // The displays that share this one's row, and have been
            // placed already.
            var row = new List<DesktopRect>();
            foreach (var placed in result)
            {
                if (placed.Width <= 0 || placed.Height <= 0) continue;
                if (!SharesRow(placed, r)) continue;
                row.Add(placed);
            }

            if (row.Count == 0)
            {
                // Its own row.  It stays where it is.
                result.Add(r);
                continue;
            }

            // To the right of the rightmost display already on this
            // row, and clear of everything else as well.
            int x = int.MinValue;
            foreach (var placed in row)
            {
                if (placed.Right > x) x = placed.Right;
            }

            while (Overlaps(result, x, r.Top, r.Width, r.Height))
            {
                x += 1;
            }

            result.Add(new DesktopRect(x, r.Top, x + r.Width, r.Bottom));
        }

        return result;
    }

    /// <summary>
    /// Whether two displays are on the same row of the desktop.
    /// </summary>
    /// <remarks>
    /// A row is a band of vertical space.  Two displays are on the
    /// same row when their vertical ranges overlap - which is what
    /// makes them compete for the same horizontal space.
    ///
    /// Touching edges do NOT count: two displays stacked exactly on
    /// top of each other are on different rows, and that is a valid
    /// desktop.
    /// </remarks>
    private static bool SharesRow(DesktopRect a, DesktopRect b)
        => a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>
    /// Whether a rectangle would overlap any of the given ones.
    /// </summary>
    private static bool Overlaps(
        IReadOnlyList<DesktopRect> placed, int left, int top, int width, int height)
    {
        var candidate = new DesktopRect(left, top, left + width, top + height);
        foreach (var r in placed)
        {
            if (candidate.Intersects(r)) return true;
        }
        return false;
    }
}
