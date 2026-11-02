// SPDX-License-Identifier: MIT
//
// Keeping the mouse off the Rokid Max's own display.
//
// THE PROBLEM
// -----------
// The user's setup is:
//
//     Display 1  (0,0)      the laptop's panel
//     Display 2  (1920,0)   the XP-Pen Artist 13.3
//     Display 5  (3840,0)   the Rokid Max's own screen
//     the virtual display  (-1920,0)  the spatial panel's canvas
//
// The Rokid Max's screen is a PROJECTION CANVAS.  What the user
// actually sees in the glasses is the virtual display, rendered by
// the panel.  The glasses' own screen shows the panel as well - but
// the user's mouse has no business there: it is a mirror of the
// panel, not a work surface.  Letting the cursor wander onto it
// means the arrow appears somewhere the user cannot see properly, and
// clicking there does something the user did not intend.
//
// So: while the spatial display is open, the cursor is kept out of
// the glasses' display rectangle.
//
// WHY NOT ClipCursor
// ------------------
// ClipCursor is the obvious API and it is the wrong one.  It is a
// HARD barrier: the cursor cannot leave the clip rectangle at all.
//
//   * The XP-Pen Artist 13.3 is an ABSOLUTE digitiser mapped to
//     DISPLAY2.  While the cursor is clipped away from that screen,
//     the kernel CLAMPS the pen's absolute coordinates to the nearest
//     edge of the clip rectangle - and the user physically cannot
//     draw.  That is a hard blocker for a product whose companion is
//     a drawing tablet.
//
//   * ClipCursor is also reset by the system on a desktop switch
//     (UAC prompt, Win+L), so any release logic would have to handle
//     that too.
//
// The bar this class installs is SOFT: the cursor is pushed back, but
// the user can always get out.
//
// HOW THE BAR WORKS
// -----------------
// A WH_MOUSE_LL hook watches every mouse move.  When the cursor is
// about to cross into the glasses' rectangle, the hook:
//   * calls SetCursorPos to put it back on the near edge, and
//   * swallows the event, so the cursor does not visibly cross.
//
// The hook is a GLOBAL hook, so it costs something - a hook call on
// every mouse move in the system.  That cost is acceptable HERE and
// nowhere else, because the hook only exists while the spatial
// display is open, and it only does work when the cursor is near the
// glasses' rectangle.  Everywhere else the callback returns
// immediately.
//
// The alternative - polling GetCursorPos on a timer - is cheaper but
// lags: the cursor visibly enters the forbidden rectangle for a frame
// before it is pushed back, which looks like a flicker.
//
// THE HOOK'S OWN THREAD
// ---------------------
// A low-level hook's callback is invoked by the SYSTEM, but it is
// DISPATCHED on the thread that installed the hook - and that thread
// must pump messages, or the hook never fires at all.
//
// The barrier therefore owns a dedicated thread that does nothing but
// install the hook and pump messages.  Installing it on the render
// thread would work too, but the render thread is the one thing that
// must never stall, and the barrier's lifecycle is independent of the
// renderer's.
//
// WHY A SEPARATE CLASS
// --------------------
// The decision logic is a pure static method, so it can be tested
// without a hook, a mouse, or a set of glasses.  The Win32 plumbing
// lives around it.
using System.Runtime.InteropServices;

namespace RokidDiyStation.App.Spatial;

/// <summary>
/// Decides what to do about a cursor that is heading into the
/// glasses' display.
/// </summary>
internal static class MouseBarrierLogic
{
    /// <summary>
    /// The outcome of the barrier's check.
    /// </summary>
    /// <param name="ShouldPushBack">
    /// True when the cursor must be pushed back out.
    /// </param>
    /// <param name="PushBackX">
    /// The X coordinate to push the cursor to, when
    /// <paramref name="ShouldPushBack"/> is true.
    /// </param>
    public readonly record struct Decision(bool ShouldPushBack, int PushBackX);

    /// <summary>
    /// Decide whether a cursor at the given position must be pushed
    /// out of the forbidden rectangle.
    /// </summary>
    /// <param name="x">The cursor's X coordinate in virtual-desktop pixels.</param>
    /// <param name="y">The cursor's Y coordinate in virtual-desktop pixels.</param>
    /// <param name="forbidden">
    /// The rectangle the cursor must stay out of, or null when there
    /// is no barrier.
    /// </param>
    /// <param name="forbiddenIsToTheRight">
    /// True when the forbidden rectangle lies to the RIGHT of the
    /// working displays - which is the usual layout, where the
    /// glasses sit past the laptop and the tablet - so the cursor is
    /// pushed back to its LEFT edge.  When the glasses are on the
    /// other side, the cursor is pushed to the right instead.
    /// </param>
    public static Decision Evaluate(
        int x, int y, NativeMethods.RECT? forbidden, bool forbiddenIsToTheRight)
    {
        if (forbidden is null) return new Decision(false, x);

        var f = forbidden.Value;

        // Only the X axis matters.  The glasses' display is a full
        // column at the edge of the virtual desktop, and the user
        // moves between displays horizontally.
        //
        // The vertical extent is deliberately NOT enforced: the
        // glasses' screen and the displays beside it can overlap
        // vertically, and a user working on the laptop's panel at
        // Y = 200 must not be blocked because the glasses' rectangle
        // happens to span that Y too.
        if (x < f.left || x >= f.right) return new Decision(false, x);

        // The cursor is inside the forbidden column.  Push it back
        // to the nearest edge that is NOT the forbidden one.
        //
        // If the forbidden rectangle is to the right of the working
        // area, the cursor is pushed to just left of it.  If it is to
        // the left, the cursor is pushed to just right of it.
        var pushTo = forbiddenIsToTheRight ? f.left - 1 : f.right + 1;
        return new Decision(true, pushTo);
    }
}

/// <summary>
/// Keeps the mouse cursor out of the Rokid Max's own display while
/// the spatial display is open.
/// </summary>
public sealed class MouseBarrier : IDisposable
{
    private readonly NativeMethods.MouseHookProc _callback;
    private Thread? _thread;
    private IntPtr _hook;
    private NativeMethods.RECT? _forbidden;
    private volatile bool _forbiddenIsToTheRight = true;
    private volatile bool _active;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _hookInstalled;

    /// <summary>Raised when the barrier pushes the cursor back.</summary>
    public event Action<int>? OnPushBack;

    /// <summary>Whether the barrier is currently installed.</summary>
    public bool IsActive => _active;

    /// <summary>
    /// Install the barrier, keeping the cursor out of the given
    /// rectangle.
    /// </summary>
    /// <param name="forbidden">
    /// The rectangle the cursor must stay out of - the glasses'
    /// display.
    /// </param>
    /// <param name="forbiddenIsToTheRight">
    /// True when the forbidden rectangle lies to the right of the
    /// working displays.
    /// </param>
    /// <returns>True when the hook was installed.</returns>
    public bool Install(NativeMethods.RECT forbidden, bool forbiddenIsToTheRight)
    {
        if (_active) return true;

        _forbidden = forbidden;
        _forbiddenIsToTheRight = forbiddenIsToTheRight;
        _active = true;
        _hookInstalled = false;
        _ready.Reset();

        _thread = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "MouseBarrier",
        };
        _thread.Start();

        // The hook has to be installed ON the barrier's own thread,
        // and the result is reported back through this flag, so the
        // caller knows whether the barrier is real or not.
        if (!_ready.Wait(TimeSpan.FromSeconds(3)))
        {
            Uninstall();
            return false;
        }

        return _hookInstalled;
    }

    /// <summary>Remove the barrier.</summary>
    public void Uninstall()
    {
        if (!_active) return;
        _active = false;
        _forbidden = null;

        // The hook is removed on the thread that installed it, which
        // is the only thread allowed to remove it safely.
        var thread = _thread;
        if (thread is not null && thread.IsAlive)
        {
            // The thread's loop exits as soon as _active goes false
            // and it uninstalls the hook on the way out.
            thread.Join(TimeSpan.FromSeconds(2));
        }
        _thread = null;
    }

    /// <summary>
    /// The barrier's own thread: install the hook, then pump messages
    /// until the barrier is uninstalled.
    /// </summary>
    private void HookThread()
    {
        try
        {
            _hook = NativeMethods.SetMouseHook(_callback);
            _hookInstalled = _hook != IntPtr.Zero;
        }
        catch
        {
            _hookInstalled = false;
        }

        _ready.Set();

        try
        {
            while (_active)
            {
                // The hook's callback is dispatched on THIS thread, so
                // the message queue has to be drained here.  Without
                // this the hook never fires.
                while (NativeMethods.PeekMessage(out var msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
                {
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }

                Thread.Sleep(5);
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

    /// <summary>
    /// The hook callback.
    /// </summary>
    private IntPtr OnMouseMove(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (!_active)
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        // nCode < 0 means the hook could not process the event; the
        // convention is to pass it on untouched.
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        // Only mouse moves matter.  The button events and the wheel
        // are delivered to whatever is under the cursor, and by the
        // time they arrive the cursor has already been pushed back.
        const int WM_MOUSEMOVE = 0x0200;
        if ((int)wParam != WM_MOUSEMOVE)
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        var point = NativeMethods.GetPointFromHook(lParam);
        var decision = MouseBarrierLogic.Evaluate(
            point.x, point.y, _forbidden, _forbiddenIsToTheRight);

        if (!decision.ShouldPushBack)
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        // Push the cursor back to the edge and swallow the move, so
        // the arrow never visibly crosses into the glasses' display.
        NativeMethods.SetCursorPos(decision.PushBackX, point.y);

        try { OnPushBack?.Invoke(decision.PushBackX); } catch { }

        // Returning a non-zero value swallows the event.
        return new IntPtr(1);
    }

    public MouseBarrier()
    {
        // The delegate is kept alive for the lifetime of the object:
        // the hook holds a raw pointer to it, and a collected
        // delegate would leave that pointer dangling.
        _callback = OnMouseMove;
    }

    public void Dispose()
    {
        Uninstall();
        _ready.Dispose();
    }
}
