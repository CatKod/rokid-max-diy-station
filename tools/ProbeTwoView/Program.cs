// SPDX-License-Identifier: MIT
//
// THE TWO-VIEW PROBE.
//
// THE QUESTION
// ------------
// The user reports that closing the spatial display works with one
// physical monitor and with three, but NOT with two.  The machine is
// in the two-monitor state right now (the laptop panel and the XP-Pen
// artist display; the Rokid Max is not plugged in), so the failure
// can be reproduced here.
//
// The code has TWO views of the desktop:
//
//   * the SHELL's view  - EnumDisplayMonitors, which is what the user
//     SEES, and what the mouse and every window are placed against;
//
//   * the CCD's view     - QueryDisplayConfig, which is what the
//     attach and the detach are built on, and what decides whether
//     the virtual display is "on the desktop" as far as the code is
//     concerned.
//
// The detach reports success when the CCD's view says the virtual
// display is gone.  If the SHELL's view still shows it, the user is
// looking at a rectangle that the code believes it has removed.  That
// is exactly the reported symptom, and nothing in the code compares
// the two views at the moment the detach is declared successful.
//
// So this probe dumps BOTH views, side by side, at every step of the
// lifecycle, and cross-checks them.
//
// THE THREE CCD QUERIES
// ---------------------
//   QDC_ONLY_ACTIVE_PATHS (0x2) - the paths that are on the desktop
//       right now.
//
//   QDC_DATABASE_CURRENT (0x4)  - the topology Windows has PERSISTED.
//       This is the one Windows rebuilds the desktop from, so its
//       positions can be stale - and they can OVERLAP, which Windows
//       refuses to apply.
//
//   QDC_ALL_PATHS (0x1)         - every path, connected or not.
//
// The virtual display's path has to be found in the DATABASE while it
// is detached, because a detached display is not in the active set.
// Which query actually returns it is worth knowing.

using System.Runtime.InteropServices;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeTwoView;

internal static class Program
{
    private static int failures = 0;

    private static int Main()
    {
        Console.WriteLine("=== the starting state ===");
        Dump();

        Console.WriteLine();
        Console.WriteLine("=== attach the virtual display at the computed position ===");
        var (x, y) = VirtualDisplayController.ComputeAttachPosition();
        Console.WriteLine($"  the computed position is ({x},{y})");
        Console.WriteLine($"  Attach = {DisplayConfigController.Attach(x, y)}");
        Thread.Sleep(1500);
        Dump();

        Console.WriteLine();
        Console.WriteLine("=== detach it ===");
        Console.WriteLine($"  Detach = {DisplayConfigController.Detach()}");
        Thread.Sleep(1500);
        Dump();

        Console.WriteLine();
        Console.WriteLine("=== attach it AGAIN, from the detached state ===");
        var (x2, y2) = VirtualDisplayController.ComputeAttachPosition();
        Console.WriteLine($"  the computed position is ({x2},{y2})");
        bool attached = DisplayConfigController.Attach(x2, y2);
        Console.WriteLine($"  Attach = {attached}");
        Thread.Sleep(1500);
        Dump();

        Check("the virtual display can be attached again after a detach", attached);

        Console.WriteLine();
        Console.WriteLine("=== and detach it again ===");
        Console.WriteLine($"  Detach = {DisplayConfigController.Detach()}");
        Thread.Sleep(1500);
        Dump();

        Check("the shell does not list the virtual monitor at the end",
            !ShellHasVirtualMonitor());

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");
        return failures == 0 ? 0 : 1;
    }

    private static void Dump()
    {
        Console.WriteLine("  --- the SHELL's view (what the user sees) ---");
        foreach (var m in VirtualDisplayLocator.Enumerate())
            Console.WriteLine($"      {m.DeviceName,-16} {m.Width}x{m.Height} at "
                + $"({m.Bounds.Left},{m.Bounds.Top})  virtual={m.IsVirtual}");

        Console.WriteLine("  --- the CCD's view ---");
        foreach (var (flags, label) in new[]
        {
            (Ccd.QDC_ONLY_ACTIVE_PATHS, "ONLY_ACTIVE"),
            (Ccd.QDC_DATABASE_CURRENT, "DATABASE_CURRENT"),
            (Ccd.QDC_ALL_PATHS, "ALL_PATHS"),
        })
        {
            var paths = Ccd.Query(flags, out var modes);
            if (paths is null) { Console.WriteLine($"      {label}: the query failed"); continue; }

            Console.WriteLine($"      {label}: {paths.Length} path(s)");
            for (int i = 0; i < paths.Length; i++)
            {
                var p = paths[i];
                bool virt = Ccd.IsVirtual(p);
                string pos = "(no mode)";
                if (p.sourceInfo.modeInfoIdx != uint.MaxValue
                    && p.sourceInfo.modeInfoIdx < modes.Length)
                {
                    var m = modes[p.sourceInfo.modeInfoIdx];
                    if (m.infoType == 1)
                        pos = $"({m.sourceMode.position.x},{m.sourceMode.position.y}) "
                            + $"{m.sourceMode.Width}x{m.sourceMode.Height}";
                }
                Console.WriteLine($"        [{i}] active={((p.flags & 0x1) != 0 ? "Y" : "N")} "
                    + $"virtual={virt,-5} pos={pos}");
            }
        }

        Console.WriteLine($"      the code's answer: IsVirtualActive = "
            + $"{DisplayConfigController.IsVirtualActive()}, "
            + $"IsAttached = {VirtualDisplayController.IsAttached()}, "
            + $"the shell has it = {ShellHasVirtualMonitor()}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr h, IntPtr dc, ref RECT r, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);

    /// <summary>
    /// Whether the SHELL is drawing the virtual monitor.
    /// </summary>
    /// <remarks>
    /// The shell's device name is "\\.\DISPLAYn\Monitor0", which does
    /// NOT carry the EDID fingerprint - so the identity has to be
    /// resolved through the adapter, exactly like
    /// VirtualDisplayLocator does.
    /// </remarks>
    private static bool ShellHasVirtualMonitor()
        => VirtualDisplayLocator.Enumerate().Any(m => m.IsVirtual);

    private static void Check(string what, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"
            + (detail is null ? "" : $"  ({detail})"));
        if (!ok) failures++;
    }
}
