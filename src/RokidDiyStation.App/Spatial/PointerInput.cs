// SPDX-License-Identifier: MIT
//
// The physical mouse, as seen from the panel.
//
// This class sits between the Win32 input plumbing and the virtual
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
// Why a low-level mouse HOOK, and not the window messages
// ---------------------------------------------------------
// The panel window is a real, opaque window, so it does receive
// WM_LBUTTONDOWN and friends.  But two things make the window
// messages the wrong channel:
//
//  * WM_MOUSEWHEEL is delivered to the FOCUSED window, not to the
//    window under the pointer.  The panel is WS_EX_NOACTIVATE and
//    never takes focus, so it would NEVER see a wheel event - the
//    scroll would silently go to whatever the user had focused.
//
//  * Whether the panel wins the Z-order hit test is a side effect of
//    WS_EX_TRANSPARENT, which for a non-layered window is a PAINT
//    order flag rather than a hit-test rule.  Depending on that for
//    the button events would be fragile.
//
// A WH_MOUSE_LL hook sees every mouse event on the machine, so this
// class FILTERS BY THE POINTER'S POSITION: only events over the
// glasses' display are treated as input for the panel.  The hook
// callback runs on the thread that installed it, so the input thread
// has its own message pump.
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
// Why the position comes from polling, not from the hook
// -------------------------------------------------------
// The mouse's POSITION is polled with GetCursorPos, once per frame,
// rather than taken from the hook's WM_MOUSEMOVE.  Two reasons:
//
//  * Entry detection.  If the pointer happens to be sitting over the
//    glasses' display when the app starts, no move event ever
//    arrives - the mouse is not moving.  Polling notices the pointer
//    is there immediately.
//
//  * Leave detection.  Polling compares the pointer's position
//    against the display's rectangle every frame, so the transition
//    is detected the moment it happens.
//
// The CLICK and WHEEL events still come from the hook, because those
// are events rather than states, and a poll would miss a click that
// happened between two frames.
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
public sealed class PointerInput : IDisposable
{
    private readonly VirtualCursor _cursor;
    private readonly Func<PanelPose?> _poseProvider;
    private readonly Func<Matrix4x4> _viewProvider;
    private readonly Func<Matrix4x4> _projectionProvider;

    private int _lastX;
    private int _lastY;
    private bool _haveLastPosition;
    private volatile bool _pointerInside;

    private IntPtr _hook;
    private Thread? _hookThread;
    private readonly CancellationTokenSource _cts = new();
    private NativeMethods.MouseHookProc? _hookProc;

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
    // The low-level hook
    // -----------------------------------------------------------------

    /// <summary>
    /// Install the low-level mouse hook on a dedicated thread.
    /// </summary>
    /// <remarks>
    /// The hook's callback is invoked on the thread that installed it,
    /// and that thread must pump messages - so it gets its own thread
    /// with its own message loop.
    ///
    /// The delegate is kept in a field, because the hook holds a raw
    /// pointer to it and a collected delegate would leave that
    /// pointer dangling.
    /// </remarks>
    public void StartHook()
    {
        if (_hookThread is { IsAlive: true }) return;

        _hookProc = OnMouseHook;

        _hookThread = new Thread(HookThreadLoop)
        {
            IsBackground = true,
            Name = "MouseHook",
            // Above normal: the hook's latency is the latency of the
            // user's click, and it should not queue behind the render
            // thread's work.
            Priority = ThreadPriority.AboveNormal,
        };
        _hookThread.Start();
    }

    private void HookThreadLoop()
    {
        try
        {
            _hook = NativeMethods.SetMouseHook(_hookProc!);
            if (_hook == IntPtr.Zero)
            {
                // The hook could not be installed - almost always
                // because another process has a higher-integrity
                // hook, or UAC is in the way.  The window messages
                // still work for clicks, just not for the wheel.
                OnLog?.Invoke(
                    "Could not install the low-level mouse hook - wheel scrolling over "
                    + "the glasses may not reach the projected app.");
            }

            // The hook's messages arrive here, so this thread has to
            // pump them.
            while (!_cts.IsCancellationRequested)
            {
                if (!NativeMethods.PeekMessage(out var msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
                {
                    Thread.Sleep(4);
                    continue;
                }
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }

    /// <summary>Raised for problems worth showing to the user.</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// The low-level mouse hook callback.
    /// </summary>
    /// <remarks>
    /// Only events over the glasses' display are consumed.  Everything
    /// else is passed straight down the chain, so the user's normal
    /// mouse behaviour on the other displays is untouched.
    ///
    /// The consumed events are SWALLOWED (a non-zero return), because
    /// the panel window would otherwise also see them and the click
    /// would be delivered twice.
    /// </remarks>
    private IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = (uint)wParam.ToInt64();
            if (message is NativeMethods.WM_LBUTTONDOWN
                or NativeMethods.WM_LBUTTONUP
                or NativeMethods.WM_RBUTTONDOWN
                or NativeMethods.WM_RBUTTONUP
                or NativeMethods.WM_MBUTTONDOWN
                or NativeMethods.WM_MBUTTONUP
                or NativeMethods.WM_MOUSEWHEEL)
            {
                var point = NativeMethods.GetPointFromHook(lParam);
                if (DisplayBounds.Contains(point.x, point.y))
                {
                    HandleHookEvent(message, lParam);
                    // Swallow it: the panel window would otherwise
                    // receive the same click and dispatch it twice.
                    return new IntPtr(1);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void HandleHookEvent(uint message, IntPtr lParam)
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
                RaiseWheel(NativeMethods.GetWheelDeltaFromHook(lParam));
                break;
        }
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
        //
        // The gain is per-EYE, not per-display: in the glasses'
        // side-by-side 3D mode the display is 3840 wide but each eye
        // only sees 1920 of it, and the mouse's travel maps onto ONE
        // eye's worth of panel.  Using the full 3840 would make the
        // cursor move at half speed.
        var (contentWidth, contentHeight) = ContentSize;
        var eyeWidth = RokidDisplayProfile.EyeViewportWidth(DisplayBounds.Width);
        var scaleX = contentWidth / (float)Math.Max(1, eyeWidth);
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

        // The mouse is over ONE eye's part of the display.  In the
        // glasses' side-by-side 3D mode the display is 3840 wide and
        // the bridge splits it at the halfway point, so the mouse's
        // pixel has to be mapped into the half it is actually over -
        // and into that eye's own 1920x1080 viewport - before it is
        // unprojected.  Unprojecting a 3840-wide pixel against a
        // 16:9 frustum would put the ray in the wrong place entirely.
        var eyeWidth = RokidDisplayProfile.EyeViewportWidth(DisplayBounds.Width);
        var eyeIndex = eyeWidth > 0 ? displayX / eyeWidth : 0;
        var localX = eyeWidth > 0 ? displayX - eyeIndex * eyeWidth : displayX;

        // Which eye is the user pointing at?  In side-by-side mode the
        // left half is the left eye and the right half the right eye,
        // and the content is the same in both, so the ray-cast is the
        // same either way.  We only need the LOCAL pixel.
        if (!PanelPointer.TryUnproject(
                localX, displayY,
                eyeWidth, DisplayBounds.Height,
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
    /// The panel window's mouse messages are a FALLBACK for the button
    /// events.  The low-level hook is the primary path, because the
    /// wheel can only be seen there.  When the hook is installed the
    /// hook swallows the events, so this method is not called for the
    /// same click twice.
    /// </remarks>
    public void HandleMouseMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        // If the hook is working, it owns the button events.
        if (_hook != IntPtr.Zero) return;

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

    public void Dispose()
    {
        _cts.Cancel();
        try { _hookThread?.Join(TimeSpan.FromSeconds(1)); } catch { }
        _hookProc = null;
        _cts.Dispose();
    }
}
