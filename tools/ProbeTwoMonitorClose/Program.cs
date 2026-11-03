// SPDX-License-Identifier: MIT
//
// THE TWO-PHYSICAL-MONITOR CLOSE PROBE.
//
// THE USER'S REPORT
// -----------------
//   "with one monitor, or with three, closing the spatial display
//    takes the virtual display off the desktop.  With TWO, it does
//    not."
//
// The topology on the machine right now is exactly the reported one:
// the laptop panel at (0,0) and the XP-Pen Artist at (1920,0), and
// the Rokid Max is NOT plugged in.
//
// WHAT THIS PROBE DOES
// --------------------
// It starts the REAL app - not the controller - and drives the
// "close the spatial display" action through the app's own named
// pipe, then asks WINDOWS what is on the desktop.
//
// The point is that the app's log claims success ("The virtual
// display went away."), and the CCD view agrees, and yet the user
// still sees the rectangle.  So the question this probe answers is
// the one the app never asks:
//
//   is the virtual monitor still in EnumDisplayMonitors?
//
// THE ANSWER IS THE SHELL'S
// --------------------------
// The shell is what draws the rectangle the user is complaining
// about, so the shell is the thing that has to be asked.  The
// monitors are read with NO attached-to-desktop filter, because a
// monitor that Windows has taken off the desktop still appears in
// EnumDisplayMonitors for a while, and reading it with the filter
// would hide exactly the failure this probe is looking for.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeTwoMonitorClose;

internal static class Program
{
    // The pipe the app listens on for commands.
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

    /// <summary>
    /// The executable under test.
    /// </summary>
    /// <remarks>
    /// It can be overridden with the first argument, so the SAME probe
    /// can be pointed at the folder build and at the shipped
    /// single-file build - and the single-file build is the one that
    /// goes to users, so it is the one that has to be proven.
    /// </remarks>
    private static readonly string Exe =
        Environment.GetCommandLineArgs().Length > 1
            ? Environment.GetCommandLineArgs()[1]
            : @"artifacts\release\RokidDiyStation.exe";

    private static int Main()
    {
        KillApp();
        Thread.Sleep(1000);

        Console.WriteLine($"=== the executable under test: {Exe} ===");
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

        Console.WriteLine();
        Console.WriteLine("=== the app is running ===");
        Thread.Sleep(9000);
        PrintMonitors();
        Check("the virtual display is on the desktop",
            ShellHasVirtualMonitor());

        Console.WriteLine();
        Console.WriteLine("=== asking the app to CLOSE THE SPATIAL DISPLAY ===");
        bool signalled = Signal("close-spatial");
        Console.WriteLine($"  the signal was delivered: {signalled}");

        // The app's own close has a settle window; give it room.
        Thread.Sleep(6000);
        PrintMonitors();

        Console.WriteLine();
        Console.WriteLine("=== after a topology rebuild ===");
        ForceTopologyRebuild();
        Thread.Sleep(2500);
        PrintMonitors();

        Check("the SHELL no longer lists the virtual monitor",
            !ShellHasVirtualMonitor(),
            "the rectangle is still on the user's desktop - this is the bug");

        Check("the CCD view agrees the virtual display is off",
            !DisplayConfigController.IsVirtualActive());

        // The close is only half of the contract.  A display that goes
        // away and can never come back is not a working product, so
        // the round trip has to be exercised too - and in the SAME
        // two-monitor topology, because that is where it broke.
        Console.WriteLine();
        Console.WriteLine("=== asking the app to OPEN THE SPATIAL DISPLAY AGAIN ===");
        bool openSignalled = Signal("open-spatial");
        Console.WriteLine($"  the signal was delivered: {openSignalled}");
        Thread.Sleep(6000);
        PrintMonitors();

        Check("the virtual display is back on the desktop",
            ShellHasVirtualMonitor(),
            "the display cannot be reopened - the close broke it");

        Console.WriteLine();
        Console.WriteLine("=== and CLOSING IT A SECOND TIME ===");
        Signal("close-spatial");
        Thread.Sleep(6000);
        PrintMonitors();

        Check("the SHELL no longer lists the virtual monitor after the second close",
            !ShellHasVirtualMonitor(),
            "the rectangle is still on the user's desktop - this is the bug");

        Console.WriteLine();
        Console.WriteLine("=== and once more, to be sure it is not a one-shot ===");
        Signal("open-spatial");
        Thread.Sleep(6000);
        Signal("close-spatial");
        Thread.Sleep(6000);
        PrintMonitors();

        Check("the virtual display survives three open/close cycles",
            !ShellHasVirtualMonitor(),
            "the rectangle is still on the user's desktop - this is the bug");

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");
        return failures == 0 ? 0 : 1;
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
            // The shell's device name carries no EDID, so the identity
            // has to be resolved through the adapter - the same way
            // VirtualDisplayLocator does it.
            bool virt = VirtualDisplayLocator.Enumerate()
                .Any(m => m.DeviceName == mi.szDevice && m.IsVirtual);
            Console.WriteLine($"      {mi.szDevice,-24} "
                + $"({mi.rcMonitor.Left},{mi.rcMonitor.Top})-({mi.rcMonitor.Right},{mi.rcMonitor.Bottom})"
                + (virt ? "   <== THE VIRTUAL DISPLAY" : ""));
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
        if (!any) Console.WriteLine("      (none)");
    }

    private static bool ShellHasVirtualMonitor()
        => VirtualDisplayLocator.Enumerate().Any(m => m.IsVirtual);

    private static void ForceTopologyRebuild()
    {
        var paths = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var modes);
        if (paths is null) return;
        Ccd.SetDisplayConfigRaw(
            (uint)paths.Length, paths,
            (uint)modes.Length, modes,
            0x00000080 /* APPLY */ | 0x00000020 /* USE_SUPPLIED */
            | 0x00000400 /* ALLOW_CHANGES */);
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
