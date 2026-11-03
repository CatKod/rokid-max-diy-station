// SPDX-License-Identifier: MIT
//
// Tests for where the spatial display is placed on the desktop.
//
// THE USER'S CONFIGURATIONS
// --------------------------
// The user tested the app with one physical monitor, two, and three.
// The virtual display's placement used to be a constant, (-1920, 0),
// and that constant is only correct for one of those layouts.
//
// These tests pin the rule down: the spatial display always goes to
// the LEFT of the leftmost display that is already there, so it can
// never overlap a physical display - Windows forbids overlapping
// monitor rectangles outright, and the overlap would also mean our
// own topmost AR panel sits on top of the capture.

using RokidDiyStation.Core.Spatial;

using Xunit;

namespace RokidDiyStation.Core.Tests;

public class SpatialDisplayPlacementTests
{
    private static DesktopRect Rect(int left, int top, int right, int bottom)
        => new(left, top, right, bottom);

    [Fact]
    public void WithNothingOnTheDesktopTheFallbackIsUsed()
    {
        var (x, y) = SpatialDisplayPlacement.Compute(
            Array.Empty<DesktopRect>(), width: 1920, height: 1080,
            fallbackX: -1920, fallbackY: 0);

        Assert.Equal(-1920, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void WithOneDisplayTheSpatialDisplayGoesToItsLeft()
    {
        // The laptop panel at (0, 0).  The spatial display goes to
        // (-1920, 0) - which is the historical constant, and it is
        // correct here BY COINCIDENCE, not by assumption.
        var (x, y) = SpatialDisplayPlacement.Compute(
            new[] { Rect(0, 0, 1920, 1080) }, 1920, 1080, -1920, 0);

        Assert.Equal(-1920, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void WithADisplayThatIsAlreadyAtNegativeCoordinatesTheSpatialDisplayFollows()
    {
        // The physical monitor was moved to the left by the user, so
        // the leftmost display is no longer at x = 0.  A hardcoded
        // (-1920, 0) would land ON it.
        var (x, y) = SpatialDisplayPlacement.Compute(
            new[] { Rect(-2560, 0, -640, 1080) }, 1920, 1080, -1920, 0);

        Assert.Equal(-2560 - 1920, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void WithSeveralDisplaysTheLeftmostOneIsTheAnchor()
    {
        // The Artist 13.3 at (1920, 0), the Rokid Max at (3840, 0),
        // the laptop at (0, 0).
        var (x, y) = SpatialDisplayPlacement.Compute(
            new[]
            {
                Rect(1920, 0, 3840, 1080),
                Rect(3840, 0, 5760, 1080),
                Rect(0, 0, 1920, 1080),
            },
            1920, 1080, -1920, 0);

        Assert.Equal(-1920, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void TheSpatialDisplayIsAtTheSameHeightAsItsAnchor()
    {
        // The user moved the laptop panel to the top half of the
        // screen; the spatial display follows it, so the two stay
        // adjacent instead of being offset.
        var (x, y) = SpatialDisplayPlacement.Compute(
            new[] { Rect(0, -1080, 1920, 0) }, 1920, 1080, -1920, 0);

        Assert.Equal(-1920, x);
        Assert.Equal(-1080, y);
    }

    [Fact]
    public void ThePlacementNeverOverlapsAnExistingDisplay()
    {
        // The property that actually matters: whatever the layout is,
        // the spatial display's rectangle must not intersect any of
        // the displays that are already there.  Windows rejects an
        // overlapping configuration outright.
        var layouts = new[]
        {
            new[] { Rect(0, 0, 1920, 1080) },
            new[] { Rect(0, 0, 1920, 1080), Rect(1920, 0, 3840, 1080) },
            new[]
            {
                Rect(0, 0, 1920, 1080),
                Rect(1920, 0, 3840, 1080),
                Rect(3840, 0, 5760, 1080),
            },
            new[] { Rect(-1920, 0, 0, 1080) },
            new[] { Rect(-1920, -1080, 0, 0), Rect(0, 0, 1920, 1080) },
            new[] { Rect(3840, 0, 5760, 1080) },
        };

        const int w = 1920;
        const int h = 1080;

        foreach (var layout in layouts)
        {
            var (x, y) = SpatialDisplayPlacement.Compute(layout, w, h, -1920, 0);
            var placed = Rect(x, y, x + w, y + h);

            foreach (var existing in layout)
            {
                bool overlap =
                    placed.Left < existing.Right && existing.Left < placed.Right
                    && placed.Top < existing.Bottom && existing.Top < placed.Bottom;

                Assert.False(overlap,
                    $"the spatial display at ({placed.Left},{placed.Top}) overlaps "
                    + $"({existing.Left},{existing.Top})-({existing.Right},{existing.Bottom})");
            }
        }
    }

    [Fact]
    public void DegenerateDisplaysAreIgnored()
    {
        // A zero-width rectangle is not a display; it must not be
        // chosen as the anchor.
        var (x, y) = SpatialDisplayPlacement.Compute(
            new[] { Rect(0, 0, 0, 0), Rect(1920, 0, 3840, 1080) },
            1920, 1080, -1920, 0);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    // -----------------------------------------------------------------
    // The overlap repair
    // -----------------------------------------------------------------

    [Fact]
    public void TouchingDisplaysDoNotOverlap()
    {
        // Two displays that share a border are the normal case: that
        // is how Windows lays a desktop out.  Treating a shared border
        // as an overlap would make every ordinary layout "broken".
        var a = Rect(0, 0, 1920, 1080);
        var b = Rect(1920, 0, 3840, 1080);

        Assert.False(a.Intersects(b));
        Assert.False(b.Intersects(a));

        // Stacked exactly on top of each other is an overlap.
        Assert.True(a.Intersects(Rect(0, 0, 1920, 1080)));
    }

    [Fact]
    public void ADisplayOnItsOwnRowIsNotMoved()
    {
        // The user's own machine holds a record that puts the virtual
        // display at (1915, -1080) - a whole row ABOVE the physical
        // monitors.  That is a valid desktop, and a repair that dragged
        // it down onto the monitors' row would be worse than the
        // problem it was meant to solve.
        var layout = new[]
        {
            Rect(1920, 0, 3840, 1080),   // the XP-Pen artist display
            Rect(0, 0, 1920, 1080),      // the laptop panel
            Rect(1915, -1080, 3835, 0),  // the virtual display, a row above
        };

        var repaired = SpatialDisplayPlacement.RepairOverlaps(layout);

        Assert.Equal(3, repaired.Count);
        Assert.Equal(Rect(1915, -1080, 3835, 0), repaired[2]);
    }

    [Fact]
    public void OverlappingDisplaysOnTheSameRowAreSeparated()
    {
        // Two displays on the same row that overlap: the repair has to
        // move one of them, because Windows refuses an overlapping
        // configuration outright.
        var layout = new[]
        {
            Rect(0, 0, 1920, 1080),
            Rect(1000, 0, 2920, 1080),
        };

        var repaired = SpatialDisplayPlacement.RepairOverlaps(layout);

        Assert.Equal(2, repaired.Count);
        Assert.False(repaired[0].Intersects(repaired[1]));
    }

    [Fact]
    public void TheRepairNeverLeavesAnOverlap()
    {
        // The property that matters: after the repair, NO two displays
        // overlap - whatever the layout was.
        var layouts = new[]
        {
            new[] { Rect(0, 0, 1920, 1080), Rect(100, 0, 2020, 1080) },
            new[] { Rect(0, 0, 1920, 1080), Rect(1920, 0, 3840, 1080), Rect(1915, -1080, 3835, 0) },
            new[] { Rect(0, 0, 1920, 1080), Rect(500, 0, 2420, 1080), Rect(1000, 0, 2920, 1080) },
            new[] { Rect(0, 0, 1920, 1080), Rect(100, 100, 2020, 1180) },
        };

        foreach (var layout in layouts)
        {
            var repaired = SpatialDisplayPlacement.RepairOverlaps(layout);

            for (int i = 0; i < repaired.Count; i++)
            {
                for (int j = i + 1; j < repaired.Count; j++)
                {
                    Assert.False(repaired[i].Intersects(repaired[j]),
                        $"({repaired[i].Left},{repaired[i].Top}) and "
                        + $"({repaired[j].Left},{repaired[j].Top}) still overlap");
                }
            }
        }
    }

    [Fact]
    public void ACleanLayoutIsNotTouchedAtAll()
    {
        // The common case has to stay free of allocations and of
        // surprises: a layout that is already valid is sent exactly as
        // it was read.
        var layout = new[]
        {
            Rect(0, 0, 1920, 1080),
            Rect(1920, 0, 3840, 1080),
        };

        var repaired = SpatialDisplayPlacement.RepairOverlaps(layout);

        Assert.Same(layout, repaired);
    }
}
