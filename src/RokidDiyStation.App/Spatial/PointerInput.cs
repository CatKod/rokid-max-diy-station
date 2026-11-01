// SPDX-License-Identifier: MIT
//
// The physical mouse, as seen from the panel.
//
// This class sits between the Win32 window procedure and the virtual
// cursor, and it does three things:
//
//  1. It hides the OS arrow while the pointer is over the glasses'
//     display, and lets it come back the moment the pointer leaves.
//
//  2. It turns the mouse's movement into the cursor's movement on the
//     panel, using the "re-seeded differential" policy: a ray-cast
//     when the pointer enters, then pure deltas forever after.
//
//  3. It reports the user's clicks, wheel and modifier keys, so the
//     input layer can forward them to the captured application.
//
// Why the arrow can be hidden per-display
// ----------------------------------------
// The cursor image is drawn by the GPU's hardware cursor plane on
// whichever OUTPUT the pointer is on.  SetCursor(NULL) removes it
// *now*, on this output.  When the pointer crosses onto the XP-Pen,
// that display's window gets its own WM_SETCURSOR and calls
// SetCursor(arrow) - so the arrow comes back instantly, with no
// flicker, and only on that display.
//
// The mouse is still fully tracked while hidden: GetCursorPos keeps
// working and the mouse messages keep arriving.  The cursor is only
// not DRAWN.
//
// Why the position comes from polling, not from WM_MOUSEMOVE
// ----------------------------------------------------------
// The mouse's POSITION is polled with GetCursorPos, once per frame,
// rather than taken from WM_MOUSEMOVE.  Two reasons:
//
//  * Entry detection.  If the pointer happens to be sitting over the
//    glasses' display when the app starts, no WM_MOUSEMOVE ever
//    arrives - the mouse is not moving.  Polling notices the pointer
//    is there immediately.
//
//  * Leave detection.  WM_MOUSELEAVE depends on TrackMouseEvent,
//    which is a request the window manager may or may not honour for
//    a WS_EX_NOACTIVATE window.  Polling compares the pointer's
//    position against the display's rectangle every frame, so the
//    transition is detected the moment it happens.
//
// The CLICK and WHEEL events still come from the window messages,
// because those are events rather than states, and a poll would miss
// a click that happened between two frames.
//
// What this class deliberately does NOT do
// -----------------------------------------
// It does not clip the cursor (ClipCursor).  Clipping would confine
// the pointer to the glasses' display, which is a hard barrier the
// user cannot push past, and it breaks the XP-Pen: the Artist 13.3 is
// an absolute digitiser, and while the pointer is clipped, the kernel
// clamps the pen's absolute coordinates to the clip edge, so the user
// cannot draw at all.
using System.Numerics;
using RokidDiyStation.Core.Spatial;

namespace RokidDiyStation.App.Spatial;

/// <summary>Which mouse button produced an event.</summary>
public enum PointerButton
{
    Left,
    Right,
    Middle,
}

/// <summary>A click, wheel tick or key press on the virtual cursor.</summary>
public readonly record struct PointerEvent(
    PointerButton Button,
    bool IsDown,
    int WheelDelta,
    bool Shift,
    bool Control,
    bool Alt);

/// <summary>
/// Tracks the physical mouse over the glasses' display and drives the
/// virtual cursor.
/// </summary>
public sealed class PointerInput
{
    private readonly VirtualCursor _cursor;
    private readonly Func<PanelPose?> _poseProvider;
    private readonly Func<Matrix4x4> _viewProvider;
    private readonly Func<Matrix4x4> _projectionProvider;

    private int _lastX;
    private int _lastY;
    private bool _haveLastPosition;
    private bool _pointerInside;

    /// <summary>Raised for clicks, wheel ticks and key presses.</summary>
    public event Action<PointerEvent>? OnPointerEvent;

    /// <summary>Raised when the pointer enters or leaves the display.</summary>
    public event Action<bool>? OnPointerInsideChanged;

    /// <summary>
    /// The cursor this input drives.
    /// </summary>
    public VirtualCursor Cursor => _cursor;

    /// <summary>True while the pointer is over the glasses' display.</summary>
    public bool IsPointerInside => _pointerInside;

    /// <summary>
    /// Create the input tracker.
    /// </summary>
    /// <param name="cursor">The virtual cursor to drive.</param>
    /// <param name="poseProvider">Supplies the panel's current pose.</param>
    /// <param name="viewProvider">Supplies the camera's view matrix.</param>
    /// <param name="projectionProvider">Supplies the projection matrix.</param>
    public PointerInput(
        VirtualCursor cursor,
        Func<PanelPose?> poseProvider,
        Func<Matrix4x4> viewProvider,
        Func<Matrix4x4> projectionProvider)
    {
        _cursor = cursor;
        _poseProvider = poseProvider;
        _viewProvider = viewProvider;
        _projectionProvider = projectionProvider;
    }

    // -----------------------------------------------------------------
    // Position tracking
    // -----------------------------------------------------------------

    /// <summary>
    /// The glasses' display, in virtual-desktop coordinates.
    /// </summary>
    /// <remarks>
    /// The panel window covers this rectangle exactly, so a pointer
    /// inside it is over the panel window, and a pointer outside it is
    /// over some other display entirely.
    /// </remarks>
    public System.Drawing.Rectangle DisplayBounds { get; set; }
        = new(0, 0, RokidDisplayProfile.Width, RokidDisplayProfile.Height);

    /// <summary>
    /// The size of the captured content, in pixels.
    /// </summary>
    /// <remarks>
    /// This is the denominator of the differential mapping: one mouse
    /// count moves the cursor by one content pixel.
    /// </remarks>
    public (int Width, int Height) ContentSize { get; set; } = (1920, 1080);

    /// <summary>
    /// Poll the physical mouse's position and update the cursor.
    /// </summary>
    /// <remarks>
    /// Called once per rendered frame, on the render thread.
    /// </remarks>
    public void Poll()
    {
        if (!NativeMethods.GetCursorPos(out var p)) return;

        var inside = DisplayBounds.Contains(p.x, p.y);
        if (!inside)
        {
            if (_pointerInside) SetInside(false);
            return;
        }

        if (!_pointerInside)
        {
            // The pointer has just entered the glasses' display.  This
            // is the re-seed: the cursor is placed at the point the
            // mouse is actually pointing at, found by ray-casting the
            // mouse pixel onto the panel's plane.
            SetInside(true);
            _lastX = p.x;
            _lastY = p.y;
            _haveLastPosition = true;
            Reseed(p.x - DisplayBounds.X, p.y - DisplayBounds.Y);
            return;
        }

        if (!_haveLastPosition)
        {
            _lastX = p.x;
            _lastY = p.y;
            _haveLastPosition = true;
            return;
        }

        var deltaX = p.x - _lastX;
        var deltaY = p.y - _lastY;
        _lastX = p.x;
        _lastY = p.y;

        if (deltaX == 0 && deltaY == 0) return;

        // Differential mapping.  The delta is in DISPLAY pixels, but
        // the gain is expressed in the CAPTURED WINDOW's pixels - the
        // content the user is actually pointing at.  Using the
        // content's own pixels is what makes one mouse count move the
        // cursor by one content pixel: the familiar 1:1 feel of an
        // ordinary monitor.
        var (contentWidth, contentHeight) = ContentSize;
        var scaleX = contentWidth / (float)Math.Max(1, DisplayBounds.Width);
        var scaleY = contentHeight / (float)Math.Max(1, DisplayBounds.Height);

        _cursor.Move(deltaX * scaleX, deltaY * scaleY, contentWidth, contentHeight);
    }

    /// <summary>
    /// Place the cursor at the point the mouse is pointing at.
    /// </summary>
    /// <remarks>
    /// This is the ray-cast: the mouse's display pixel becomes a ray
    /// in world space, and the ray is intersected with the panel's
    /// plane.  The hit point is then converted into the panel's
    /// texture coordinates.
    ///
    /// If the ray misses the panel - the user entered through the
    /// black border around it - the unclamped coordinates are clamped
    /// into [0, 1], so the cursor starts at the nearest edge instead
    /// of at an arbitrary point.
    /// </remarks>
    private void Reseed(int displayX, int displayY)
    {
        var pose = _poseProvider();
        if (pose is null) return;

        var view = _viewProvider();
        var projection = _projectionProvider();

        if (!PanelPointer.TryUnproject(
                displayX, displayY,
                DisplayBounds.Width, DisplayBounds.Height,
                view, projection,
                out var origin, out var direction))
            return;

        if (!PanelPointer.TryIntersectPlane(
                origin, direction,
                pose.Value.Center, pose.Value.Normal,
                out var hit))
        {
            // The ray missed the plane entirely (the panel is behind
            // the user, or edge-on).  Leave the cursor where it is.
            return;
        }

        var uv = PanelPointer.ToUv(hit, pose.Value);
        _cursor.Seed(uv.X, uv.Y);
    }

    // -----------------------------------------------------------------
    // Button events
    // -----------------------------------------------------------------

    /// <summary>
    /// Handle a mouse message from the panel window.
    /// </summary>
    /// <remarks>
    /// The panel window covers the whole glasses' display, so its
    /// client coordinates ARE the display-local pixels.  The mouse
    /// messages arrive because the panel is a real, opaque window:
    /// WS_EX_TRANSPARENT is only a painting-order flag, and the panel
    /// is not WS_EX_LAYERED, so it has always been receiving them.
    ///
    /// This is called on the render thread, from the window procedure.
    /// </remarks>
    public void HandleMouseMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_LBUTTONDOWN:
                Raise(PointerButton.Left, true, 0);
                break;

            case NativeMethods.WM_LBUTTONUP:
                Raise(PointerButton.Left, false, 0);
                break;

            case NativeMethods.WM_RBUTTONDOWN:
                Raise(PointerButton.Right, true, 0);
                break;

            case NativeMethods.WM_RBUTTONUP:
                Raise(PointerButton.Right, false, 0);
                break;

            case NativeMethods.WM_MBUTTONDOWN:
                Raise(PointerButton.Middle, true, 0);
                break;

            case NativeMethods.WM_MBUTTONUP:
                Raise(PointerButton.Middle, false, 0);
                break;

            case NativeMethods.WM_MOUSEWHEEL:
                RaiseWheel(NativeMethods.GetWheelDelta(wParam));
                break;
        }
    }

    private void Raise(PointerButton button, bool isDown, int wheelDelta)
    {
        if (!_pointerInside) return;

        OnPointerEvent?.Invoke(new PointerEvent(
            button, isDown, wheelDelta,
            IsShiftDown(), IsControlDown(), IsAltDown()));
    }

    private void RaiseWheel(int delta)
    {
        if (!_pointerInside) return;
        if (delta == 0) return;

        OnPointerEvent?.Invoke(new PointerEvent(
            PointerButton.Middle, false, delta,
            IsShiftDown(), IsControlDown(), IsAltDown()));
    }

    /// <summary>Whether SHIFT is currently held down.</summary>
    public static bool IsShiftDown() => (NativeMethods.GetKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;

    /// <summary>Whether CTRL is currently held down.</summary>
    public static bool IsControlDown() => (NativeMethods.GetKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;

    /// <summary>Whether ALT is currently held down.</summary>
    public static bool IsAltDown() => (NativeMethods.GetKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;

    /// <summary>
    /// Note that the pointer entered or left the glasses' display.
    /// </summary>
    /// <remarks>
    /// When the pointer leaves, the virtual cursor goes dormant.  The
    /// OS arrow is NOT restored from here: the cursor shape is a
    /// per-window property, and the window now under the pointer on
    /// the other display knows better what shape it wants.  Setting
    /// it from here would be wrong - and in practice it comes back on
    /// its own, because that display's window handles its own
    /// WM_SETCURSOR.
    /// </remarks>
    public void SetInside(bool inside)
    {
        if (_pointerInside == inside) return;
        _pointerInside = inside;

        _haveLastPosition = false;

        // Whether the cursor is active follows the pointer exactly:
        // no pointer over the glasses, no virtual cursor.
        _cursor.Deactivate();

        OnPointerInsideChanged?.Invoke(inside);
    }
}
