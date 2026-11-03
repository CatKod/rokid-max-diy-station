// SPDX-License-Identifier: MIT
//
// THE STALE-POSITION DETACH PROBE.
//
// THE STATE THE USER IS IN
// ------------------------
// The display-configuration DATABASE keeps one record per topology
// the machine has ever been in.  The record for the topology that
// included the Rokid Max holds the virtual display at
//
//   Position.cx = 1915
//   Position.cy = 4294966216   (= -1080 as a signed 32-bit value)
//
// which is a position Windows itself chose once, for a layout that no
// longer exists.
//
// When the machine comes back to the two-physical-monitor topology
// (the laptop panel and the XP-Pen), Windows rebuilds the desktop
// from that database - and if it restores the virtual display at
// (1915, -1080), the rebuilt desktop has the virtual display sitting
// ON TOP OF the XP-Pen.
//
// THAT IS THE STATE THIS PROBE CREATES, DELIBERATELY:
//
//   1. attach the virtual display;
//   2. move it to (1915, -1080) and SAVE that to the database, the
//      way Windows saved it;
//   3. ask the app to close the spatial display;
//   4. ask WINDOWS whether the virtual monitor is still on the
//      desktop.
//
// If step 4 finds the monitor still there, that is the user's bug,
// reproduced exactly.

using System.Runtime.InteropServices;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeStalePositionDetach;

internal static class Program
{
    private const int StaleX = 1915;
    private const int StaleY = -1080;

    private static int failures = 0;

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

    private static int Main()
    {
        Console.WriteLine("=== the desktop as it is now ===");
        PrintMonitors();

        Console.WriteLine();
        Console.WriteLine("=== 1. attach the virtual display ===");
        Check("the attach succeeds", DisplayConfigController.Attach(-1920, 0));
        Thread.Sleep(1200);

        Console.WriteLine();
        Console.WriteLine("=== 2. move it to the STALE position, and SAVE it ===");
        MoveVirtualTo(StaleX, StaleY);
        Thread.Sleep(1200);
        PrintMonitors();

        Console.WriteLine();
        Console.WriteLine("=== 3. close the spatial display ===");
        bool detached = DisplayConfigController.Detach();
        Console.WriteLine($"    Detach() returned {detached}");
        Thread.Sleep(1500);
        PrintMonitors();

        Console.WriteLine();
        Console.WriteLine("=== 4. what does WINDOWS think? ===");
        Check("the SHELL no longer lists the virtual monitor",
            !ShellHasVirtualMonitor(),
            "the rectangle is still on the user's desktop - this is the bug");

        Check("the CCD view agrees the virtual display is off",
            !DisplayConfigController.IsVirtualActive());

        Console.WriteLine();
        Console.WriteLine("=== and after a topology rebuild ===");
        RebuildTopology();
        Thread.Sleep(2000);
        PrintMonitors();
        Check("it stays off after a rebuild", !ShellHasVirtualMonitor());

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Move the virtual display to an absolute position, and PERSIST it.
    /// </summary>
    private static void MoveVirtualTo(int x, int y)
    {
        var paths = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var modes);
        if (paths is null)
        {
            Console.WriteLine("    the topology query failed");
            return;
        }

        foreach (var p in paths)
        {
            if (!Ccd.IsVirtual(p)) continue;
            Ccd.SetSourcePosition(modes, p, x, y);
        }

        int rc = Ccd.SetDisplayConfigRaw(
            (uint)paths.Length, paths,
            (uint)modes.Length, modes,
            0x00000080 /* APPLY */ | 0x00000020 /* USE_SUPPLIED */
            | 0x00000400 /* ALLOW_CHANGES */ | 0x00000200 /* SAVE_TO_DATABASE */);
        Console.WriteLine($"    SetDisplayConfig = {rc}");

        if (rc != 0)
        {
            Console.WriteLine("    (Windows refused the stale position - it overlaps a "
                + "physical display)");
        }
    }

    private static void RebuildTopology()
    {
        var paths = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var modes);
        if (paths is null) return;
        Ccd.SetDisplayConfigRaw(
            (uint)paths.Length, paths,
            (uint)modes.Length, modes,
            0x00000080 /* APPLY */ | 0x00000020 /* USE_SUPPLIED */
            | 0x00000400 /* ALLOW_CHANGES */);
    }

    private static void PrintMonitors()
    {
        Console.WriteLine("  --- the monitors the SHELL is drawing ---");
        bool any = false;
        bool Collect(IntPtr h, IntPtr dc, ref RECT r, IntPtr data)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(h, ref mi)) return true;
            any = true;
            bool virt = VirtualDisplayLocator.IsVirtualMonitorId(mi.szDevice);
            Console.WriteLine($"      {mi.szDevice,-24} "
                + $"({mi.rcMonitor.Left},{mi.rcMonitor.Top})-({mi.rcMonitor.Right},{mi.rcMonitor.Bottom})"
                + (virt ? "   <== THE VIRTUAL DISPLAY" : ""));
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
        if (!any) Console.WriteLine("      (none)");
    }

    private static bool ShellHasVirtualMonitor()
    {
        bool found = false;
        bool Collect(IntPtr h, IntPtr dc, ref RECT r, IntPtr data)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(h, ref mi)) return true;
            if (VirtualDisplayLocator.IsVirtualMonitorId(mi.szDevice)) found = true;
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
        return found;
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"
            + (detail is null ? "" : $"  ({detail})"));
        if (!ok) failures++;
    }
}
