// SPDX-License-Identifier: MIT
//
// THE HOT-PLUG DETACH PROBE.
//
// THE QUESTION
// ------------
// The user reports that closing the spatial display works with one
// physical monitor and with three, but not with two.
//
// A plain close works on this machine right now - the earlier probe
// proved that.  So the difference must be the TRANSITION: the user
// was on one monitor, plugged in the XP-Pen, and THEN closed the
// spatial display.
//
// A hot-plug is the one thing that makes Windows rewrite the
// display-configuration DATABASE from a topology record that was
// saved when the machine was in a DIFFERENT layout.  That is how a
// stale position gets into the database - the record for the
// three-monitor topology puts the virtual display at (1915, -1080),
// which is above and partly over the XP-Pen.
//
// So this probe:
//   1. starts the app, so the spatial display is open;
//   2. forces a topology rebuild THAT SAVES TO THE DATABASE, which
//      is what a hot-plug does;
//   3. asks the app to close the spatial display;
//   4. asks WINDOWS whether the virtual monitor is still on the
//      desktop.
//
// The rebuild is the interesting part: it is the moment the database
// and the session can drift apart, and the close that follows is
// where the drift bites.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeHotPlugDetach;

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

        Console.WriteLine("=== the desktop before the app starts ===");
        PrintMonitors();

        var app = Process.Start(new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(Exe))!,
        });
        if (app is null)
        {
            Console.WriteLine("the app did not start");
            return 1;
        }

        Thread.Sleep(9000);
        Console.WriteLine();
        Console.WriteLine("=== the app is running, the spatial display is open ===");
        PrintMonitors();

        Console.WriteLine();
        Console.WriteLine("=== a hot-plug: a topology rebuild that SAVES TO THE DATABASE ===");
        RebuildAndSave();
        Thread.Sleep(3000);
        PrintMonitors();
        DumpDatabase();

        Console.WriteLine();
        Console.WriteLine("=== asking the app to CLOSE THE SPATIAL DISPLAY ===");
        Console.WriteLine($"  the signal was delivered: {Signal("close-spatial")}");
        Thread.Sleep(7000);
        PrintMonitors();

        Check("the SHELL no longer lists the virtual monitor",
            !ShellHasVirtualMonitor(),
            "the rectangle is still on the user's desktop - this is the bug");

        Console.WriteLine();
        Console.WriteLine("=== and after a further rebuild ===");
        RebuildAndSave();
        Thread.Sleep(2500);
        PrintMonitors();

        Check("the virtual display stays off after a rebuild",
            !ShellHasVirtualMonitor());

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// A topology rebuild that is PERSISTED, the way a hot-plug is.
    /// </summary>
    private static void RebuildAndSave()
    {
        var paths = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var modes);
        if (paths is null) return;
        int rc = Ccd.SetDisplayConfigRaw(
            (uint)paths.Length, paths,
            (uint)modes.Length, modes,
            0x00000080 /* APPLY */ | 0x00000020 /* USE_SUPPLIED */
            | 0x00000400 /* ALLOW_CHANGES */ | 0x00000800 /* SAVE_TO_DATABASE */);
        Console.WriteLine($"  the rebuild: SetDisplayConfig = {rc}");
    }

    private static void DumpDatabase()
    {
        var db = Ccd.Query(Ccd.QDC_DATABASE_CURRENT, out var modes);
        if (db is null) { Console.WriteLine("    the database query failed"); return; }
        Console.WriteLine("    the database's current topology:");
        for (int i = 0; i < db.Length; i++)
        {
            var p = db[i];
            if ((p.flags & 0x1) == 0) continue;
            string pos = "?";
            if (p.sourceInfo.modeInfoIdx != uint.MaxValue
                && p.sourceInfo.modeInfoIdx < modes.Length)
            {
                var sm = modes[p.sourceInfo.modeInfoIdx].sourceMode;
                pos = $"({sm.position.x},{sm.position.y}) {sm.Width}x{sm.Height}";
            }
            Console.WriteLine($"      [{i}] virtual={Ccd.IsVirtual(p),-5} pos={pos}");
        }
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
