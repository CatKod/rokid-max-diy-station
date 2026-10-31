// SPDX-License-Identifier: MIT
//
// Picking the window to pin.
//
// The user says "pin THIS window", and the only way to know which
// one they mean is to ask.  Two options exist:
//
//   1. The system GraphicsCapturePicker — a modal OS dialog.  It works
//      from an unpackaged app, but ONLY if it is initialised with
//      IInitializeWithWindow, or the modal dialog has no parent and
//      the call fails with E_FAIL.
//
//   2. Our own list of the top-level windows, rebuilt on demand.
//
// Option 2 is better for a tray app: the list can be refreshed, it
// can be presented as a menu, and it does not steal focus with a modal
// system dialog in the middle of a game.  It also lets us filter out
// the windows that cannot be captured anyway (minimised, cloaked, our
// own).
//
// The system picker is still worth having for the "capture something
// unusual" case, so both are exposed.
using System.Runtime.InteropServices;
using System.Text;

namespace RokidDiyStation.App.Spatial;

/// <summary>A capturable top-level window.</summary>
/// <param name="Handle">The window handle.</param>
/// <param name="Title">The window's title bar text.</param>
/// <param name="ClassName">The window's class name.</param>
/// <param name="ProcessName">The owning process's name.</param>
/// <param name="IsMinimised">Whether the window is currently minimised.</param>
public sealed record WindowEntry(
    IntPtr Handle,
    string Title,
    string ClassName,
    string ProcessName,
    bool IsMinimised)
{
    public override string ToString()
    {
        var mark = IsMinimised ? " [minimised]" : "";
        return $"{Title}  —  {ProcessName}{mark}";
    }
}

/// <summary>Enumerates the windows that can be captured.</summary>
public static class WindowPicker
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr h);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr h);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    /// <summary>
    /// DwmGetWindowAttribute lives in dwmapi.dll, not user32.dll.
    /// </summary>
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

    /// <summary>The DWMWA_CLOAKED attribute: a window cloaked by the
    /// system is not visible on any display.</summary>
    private const int DWMWA_CLOAKED = 14;

    /// <summary>
    /// List the windows that are worth offering to the user.
    /// </summary>
    /// <param name="exclude">
    /// Window handles to leave out — our own panel, for instance.
    /// </param>
    public static IReadOnlyList<WindowEntry> List(IEnumerable<IntPtr>? exclude = null)
    {
        var skip = new HashSet<IntPtr>(exclude ?? Array.Empty<IntPtr>());
        var ourPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        var result = new List<WindowEntry>();

        EnumWindows((h, _) =>
        {
            if (h == GetShellWindow() || h == GetDesktopWindow()) return true;
            if (skip.Contains(h)) return true;
            if (!IsWindowVisible(h)) return true;

            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            // Cloaked windows are "visible" to EnumWindows but are not
            // actually on screen (they are what the shell uses for
            // suspended UWP apps and virtual-desktop thumbnails).
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
                && cloaked != 0)
                return true;

            GetClassName(h, sb, 256);
            var cls = sb.ToString();

            GetWindowThreadProcessId(h, out var pid);
            // Never offer our own windows.
            if (pid == ourPid) return true;

            result.Add(new WindowEntry(
                h, title, cls, ProcessNameOf(pid), IsIconic(h)));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static string ProcessNameOf(uint pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>
    /// Whether a window still exists.  Used to clean up a capture whose
    /// source has been closed.
    /// </summary>
    public static bool Exists(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>GA_ROOT: walk up to the top-level ancestor.</summary>
    private const uint GA_ROOT = 2;

    /// <summary>
    /// The top-level window under the mouse cursor.
    /// </summary>
    /// <remarks>
    /// There is no "GetCursorWindow" API in user32 — the obvious
    /// P/Invoke for it throws EntryPointNotFoundException, which is
    /// easy to miss because the hotkey handler swallows exceptions.
    /// The real way to do it is to ask where the cursor is and then
    /// walk up from the child window to its top-level owner.
    ///
    /// We prefer the cursor's window over the foreground window
    /// because the user is usually pointing at the thing they want to
    /// pin, while keyboard focus may still be on the app they launched
    /// it from.
    /// </remarks>
    public static IntPtr GetCursorWindow()
    {
        try
        {
            if (GetCursorPos(out var pt))
            {
                var child = WindowFromPoint(pt);
                if (child != IntPtr.Zero)
                {
                    var root = GetAncestor(child, GA_ROOT);
                    if (root != IntPtr.Zero) return root;
                }
            }

            // No window under the cursor (desktop, or a fullscreen
            // exclusive surface): fall back to the focused window.
            var fg = GetForegroundWindow();
            return fg;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
