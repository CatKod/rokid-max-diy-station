// SPDX-License-Identifier: MIT
//
// Deciding whether the 3D cursor should be drawn.
//
// THE PROBLEM
// -----------
// The user plays a 3D game - Minecraft - in the spatial display.  The
// game locks the pointer and hides the system cursor, because in
// mouselook mode the mouse drives the camera and an arrow in the
// middle of the screen would be in the way.
//
// But the 3D cursor is OURS: we draw it ourselves, as a quad floating
// in the glasses' space.  So the game hiding the system cursor does
// nothing to it, and the arrow keeps hovering in front of the user's
// face, blocking the view.
//
// THE RULE
// --------
// If the OS says the system cursor is not visible, the 3D cursor is
// not drawn either.
//
// That sounds obvious, but there is a subtlety, and it is the reason
// this is a separate class rather than a line of code:
//
//   * OUR OWN APP hides the system cursor too - over the Rokid Max's
//     own display, by answering WM_SETCURSOR with SetCursor(NULL).
//     That hiding is deliberate and it is the WHOLE POINT of the 3D
//     cursor: the OS arrow is replaced by our own, in the glasses'
//     space.
//
//   * The GAME's hiding happens on the VIRTUAL display, with the
//     pointer over it, and it is the one that has to suppress the 3D
//     cursor.
//
// The two are told apart by WHERE they happen, not by WHO caused
// them: our hiding applies to the panel window on the glasses' own
// display, where the pointer is not over the virtual display.  So by
// the time the pointer IS over the virtual display, the only hiding
// that can be in effect is somebody else's.
//
// So the decision is:
//
//     the 3D cursor is drawn  <=>  the pointer is over the virtual
//                                  display  AND  the OS cursor is
//                                  visible
//
// WHY THIS IS A SEPARATE CLASS
// ----------------------------
// It is a policy decision, and it has to be testable without a
// display, a mouse, or a game.  So it lives here as a pure function
// over plain values, and the Win32 wrapper in SpatialPointerFeedback
// calls it.
namespace RokidDiyStation.Core.Spatial;

/// <summary>
/// Decides whether the 3D cursor should be drawn.
/// </summary>
public static class CursorVisibility
{
    /// <summary>
    /// Whether the OS cursor is currently visible.
    /// </summary>
    /// <param name="cursorShowing">
    /// The CURSOR_SHOWING bit of CURSORINFO.flags.  Clear means the
    /// cursor is hidden.
    /// </param>
    /// <param name="hasCursorShape">
    /// Whether CURSORINFO.hCursor is non-NULL.  SetCursor(NULL)
    /// leaves it NULL, which is the other way an application hides
    /// the cursor.
    /// </param>
    /// <remarks>
    /// EITHER signal is enough.  An application that hides the cursor
    /// with ShowCursor(FALSE) clears CURSOR_SHOWING but may leave a
    /// valid hCursor; one that uses SetCursor(NULL) leaves hCursor
    /// NULL.  Games use both, and different builds use different
    /// ones.
    /// </remarks>
    public static bool IsOsCursorVisible(bool cursorShowing, bool hasCursorShape)
        => cursorShowing && hasCursorShape;

    /// <summary>
    /// Whether the 3D cursor should be drawn.
    /// </summary>
    /// <param name="pointerIsInside">
    /// Whether the pointer is over the virtual display.
    /// </param>
    /// <param name="osCursorVisible">
    /// Whether the OS cursor is visible.
    /// </param>
    /// <remarks>
    /// The two conditions are ANDed, and they are independent:
    ///
    ///   * the pointer has to be over the virtual display at all, or
    ///     there is nothing to point at;
    ///   * and the application on it must not have taken the cursor
    ///     away, or the user is playing a game and wants a clean
    ///     view.
    /// </remarks>
    public static bool ShouldDrawCursor(bool pointerIsInside, bool osCursorVisible)
        => pointerIsInside && osCursorVisible;
}
