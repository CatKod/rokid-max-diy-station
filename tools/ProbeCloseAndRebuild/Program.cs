// SPDX-License-Identifier: MIT
//
// THE CLOSE-AND-REBUILD PROBE.
//
// This is the probe for the user's ACTUAL complaint, and it drives
// the real shipped executable.
//
// THE SCENARIO
// ------------
//  1. the app starts, and it opens the spatial display;
//  2. the user closes the spatial display (the tray menu's "Close the
//     spatial display");
//  3. Windows rebuilds the display topology - which is what plugging
//     the XP-Pen display in does.
//
// The user reports that with TWO physical monitors the virtual display
// comes BACK at step 3, while with one and three it stays off.
//
// WHY THE CCD LAYER IS NOT ENOUGH
// -------------------------------
// The CCD detach works perfectly on its own - ProbeTwoMonitor and
// ProbeTopologyChange both show the virtual monitor really leaving
// the desktop and staying off across a rebuild.
//
// So the bug is in the APP's reaction to the rebuild: the topology
// watcher.
//
// The watcher reconciles on a DISAGREEMENT between two views:
//
//     bool shouldBeOpen = _virtualDisplayShouldBeOpen;   // the intent
//     bool isOpen       = DisplayConfigController.IsVirtualActive();  // the CCD view
//
// The intent is false after a close, so the watcher only acts if the
// CCD view says the display IS active.
//
// The bug: when the topology is rebuilt, the IddCx driver re-publishes
// its monitor, and the CCD view can report the virtual path as ACTIVE
// again for a moment - while the app has already closed the spatial
// display.  The watcher then sees "should be off, but it is on" and
// detaches it.  That is correct behaviour.
//
// But the OTHER order is possible, and it is the one the user hit:
//
//   * the topology change makes the app's CAPTURE lose the virtual
//     display (the pump logs "The virtual display went away"), because
//     VirtualDisplayLocator.ResolveIdentity skips a monitor whose
//     StateFlags lack DISPLAY_DEVICE_ATTACHED_TO_DESKTOP - and the
//     driver leaves the monitor in exactly that state while it
//     re-publishes;
//   * the watcher, meanwhile, sees the CCD view as still active, so
//     it does nothing;
//   * and then the app's own reconciler - or the next rebuild - puts
//     the display back.
//
// This probe watches the real app through all of it, and it also
// FORCES a topology rebuild after the close, because that is the part
// the user does with their hands.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeCloseAndRebuild;

internal static class Program
{
    private const string Exe = @"artifacts\release\RokidDiyStation.exe";
    private const string PipeName = "RokidDiyStation.ShowWindow";

    /// <summary>How long the display has to stay off, in ms.</summary>
    private const int RequiredOffMs = 2500;

    private static int failures = 0;

    private static int Main()
    {
        foreach (var p in Process.GetProcessesByName("RokidDiyStation"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
            p.Dispose();
        }
        Thread.Sleep(1000);

        Console.WriteLine("=== the state before the app starts ===");
        Report("before");

        var app = Process.Start(new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(Exe))!,
        });
        if (app is null) { Console.WriteLine("the app did not start"); return 1; }

        Console.WriteLine();
        Console.WriteLine("=== the app is running, and has opened the spatial display ===");
        Thread.Sleep(9000);
        Report("after the app started");

        Check("the spatial display is open",
            DisplayConfigController.IsVirtualActive(),
            "the virtual display is not active - the app did not open it");

        Console.WriteLine();
        Console.WriteLine("=== asking the app to CLOSE the spatial display ===");

        var timeline = new List<(int Ms, bool Active, int DbPaths)>();
        var watch = Stopwatch.StartNew();
        bool watching = true;

        var watcher = new Thread(() =>
        {
            while (watching)
            {
                lock (timeline)
                {
                    timeline.Add(((int)watch.ElapsedMilliseconds,
                        DisplayConfigController.IsVirtualActive(),
                        CountDatabaseVirtualPaths()));
                }
                Thread.Sleep(100);
            }
        }) { IsBackground = true };
        watcher.Start();

        bool signalled = Signal("close-spatial");
        Console.WriteLine($"  the signal was delivered: {signalled}");

        // Wait for the close to finish.
        Thread.Sleep(6000);
        Report("after the close");

        Console.WriteLine();
        Console.WriteLine("=== forcing a topology rebuild - what plugging a monitor in does ===");
        RebuildTopology();
        Thread.Sleep(2500);
        Report("after the rebuild");

        Thread.Sleep(3000);
        Report("5s after the rebuild");

        watching = false;
        watcher.Join(2000);

        Console.WriteLine();
        Console.WriteLine("=== the timeline ===");
        PrintTimeline(timeline);

        int longestOff = LongestOffRun(timeline);
        Console.WriteLine();
        Console.WriteLine($"  the longest run with the display OFF: {longestOff}ms");

        Check("the display went off", longestOff > 0);
        Check($"it stayed off for at least {RequiredOffMs}ms",
            longestOff >= RequiredOffMs, $"{longestOff}ms");

        var offSamples = timeline.Where(t => !t.Active).ToList();
        int worstDb = offSamples.Count == 0 ? -1 : offSamples.Max(t => t.DbPaths);
        Check("the database did not record it as active while it was off",
            worstDb == 0, $"the worst reading was {worstDb}");

        Console.WriteLine();
        Console.WriteLine("=== the final state ===");
        Check("the virtual display is off the desktop",
            !DisplayConfigController.IsVirtualActive());
        Check("the shell does NOT list a virtual monitor",
            !ShellHasVirtualMonitor(),
            "the shell still lists a virtual monitor - the user can still drag windows onto it");
        Check("the database does not record it as active",
            CountDatabaseVirtualPaths() == 0,
            $"the database still has {CountDatabaseVirtualPaths()} virtual path(s)");

        // And the app must still be alive and usable.
        Check("the app is still running",
            Process.GetProcessesByName("RokidDiyStation").Length > 0);

        // Clean up.
        Signal("quit");
        Thread.Sleep(4000);
        foreach (var p in Process.GetProcessesByName("RokidDiyStation"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");
        return failures == 0 ? 0 : 1;
    }

    private static void RebuildTopology()
    {
        try
        {
            var paths = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var modes);
            if (paths is null) return;
            Ccd.Apply(paths, modes);
        }
        catch { }
    }

    private static int LongestOffRun(List<(int Ms, bool Active, int DbPaths)> timeline)
    {
        int best = 0, runStart = -1;
        lock (timeline)
        {
            foreach (var t in timeline)
            {
                if (!t.Active)
                {
                    if (runStart < 0) runStart = t.Ms;
                    best = Math.Max(best, t.Ms - runStart);
                }
                else runStart = -1;
            }
        }
        return best;
    }

    private static void PrintTimeline(List<(int Ms, bool Active, int DbPaths)> timeline)
    {
        string last = "";
        lock (timeline)
        {
            foreach (var t in timeline)
            {
                string line = $"{t.Ms,6}ms  {(t.Active ? "ON " : "off")}  dbPaths={t.DbPaths}";
                if (line != last) { Console.WriteLine("    " + line); last = line; }
            }
        }
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

    private static void Report(string when)
    {
        Console.WriteLine($"  --- {when} ---");
        bool active = DisplayConfigController.IsVirtualActive();
        bool enumerated = VirtualDisplayLocator.FindVirtual() is not null;
        int db = CountDatabaseVirtualPaths();
        int shell = VirtualDisplayLocator.Enumerate().Count;

        // THE SHELL IS THE USER'S VIEW OF THE DESKTOP.
        //
        // The CCD view says whether the virtual display has an active
        // PATH.  The shell says whether there is a monitor rectangle
        // for the user to drag windows onto - which is what the
        // complaint is actually about.
        //
        // These can disagree, and the CCD view is the one that can be
        // wrong: a path can be gone from the configuration while the
        // shell is still holding a rectangle for it.
        int shellMonitors = ShellMonitorCount();
        bool shellHasVirtual = ShellHasVirtualMonitor();

        Console.WriteLine($"      ccdActive={active,-5} locatorFound={enumerated,-5} "
            + $"dbVirtualPaths={db,-3} shellMonitors={shell,-3} shellHasVirtual={shellHasVirtual}");
    }

    /// <summary>
    /// The number of monitors the shell lists.
    /// </summary>
    private static int ShellMonitorCount()
    {
        int n = 0;

        bool Collect(IntPtr h, IntPtr dc, ref RECT r, IntPtr data)
        {
            n++;
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
        return n;
    }

    /// <summary>
    /// Whether the shell lists a monitor whose ADAPTER is the virtual
    /// display's.
    /// </summary>
    /// <remarks>
    /// The decision is made on the adapter's device id ("Root\MttVDD"),
    /// not on the monitor's own attached flag - because a monitor that
    /// Windows has taken off the desktop reports no attached flag, and
    /// that is exactly the case this has to catch.
    /// </remarks>
    private static bool ShellHasVirtualMonitor()
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if (!(dd.DeviceID ?? "").Contains("MttVDD", StringComparison.OrdinalIgnoreCase)
                && !(dd.DeviceString ?? "").Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                continue;

            // The adapter is the virtual one.  Now: does the shell list
            // a monitor rectangle for it?
            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint j = 0; EnumDisplayDevices(dd.DeviceName, j, ref mon, 0); j++)
            {
                // The monitor is enumerated under the adapter.  The
                // SHELL's rectangle is what matters, so the monitor
                // has to be checked with EnumDisplayMonitors.
                var rect = new RECT();
                if (GetMonitorRectForDevice(mon.DeviceName ?? "", out rect))
                    return true;
            }
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? device, uint devNum, ref DISPLAY_DEVICE display, uint flags);

    private delegate bool MonitorEnumProc(IntPtr h, IntPtr dc, ref RECT r, IntPtr data);

    /// <summary>
    /// Whether the shell has a rectangle for a device name.
    /// </summary>
    private static bool GetMonitorRectForDevice(string deviceName, out RECT rect)
    {
        bool found = false;
        RECT foundRect = default;

        bool Collect(IntPtr h, IntPtr dc, ref RECT r, IntPtr data)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi))
            {
                // The monitor's device name is "\\.\DISPLAYn\Monitor0".
                // The adapter's monitor name is "\\.\DISPLAYn".
                if (mi.szDevice.StartsWith(deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    foundRect = mi.rcMonitor;
                    found = true;
                }
            }
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
        rect = foundRect;
        return found;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);

    private static int CountDatabaseVirtualPaths()
    {
        var db = Ccd.Query(Ccd.QDC_DATABASE_CURRENT, out _);
        if (db is null) return -1;
        int n = 0;
        foreach (var p in db) if (Ccd.IsVirtual(p)) n++;
        return n;
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"
            + (detail is null ? "" : $"  ({detail})"));
        if (!ok) failures++;
    }
}
