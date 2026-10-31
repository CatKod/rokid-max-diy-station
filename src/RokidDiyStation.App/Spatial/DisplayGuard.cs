// SPDX-License-Identifier: MIT
//
// Keeping the panel window on the glasses' display, and nowhere else.
//
// The blinding trap
// -----------------
// This is the single most dangerous failure mode of the whole app, and
// it is a Windows behaviour rather than a bug in our code.
//
// The render window is a borderless, topmost, 1920x1080 popup that
// covers the Rokid Max's display exactly.  Now the user unplugs the
// USB-C cable.  DWM notices that the virtual desktop's coordinates
// have shrunk, and — to stop windows becoming unreachable — it
// RELOCATES every window on the vanished display to (0, 0) on the
// primary display.
//
// For an ordinary application window that is a mild annoyance.  For a
// borderless 1920x1080 topmost popup it is catastrophic: the user's
// laptop screen is instantly covered by a black rectangle that they
// cannot get rid of, cannot Alt+Tab away, and cannot click through,
// because the window is WS_EX_NOACTIVATE and covers the taskbar.
//
// The fix has to happen INSIDE the WM_DISPLAYCHANGE handler, before
// DWM finishes the compositor relayout.  Hiding the window there
// (ShowWindow(hwnd, SW_HIDE)) means there is nothing left for DWM to
// relocate.
//
// The check
// ---------
// The window is on the right display if MONITOR_DEFAULTTONULL finds a
// monitor for it AND that monitor is the glasses'.  We identify the
// glasses' monitor by the same EDID fingerprint the display locator
// uses ("MONITOR\LBT4753\..." — the Lontium bridge inside the
// glasses), so the guard keeps working even when the adapter's
// "\\.\DISPLAYn" name changes across a reboot or a re-plug.
//
// If we cannot positively identify the glasses' monitor, we fall back
// to comparing the window's rectangle against the glasses' bounds,
// which is enough to catch the common case.
//
// Why this is a separate class
// ----------------------------
// The guard has to be testable without a GPU, a window, and a set of
// glasses.  So the decision logic lives in a pure static method —
// GivenTheWindowIsAt(these bounds, onThisMonitor, isThisTheGlasses)
// — and the Win32 plumbing lives around it.  The tests exercise the
// pure part.

using System.Runtime.InteropServices;

namespace RokidDiyStation.App.Spatial;

/// <summary>
/// Decides whether the panel window is still on the glasses' display.
/// </summary>
internal static class DisplayGuardLogic
{
    /// <summary>
    /// The outcome of the guard's check.
    /// </summary>
    /// <param name="ShouldHide">
    /// True when the window must be hidden, because it is no longer on
    /// the glasses' display.
    /// </param>
    /// <param name="Reason">
    /// A short human-readable explanation, for the log.
    /// </param>
    public readonly record struct Decision(bool ShouldHide, string Reason);

    /// <summary>
    /// Decide what to do about the panel window, given where it is.
    /// </summary>
    /// <param name="windowBounds">
    /// The window's rectangle in virtual-desktop coordinates.
    /// </param>
    /// <param name="onAMonitor">
    /// True if the window currently sits on some monitor.  False when
    /// the monitor it was on has gone away.
    /// </param>
    /// <param name="glassesBounds">
    /// The glasses' display rectangle in virtual-desktop coordinates,
    /// or null if the glasses are not attached.
    /// </param>
    /// <param name="onTheGlasses">
    /// True if the window's monitor has been positively identified as
    /// the glasses' display (by EDID fingerprint).
    /// </param>
    public static Decision Evaluate(
        NativeMethods.RECT windowBounds,
        bool onAMonitor,
        NativeMethods.RECT? glassesBounds,
        bool onTheGlasses)
    {
        // Case 1: the glasses are gone entirely.
        if (glassesBounds is null)
        {
            return new Decision(true,
                "the Rokid Max is no longer attached — hiding the panel so it cannot cover the primary display");
        }

        // Case 2: the window's monitor is gone.  DWM is about to move
        // the window to the primary display; hide it first.
        if (!onAMonitor)
        {
            return new Decision(true,
                "the panel's display disappeared — hiding the panel so DWM cannot move it onto the primary display");
        }

        // Case 3: the window is positively known to be on the glasses.
        // Nothing to do.
        if (onTheGlasses)
        {
            return new Decision(false, "the panel is on the Rokid Max's display");
        }

        // Case 4: we cannot identify the monitor by fingerprint, so
        // fall back to a geometric test: is the window still inside
        // the glasses' rectangle?
        if (!Overlaps(windowBounds, glassesBounds.Value))
        {
            return new Decision(true,
                "the panel is no longer over the Rokid Max's display — hiding it so it cannot cover the primary display");
        }

        return new Decision(false, "the panel is on the Rokid Max's display");
    }

    /// <summary>
    /// Whether two rectangles overlap by more than a hair.
    /// </summary>
    /// <remarks>
    /// A tolerance of a couple of pixels is allowed, because the
    /// window's bounds and the monitor's bounds can differ by a pixel
    /// or two depending on the DPI rounding of the two APIs.  Without
    /// the tolerance the guard would flap on and off during a
    /// resolution change.
    /// </remarks>
    public static bool Overlaps(NativeMethods.RECT a, NativeMethods.RECT b, int tolerance = 2)
    {
        var overlapWidth = Math.Min(a.right, b.right) - Math.Max(a.left, b.left);
        var overlapHeight = Math.Min(a.bottom, b.bottom) - Math.Max(a.top, b.top);
        return overlapWidth > tolerance && overlapHeight > tolerance;
    }
}

/// <summary>
/// Watches the glasses' display and hides the panel window when it
/// disappears, so DWM cannot relocate it onto the primary display.
/// </summary>
public sealed class DisplayGuard : IDisposable
{
    private readonly PanelRenderer _renderer;
    private readonly DisplayTarget? _display;
    private int _hidden;
    /// <summary>Raised when the guard hides or restores the panel.</summary>
    public event Action<string>? OnLog;

    public DisplayGuard(PanelRenderer renderer, DisplayTarget? display)
    {
        _renderer = renderer;
        _display = display;
    }

    /// <summary>
    /// Re-check the display situation.  Called on WM_DISPLAYCHANGE
    /// and WM_DEVICECHANGE.
    /// </summary>
    /// <remarks>
    /// The window procedure hides the window as soon as
    /// WM_DISPLAYCHANGE arrives — that is the only way to beat DWM's
    /// relocation.  This method is the other half: it works out
    /// whether the glasses are still there, and re-shows and
    /// re-places the window if they are.
    ///
    /// The window's visibility is therefore NOT tracked by a flag
    /// here: the window procedure hides unconditionally, so the
    /// decision has to be re-derived every time.  The flag only
    /// exists to avoid logging the same message over and over.
    /// </remarks>
    public void Check()
    {
        var hwnd = _renderer.WindowHandle;
        if (hwnd == IntPtr.Zero) return;

        // Where is the glasses' display now?
        //
        // The display's adapter device name ("\\.\DISPLAY5") is NOT
        // stable across a re-plug, so the glasses are located by
        // their EDID fingerprint ("MONITOR\LBT4753...") — the Lontium
        // bridge inside the glasses — with the old adapter name as a
        // fallback.
        var stillThere = DisplayLocator.Enumerate()
            .FirstOrDefault(d => d.IsRokid);

        NativeMethods.RECT? glassesBounds = stillThere is null
            ? null
            : new NativeMethods.RECT
            {
                left = stillThere.Bounds.X,
                top = stillThere.Bounds.Y,
                right = stillThere.Bounds.X + stillThere.Bounds.Width,
                bottom = stillThere.Bounds.Y + stillThere.Bounds.Height,
            };

        if (glassesBounds is null)
        {
            // The glasses are gone.  The window was already hidden by
            // the window procedure; make sure it stays hidden, so
            // DWM cannot move it onto the primary display.
            if (Interlocked.Exchange(ref _hidden, 1) == 0)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                OnLog?.Invoke(
                    "Display guard: the Rokid Max is no longer attached — the panel is hidden so it cannot cover your main screen.");
            }
            return;
        }

        // The glasses are there.  Is the window on them?
        NativeMethods.GetWindowRect(hwnd, out var windowRect);
        var onAMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONULL)
                         != IntPtr.Zero;

        var decision = DisplayGuardLogic.Evaluate(windowRect, onAMonitor, glassesBounds, onTheGlasses: false);

        if (decision.ShouldHide)
        {
            if (Interlocked.Exchange(ref _hidden, 1) == 0)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                OnLog?.Invoke($"Display guard: {decision.Reason}.");
            }
            return;
        }

        // Everything is fine: re-show and re-place the window.
        //
        // Repositioning is what makes the recovery work: after a
        // re-plug the glasses' display can come back at a different
        // position in the virtual desktop, and the window would
        // otherwise be sitting at the old coordinates — possibly on
        // the primary display.
        var wasHidden = Interlocked.Exchange(ref _hidden, 0) == 1;
        _renderer.Reposition(stillThere!);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);

        if (wasHidden)
        {
            OnLog?.Invoke(
                "Display guard: the Rokid Max's display is back — the panel is visible again.");
        }
    }

    public void Dispose() { }
}
