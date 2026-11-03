// SPDX-License-Identifier: MIT
//
// Tests for the cursor-visibility policy.
//
// The rule itself is short - "if the OS says the system cursor is not
// visible, the 3D cursor is not drawn either" - but it is the rule
// that decides whether a player of a 3D game can see the game, or
// whether a cursor hovers in front of their face.  So it is tested
// as a policy, in isolation, without a display and without a game.
//
// THE TWO WAYS AN APPLICATION HIDES THE CURSOR
// ---------------------------------------------
//   * ShowCursor(FALSE) clears CURSOR_SHOWING but may leave a valid
//     hCursor behind.
//   * SetCursor(NULL) leaves hCursor NULL.
//
// Games use both, and different builds use different ones, so either
// signal has to be enough on its own.

using RokidDiyStation.Core.Spatial;

using Xunit;

namespace RokidDiyStation.Core.Tests;

public class CursorVisibilityTests
{
    [Fact]
    public void AVisibleCursorIsVisible()
    {
        Assert.True(CursorVisibility.IsOsCursorVisible(
            cursorShowing: true, hasCursorShape: true));
    }

    [Fact]
    public void AHiddenCursorIsNotVisible()
    {
        // ShowCursor(FALSE): the flag is clear, the shape is still there.
        Assert.False(CursorVisibility.IsOsCursorVisible(
            cursorShowing: false, hasCursorShape: true));
    }

    [Fact]
    public void ANullCursorShapeIsNotVisible()
    {
        // SetCursor(NULL): there is no shape at all.
        Assert.False(CursorVisibility.IsOsCursorVisible(
            cursorShowing: true, hasCursorShape: false));
    }

    [Fact]
    public void BothSignalsHiddenIsNotVisible()
    {
        Assert.False(CursorVisibility.IsOsCursorVisible(
            cursorShowing: false, hasCursorShape: false));
    }

    [Fact]
    public void TheCursorIsDrawnOnlyInsideTheDisplayAndWhileTheOsCursorIsVisible()
    {
        // State 1: the ordinary UI case.  The pointer is over the
        // virtual display and the application has not taken the cursor
        // away, so the 3D cursor is drawn.
        Assert.True(CursorVisibility.ShouldDrawCursor(
            pointerIsInside: true, osCursorVisible: true));

        // State 2: the game in mouselook mode.  The pointer is over the
        // virtual display, but the application has hidden the system
        // cursor - so the 3D cursor has to go with it.
        Assert.False(CursorVisibility.ShouldDrawCursor(
            pointerIsInside: true, osCursorVisible: false));

        // And the pointer is somewhere else entirely.
        Assert.False(CursorVisibility.ShouldDrawCursor(
            pointerIsInside: false, osCursorVisible: true));

        Assert.False(CursorVisibility.ShouldDrawCursor(
            pointerIsInside: false, osCursorVisible: false));
    }
}
