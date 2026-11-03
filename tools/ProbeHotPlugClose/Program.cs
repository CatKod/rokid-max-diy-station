// SPDX-License-Identifier: MIT
//
// THE HOT-PLUG CLOSE PROBE.
//
// THE USER'S WORDS
// -----------------
//   "I plugged the XP-Pen in so you could test."
//
// So the trigger is a TOPOLOGY CHANGE WHILE THE APP IS RUNNING: the
// app was already up with the virtual display attached, the XP-Pen
// was plugged in, and THEN the spatial display was closed - and the
// virtual display stayed on the desktop.
//
// A hot-plug is the one thing that makes Windows REBUILD the desktop
// from its own display-configuration database, and the database keeps
// one record per topology the machine has ever been in.  Those
// records are not consistent with one another, and one of them holds
// the virtual display at (1915, -1080) - on top of the XP-Pen.
//
// THIS PROBE REPRODUCES THE HOT-PLUG WITHOUT UNPLUGGING ANYTHING.
//
// The XP-Pen's path is DISCONNECTED from the desktop - exactly what
// Windows' Display Settings does when the user turns a display off,
// and exactly the state Windows is in for a moment while it handles
// a real unplug - and then it is connected again.
//
// The disconnect is done through the CCD API with
// SDC_SAVE_TO_DATABASE, so the database is rewritten the way a real
// hot-plug rewrites it.  That is what makes the difference between
// this and a plain rebuild.
//
// THE MEASUREMENT
// ----------------
// After the hot-plug, the app is asked to close the spatial display
// through its own named pipe, and then WINDOWS is asked what is on
// the desktop - not the CCD, not the app's own view: the shell.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeHotPlugClose;

internal static class Program
{
    private const string Exe = @"artifacts\release\RokidDiyStation.exe";
    private const string PipeName = "RokidDiyStation.ShowWindow";

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
        KillApp();
        Thread.Sleep(1000);

        var app = Process.Start(new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(Exe))!,
        });
        if (app is null) { Console.WriteLine("the app did not start"); return 1; }

        Thread.Sleep(9000);
        Console.WriteLine("=== the app is running, the spatial display is open ===");
        PrintMonitors();

        Console.WriteLine();
        Console.WriteLine("=== the HOT-PLUG: the XP-Pen's path is disconnected ===");
        DisconnectOnePhysicalDisplay();
        Thread.Sleep(2500);
        PrintMonitors();

        Console.WriteLine();
        Console.WriteLine("=== and connected again ===");
        ConnectPhysicalDisplay();
        Thread.Sleep(2500);
        PrintMonitors();

        Console.WriteLine();
        Console.WriteLine("=== the app is asked to CLOSE the spatial display ===");
        Console.WriteLine($"  the signal was delivered: {Signal("close-spatial")}");
        Thread.Sleep(7000);
        PrintMonitors();

        Check("the SHELL no longer lists the virtual monitor",
            !ShellHasVirtualMonitor(),
            "the rectangle is still on the user's desktop - this is the bug");

        Console.WriteLine();
        Console.WriteLine("=== and after a further rebuild ===");
        Rebuild();
        Thread.Sleep(2500);
        PrintMonitors();
        Check("it stays off after a rebuild", !ShellHasVirtualMonitor());

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Disconnect one physical display's path, and save it to the
    /// database - the state Windows is in while it handles an unplug.
    /// </summary>
    private static void DisconnectOnePhysicalDisplay()
    {
        var paths = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var modes);
        if (paths is null) return;

        // The XP-Pen is the SECOND physical display; find it by its
        // position - it is the one at x = 1920.
        foreach (var p in paths)
        {
            if (Ccd.IsVirtual(p)) continue;
            if (p.sourceInfo.modeInfoIdx == uint.MaxValue) continue;
            if (p.sourceInfo.modeInfoIdx >= modes.Length) continue;

            var sm = modes[p.sourceInfo.modeInfoIdx].sourceMode;
            if (sm.position.x != 1920) continue;

            var disconnected = p;
            disconnected.flags &= ~0x1u;
            disconnected.sourceInfo.modeInfoIdx = uint.MaxValue;
            disconnected.targetInfo.modeInfoIdx = uint.MaxValue;

            int rc = ApplyWith(paths, modes, disconnected);
            Console.WriteLine($"    the XP-Pen's path was disconnected: SetDisplayConfig = {rc}");
            return;
        }

        Console.WriteLine("    the XP-Pen's path was not found");
    }

    /// <summary>
    /// Put the display back.
    /// </summary>
    private static void ConnectPhysicalDisplay()
    {
        var paths = Ccd.Query(Ccd.QDC_ALL_PATHS, out var modes);
        if (paths is null) return;

        foreach (var p in paths)
        {
            if (Ccd.IsVirtual(p)) continue;
            if ((p.flags & 0x1) != 0) continue;
            if (p.sourceInfo.modeInfoIdx == uint.MaxValue) continue;
            if (p.sourceInfo.modeInfoIdx >= modes.Length) continue;

            var sm = modes[p.sourceInfo.modeInfoIdx].sourceMode;
            if (sm.position.x != 1920) continue;

            var connected = p;
            connected.flags |= 0x1u;

            int rc = ApplyWith(paths, modes, connected);
            Console.WriteLine($"    the XP-Pen's path was connected: SetDisplayConfig = {rc}");
            return;
        }

        Console.WriteLine("    the XP-Pen's path was not found in the database");
    }

    /// <summary>
    /// Apply a configuration in which one path has been replaced.
    /// </summary>
    private static int ApplyWith(Ccd.Path[] paths, Ccd.ModeInfo[] modes, Ccd.Path replacement)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            if (!Ccd.IsVirtual(paths[i])) continue;
            if (paths[i].sourceInfo.modeInfoIdx == uint.MaxValue) continue;
            if (paths[i].sourceInfo.modeInfoIdx >= modes.Length) continue;
            if (modes[paths[i].sourceInfo.modeInfoIdx].sourceMode.position.x != 1920) continue;

            paths[i] = replacement;
            break;
        }

        foreach (var flags in new uint[]
        {
            0x00000080u | 0x00000020u | 0x00000400u | 0x00000200u | 0x00002000u | 0x00001000u,
            0x00000080u | 0x00000020u | 0x00000400u | 0x00000200u,
            0x00000080u | 0x00000020u | 0x00000200u,
        })
        {
            int validate = Ccd.SetDisplayConfigRaw(
                (uint)paths.Length, paths, (uint)modes.Length, modes,
                (flags & ~0x00000080u) | 0x00000040u);
            if (validate != 0) continue;

            return Ccd.SetDisplayConfigRaw(
                (uint)paths.Length, paths, (uint)modes.Length, modes, flags);
        }

        return -1;
    }

    private static void Rebuild()
    {
        var paths = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var modes);
        if (paths is null) return;
        Ccd.SetDisplayConfigRaw(
            (uint)paths.Length, paths, (uint)modes.Length, modes,
            0x00000080u | 0x00000020u | 0x00000400u);
    }

    private static void KillApp()
    {
        foreach (var p in Process.GetProcessesByName("RokidDiyStation"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
            p.Dispose();
        }
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

    private static bool Signal(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 5000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(command);
            writer.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (the signal failed: {ex.Message})");
            return false;
        }
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"
            + (detail is null ? "" : $"  ({detail})"));
        if (!ok) failures++;
    }
}
