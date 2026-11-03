// SPDX-License-Identifier: MIT
//
// Tests for the 3D cursor's suppression.
//
// THE USER'S REPORT
// -----------------
// "When I play a 3D game with mouselook - Minecraft - the game locks
// the centre and makes the mouse cursor disappear.  But the cursor on
// the glasses is rendered by the app itself, so while I turn the
// camera the 3D cursor is still floating in front of my face and it
// is very much in the way."
//
// THE TWO STATES THAT HAVE TO BE TOLD APART
// ------------------------------------------
//
//   1. An ordinary application inside the virtual display, in its
//      normal UI mode.
//         -> The Custom 3D Cursor is SHOWN.
//
//   2. An application that has ACTIVELY HIDDEN the system cursor -
//      a game in mouselook mode, for instance, which calls
//      ShowCursor(FALSE) or SetCursor(NULL).
//         -> The Custom 3D Cursor is HIDDEN as well, so the user gets
//            a clean view of the game.
//
// The distinction has to be made from the OS's own answer, and
// GetCursorInfo gives it: CURSORINFO.flags has CURSOR_SHOWING clear
// when the cursor is hidden, and hCursor is NULL in the same cases.
//
// The subtlety is that OUR OWN APP also hides the system cursor, over
// the Rokid Max's own display, by answering WM_SETCURSOR with
// SetCursor(NULL).  That is a different hiding, and it must not be
// confused with the game's:
//
//   * ours applies to the panel window on the glasses' own display,
//     where the pointer is NOT over the virtual display, so the 3D
//     cursor is not drawn anyway;
//   * the game's applies to the virtual display, with the pointer
//     over it, and it is the one that has to suppress the 3D cursor.
//
// So the rule is simply: if the OS says the cursor is not visible,
// the 3D cursor is not drawn.
//
// THIS FILE TESTS THE STATE MACHINE THAT IMPLEMENTS THAT RULE.

using RokidDiyStation.Core.Spatial;

using Xunit;

namespace RokidDiyStation.Core.Tests;

public class VirtualCursorSuppressionTests
{
    [Fact]
    public void AnOrdinaryCursorIsRendered()
    {
        // State 1: a normal application in the virtual display.  The
        // cursor is over the display and the OS says it is visible.
        var cursor = new VirtualCursor();
        cursor.Seed(0.5f, 0.5f);

        Assert.True(cursor.IsActive);
        Assert.False(cursor.IsSuppressed);
        Assert.True(cursor.ShouldRender);
    }

    [Fact]
    public void ASystemHiddenCursorSuppressesThe3DCursor()
    {
        // State 2: the game has hidden the system cursor.
        var cursor = new VirtualCursor();
        cursor.Seed(0.5f, 0.5f);
        cursor.Suppress(true);

        Assert.True(cursor.IsSuppressed);

        // The cursor is STILL TRACKED - it is just not drawn.  This is
        // what lets it come straight back, in the right place, when
        // the game shows it again.
        Assert.True(cursor.IsActive);
        Assert.Equal(0.5f, cursor.U, 4);
        Assert.Equal(0.5f, cursor.V, 4);

        // And it is not rendered.
        Assert.False(cursor.ShouldRender);
    }

    [Fact]
    public void TheCursorComesBackWhenTheGameShowsItAgain()
    {
        // The user's requirement: "when I press Esc or open the
        // inventory and the system cursor appears again, the Custom 3D
        // Cursor appears again."
        var cursor = new VirtualCursor();
        cursor.Seed(0.42f, 0.58f);

        cursor.Suppress(true);
        Assert.False(cursor.ShouldRender);

        cursor.Suppress(false);

        Assert.True(cursor.ShouldRender);
        Assert.False(cursor.IsSuppressed);

        // And it is in the RIGHT PLACE - the position was kept while
        // it was hidden.
        Assert.Equal(0.42f, cursor.U, 4);
        Assert.Equal(0.58f, cursor.V, 4);
    }

    [Fact]
    public void SuppressingDoesNotMoveTheCursor()
    {
        // The suppression must not disturb the tracking.  A game in
        // mouselook mode moves the mouse constantly, and the cursor
        // has to be where the mouse actually is when the game hands
        // the pointer back.
        var cursor = new VirtualCursor();
        cursor.Seed(0.25f, 0.25f);

        cursor.Suppress(true);
        cursor.Seed(0.75f, 0.75f);
        cursor.Suppress(false);

        Assert.Equal(0.75f, cursor.U, 4);
        Assert.Equal(0.75f, cursor.V, 4);
        Assert.True(cursor.ShouldRender);
    }

    [Fact]
    public void ACursorOutsideTheDisplayIsNotRenderedEither()
    {
        // The other reason the cursor is not drawn: the pointer is not
        // over the virtual display at all.
        var cursor = new VirtualCursor();
        cursor.Seed(0.5f, 0.5f);
        cursor.Deactivate();

        Assert.False(cursor.IsActive);
        Assert.False(cursor.ShouldRender);
    }

    [Fact]
    public void TheSuppressionSurvivesLeavingAndReenteringTheDisplay()
    {
        // A subtle one: the pointer can leave the virtual display and
        // come back while the game is still running.  The suppression
        // is a property of the APPLICATION, not of the pointer's
        // position, so it must not be reset by the pointer moving.
        var cursor = new VirtualCursor();
        cursor.Seed(0.5f, 0.5f);
        cursor.Suppress(true);

        cursor.Deactivate();
        Assert.False(cursor.ShouldRender);

        cursor.Seed(0.5f, 0.5f);
        Assert.True(cursor.IsSuppressed);
        Assert.False(cursor.ShouldRender);

        // The game shows the cursor again.
        cursor.Suppress(false);
        Assert.True(cursor.ShouldRender);
    }

    [Fact]
    public void TheSuppressionIsIdempotent()
    {
        // The feedback loop runs every frame, so the suppression is
        // set over and over.  Setting it to the same value must not
        // churn the dirty flag, or the renderer would rebuild the
        // cursor quad every frame for nothing.
        var cursor = new VirtualCursor();
        cursor.Seed(0.5f, 0.5f);

        // Reading the dirty flag clears it.
        Assert.True(cursor.IsDirty);

        cursor.Suppress(true);
        Assert.True(cursor.IsDirty);

        // The same value again - no change, no dirty flag.
        cursor.Suppress(true);
        Assert.False(cursor.IsDirty);
    }
}
