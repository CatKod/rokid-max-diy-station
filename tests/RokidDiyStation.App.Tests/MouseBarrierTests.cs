// SPDX-License-Identifier: MIT
//
// Tests for the mouse barrier's decision logic.
//
// WHY THIS MATTERS ENOUGH TO TEST
// -------------------------------
// The barrier is what keeps the cursor off the Rokid Max's own
// display.  Two ways it can be wrong, and both are user-visible:
//
//   * It pushes the cursor back when it should not - the mouse feels
//     broken, and the user cannot reach part of their desktop.
//
//   * It does NOT push the cursor back when it should - the arrow
//     ends up on a screen the user is not looking at, and a click
//     there does something unexpected.
//
// The decision is a pure function of the cursor's position and the
// forbidden rectangle, so it can be tested exhaustively here without
// a mouse, a hook, or a set of glasses.
//
// THE USER'S ACTUAL LAYOUT
// ------------------------
//     Display 1  (0,0)      the laptop's panel
//     Display 2  (1920,0)   the XP-Pen Artist 13.3
//     Display 5  (3840,0)   the Rokid Max's own screen
//     the virtual display  (-1920,0)  the spatial panel's canvas
//
// So the glasses are to the RIGHT of the working area, and the
// cursor has to be pushed back to the left of them.
using RokidDiyStation.App.Spatial;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class MouseBarrierTests
{
    /// <summary>
    /// The Rokid Max's display, as it appears in the user's layout.
    /// </summary>
    private static NativeMethods.RECT RokidDisplay() => new()
    {
        left = 3840,
        top = 0,
        right = 5760,
        bottom = 1080,
    };

    [Fact]
    public void TheCursorIsLeftAloneOnThePrimaryDisplay()
    {
        var decision = MouseBarrierLogic.Evaluate(500, 400, RokidDisplay(), forbiddenIsToTheRight: true);
        Assert.False(decision.ShouldPushBack);
        Assert.Equal(500, decision.PushBackX);
    }

    [Fact]
    public void TheCursorIsLeftAloneOnTheDrawingTablet()
    {
        // The XP-Pen Artist 13.3 is a drawing surface.  The barrier
        // must never interfere with it - that is the whole reason
        // ClipCursor is not used.
        var decision = MouseBarrierLogic.Evaluate(2500, 400, RokidDisplay(), forbiddenIsToTheRight: true);
        Assert.False(decision.ShouldPushBack);
    }

    [Fact]
    public void TheCursorIsLeftAloneOnTheVirtualDisplay()
    {
        // The virtual display is at negative coordinates, on the far
        // LEFT of the desktop.  It is the surface the user actually
        // works on, so it must be completely untouched.
        var decision = MouseBarrierLogic.Evaluate(-1000, 400, RokidDisplay(), forbiddenIsToTheRight: true);
        Assert.False(decision.ShouldPushBack);
        Assert.Equal(-1000, decision.PushBackX);
    }

    [Fact]
    public void TheCursorIsPushedBackOutOfTheGlassesDisplay()
    {
        var decision = MouseBarrierLogic.Evaluate(4000, 400, RokidDisplay(), forbiddenIsToTheRight: true);
        Assert.True(decision.ShouldPushBack);
        Assert.Equal(3839, decision.PushBackX);
    }

    [Fact]
    public void TheCursorIsPushedBackFromTheVeryFirstPixel()
    {
        // x == left is the first pixel of the forbidden column, and it
        // has to be caught - an off-by-one here would let the arrow
        // cross onto the glasses' screen for one frame.
        var decision = MouseBarrierLogic.Evaluate(3840, 400, RokidDisplay(), forbiddenIsToTheRight: true);
        Assert.True(decision.ShouldPushBack);
        Assert.Equal(3839, decision.PushBackX);
    }

    [Fact]
    public void TheLastPixelOfTheGlassesDisplayIsAlsoCaught()
    {
        // The rectangle is [left, right), so x == 5759 is the last
        // pixel and x == 5760 is one past it.
        Assert.True(MouseBarrierLogic.Evaluate(5759, 400, RokidDisplay(), forbiddenIsToTheRight: true).ShouldPushBack);
        Assert.False(MouseBarrierLogic.Evaluate(5760, 400, RokidDisplay(), forbiddenIsToTheRight: true).ShouldPushBack);
    }

    [Fact]
    public void TheVerticalExtentIsNotEnforced()
    {
        // The glasses' screen is a full column, and the displays
        // beside it overlap vertically.  A user working on the
        // laptop's panel at Y = 200 must not be blocked because the
        // glasses' rectangle happens to span that Y too.
        //
        // This is checked on the X axis: a cursor at the laptop's X
        // is fine at ANY Y.
        for (var y = 0; y < 1080; y += 97)
        {
            var decision = MouseBarrierLogic.Evaluate(500, y, RokidDisplay(), forbiddenIsToTheRight: true);
            Assert.False(decision.ShouldPushBack);
        }
    }

    [Fact]
    public void ThereIsNoBarrierWithoutAForbiddenRectangle()
    {
        var decision = MouseBarrierLogic.Evaluate(4000, 400, null, forbiddenIsToTheRight: true);
        Assert.False(decision.ShouldPushBack);
        Assert.Equal(4000, decision.PushBackX);
    }

    [Fact]
    public void AForbiddenColumnOnTheLeftPushesTheCursorToTheRight()
    {
        // The mirror-image layout: the glasses at (-1920, 0), the
        // working displays to their right.
        var glasses = new NativeMethods.RECT
        {
            left = -1920,
            top = 0,
            right = 0,
            bottom = 1080,
        };

        var decision = MouseBarrierLogic.Evaluate(-1000, 400, glasses, forbiddenIsToTheRight: false);
        Assert.True(decision.ShouldPushBack);
        Assert.Equal(1, decision.PushBackX);
    }

    [Fact]
    public void TheCursorIsPushedToTheEdgeNearestTheWorkingArea()
    {
        // The push-back must land on the working side of the glasses,
        // not on the far side.  In the user's layout the working area
        // is to the LEFT, so the cursor lands at left - 1.
        var decision = MouseBarrierLogic.Evaluate(5000, 400, RokidDisplay(), forbiddenIsToTheRight: true);
        Assert.Equal(3839, decision.PushBackX);
        Assert.True(decision.PushBackX < 3840, "the cursor must not be pushed INTO the glasses' display");
    }

    [Fact]
    public void EveryPixelOfTheForbiddenColumnIsCaught()
    {
        // An exhaustive sweep over the column, so no pixel can slip
        // through.
        var glasses = RokidDisplay();
        for (var x = glasses.left; x < glasses.right; x++)
        {
            var decision = MouseBarrierLogic.Evaluate(x, 540, glasses, forbiddenIsToTheRight: true);
            Assert.True(decision.ShouldPushBack, $"x={x} was not caught");
        }
    }

    [Fact]
    public void TheWholeWorkingAreaIsFree()
    {
        // Every display the user actually works on - the laptop, the
        // tablet and the virtual display - must be completely free.
        var glasses = RokidDisplay();
        foreach (var x in new[] { -1919, -960, 0, 960, 1919, 1920, 2879, 3839 })
        {
            var decision = MouseBarrierLogic.Evaluate(x, 540, glasses, forbiddenIsToTheRight: true);
            Assert.False(decision.ShouldPushBack, $"x={x} was wrongly blocked");
        }
    }
}
