// SPDX-License-Identifier: MIT
//
// The cursor feedback loop for the spatial display.
//
// In the True Spatial Display architecture the operating system does
// ALL of the input work: the virtual monitor is a real Windows display,
// so the OS hit-tests the mouse against the windows on it, activates
// them, orders them, drags them, and delivers clicks, wheel ticks and
// keystrokes.  There is no injection, no hook, no CDP.
//
// That leaves exactly ONE job for us, and it is a feedback job: draw
// the cursor where the OS thinks it is.
//
// The loop is:
//
//     GetCursorInfo()  ->  is it inside the virtual monitor's rectangle?
//                       ->  u = (x - left) / width,  v = (y - top) / height
//                       ->  seed the 3D cursor quad
//
// Why this is exact
// -----------------
// The process is PerMonitorV2 DPI aware (app.manifest), so GetCursorInfo
// and GetMonitorInfo report the same physical pixel space, and the
// monitor's pixel rectangle is exactly the size of the captured texture.
// The mapping is therefore a plain affine transform with no scaling
// factor, no gain, and no accumulated drift.
//
// Note what is NOT here: the differential gain that PointerInput used
// (scaleX = contentWidth / eyeWidth).  That existed because the mouse
// was moving over the glasses' REAL display, whose pixel grid is
// unrelated to the captured content's.  Here the mouse moves over the
// virtual monitor, whose pixel grid IS the content's, so an absolute
// mapping is both simpler and more accurate than a differential one.
//
// Why GetCursorInfo and not the mouse hook
// ----------------------------------------
// The old WH_MOUSE_LL hook was installed to intercept clicks so they
// could be forwarded to a browser over CDP.  With the OS handling the
// clicks there is nothing to intercept, and a global hook on every
// mouse move in the system is a cost we no longer need to pay.
// GetCursorInfo is a cheap, non-hooking call, and it also hands us the
// current cursor SHAPE, so the 3D cursor can become an I-beam over
// text and a resize arrow over a window edge.
using System.Runtime.InteropServices;
using RokidDiyStation.Core.Spatial;

namespace RokidDiyStation.App.Spatial;

/// <summary>
/// Tracks the OS cursor over the virtual display and drives the 3D
/// cursor quad.
/// </summary>
public sealed class SpatialPointerFeedback : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    private const int CURSOR_SHOWING = 0x00000001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    private readonly VirtualCursor _cursor;
    private readonly Func<MonitorInfo?> _monitorProvider;

    private volatile bool _inside;
    private IntPtr _lastCursorShape;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;

    /// <summary>
    /// The cursor this feedback drives.
    /// </summary>
    public VirtualCursor Cursor => _cursor;

    /// <summary>
    /// True while the OS cursor is over the virtual display.
    /// </summary>
    public bool IsInside => _inside;

    /// <summary>
    /// Raised when the cursor enters or leaves the virtual display.
    /// </summary>
    public event Action<bool>? OnInsideChanged;

    /// <summary>
    /// Raised when the OS cursor's shape changes.
    /// </summary>
    /// <remarks>
    /// The shape is the raw HCURSOR.  A caller that wants to draw a
    /// matching 3D cursor can compare it against the previous one; the
    /// handle is stable for as long as the shape does.
    /// </remarks>
    public event Action<IntPtr>? OnCursorShapeChanged;

    /// <summary>
    /// Create the feedback loop.
    /// </summary>
    /// <param name="cursor">The 3D cursor to drive.</param>
    /// <param name="monitorProvider">
    /// Supplies the virtual monitor, or null when it is not attached.
    /// </param>
    public SpatialPointerFeedback(
        VirtualCursor cursor,
        Func<MonitorInfo?> monitorProvider)
    {
        _cursor = cursor;
        _monitorProvider = monitorProvider;
    }

    /// <summary>
    /// Read the OS cursor and update the 3D cursor.
    /// </summary>
    /// <remarks>
    /// Call this once per rendered frame.  It is cheap - one
    /// GetCursorInfo call - and it does no work at all when the cursor
    /// has not moved.
    /// </remarks>
    public void Update()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci))
        {
            // The cursor info could not be read.  Treat it as "not over
            // the display" rather than leaving a stale cursor hanging in
            // the air.
            SetInside(false);
            return;
        }

        // The shape changed - tell the renderer so it can swap the 3D
        // cursor's bitmap.
        if (ci.hCursor != _lastCursorShape)
        {
            _lastCursorShape = ci.hCursor;
            OnCursorShapeChanged?.Invoke(ci.hCursor);
        }

        var monitor = _monitorProvider();
        if (monitor is null)
        {
            SetInside(false);
            return;
        }

        if (!monitor.TryToUv(ci.ptScreenPos.X, ci.ptScreenPos.Y, out var u, out var v))
        {
            // The cursor is on some other display - the desktop, the
            // Artist 13.3, the glasses' own screen.  There is no cursor
            // to draw in the spatial panel.
            SetInside(false);
            return;
        }

        SetInside(true);

        // Nothing to do if the cursor has not moved.  The virtual cursor
        // only rebuilds its quad when it is dirty, so skipping this
        // avoids a pointless vertex-buffer upload every frame.
        if (ci.ptScreenPos.X == _lastX && ci.ptScreenPos.Y == _lastY) return;
        _lastX = ci.ptScreenPos.X;
        _lastY = ci.ptScreenPos.Y;

        _cursor.Seed(u, v);
    }

    /// <summary>
    /// Whether the OS cursor is currently visible.
    /// </summary>
    public static bool IsCursorVisible
    {
        get
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            return GetCursorInfo(ref ci) && (ci.flags & CURSOR_SHOWING) != 0;
        }
    }

    private void SetInside(bool inside)
    {
        if (_inside == inside) return;
        _inside = inside;
        if (!inside) _cursor.Deactivate();
        OnInsideChanged?.Invoke(inside);
    }

    public void Dispose()
    {
        _cursor.Deactivate();
    }
}
