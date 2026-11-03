// SPDX-License-Identifier: MIT
//
// THE CLOSE-RACE PROBE.
//
// THE RACE
// --------
// The user's report: with TWO physical monitors, closing the spatial
// display does NOT take the virtual display off the desktop.  With
// one and with three, it works.
//
// The difference is not the number of monitors.  It is whether a
// topology change happens at the same moment as the close - and
// plugging a monitor in is exactly that.
//
// The app has two actors that both manipulate the virtual display:
//
//   * CloseSpatialDisplay() - on the UI thread.  It clears the intent
//     flag, unhooks the watcher, and calls VirtualDisplayController.Close().
//
//   * OnTopologyChanged() - on a thread-pool thread.  It sleeps 600ms
//     to debounce, reads the intent flag, and then attaches or
//     detaches to match it.
//
// The close unhooks the watcher, so no NEW reconciliation starts.  But
// a reconciliation that ALREADY STARTED is not stopped - the unhook
// only prevents the next one.
//
// So the interleaving that breaks it is:
//
//   t=0ms     the user plugs the XP-Pen display in
//             -> WM_DISPLAYCHANGE -> the watcher is queued
//
//   t=10ms    the watcher starts: _reconciling = 1
//             -> Thread.Sleep(600)
//
//   t=50ms    the user closes the spatial display
//             -> the intent flag is cleared
//             -> the watcher is unhooked
//             -> Close() starts: it disconnects the virtual path
//
//   t=650ms   the watcher wakes up.  It reads the intent flag - which
//             is now false - so it wants the display OFF.  It detaches.
//             That is harmless.
//
// ...BUT THE OTHER ORDER IS NOT:
//
//   t=0ms     the user plugs the XP-Pen display in
//             -> the watcher starts: _reconciling = 1
//             -> Thread.Sleep(600)
//
//   t=100ms   the watcher wakes up.  It reads the intent flag: TRUE.
//             -> it decides the display SHOULD be open
//             -> it calls VirtualDisplayController.Open()
//
//   t=120ms   the user closes the spatial display
//             -> the intent flag is cleared
//             -> the watcher is unhooked
//             -> Close() starts
//
//   t=300ms   Close() finishes its disconnect and waits out the settle
//             window...
//
//   t=400ms   the watcher's Open() lands.  The virtual display is back
//             on the desktop, at the position the attach computed.
//
//   t=1500ms  Close()'s settle window expires.  It checks, sees the
//             display is on the desktop, and reports failure.
//
// The number of monitors only decides whether the user is likely to
// be plugging something in at that moment.
//
// WHAT THIS PROBE DOES
// --------------------
// It reproduces the interleaving directly: it starts a reconciliation
// and a close at the same time, and checks which one wins.
//
// The fix is to make the close WAIT for any reconciliation that is
// already running, and to make the reconciliation RE-READ the intent
// after its work - so a reconciliation that started before the close
// cannot re-open the display behind the close's back.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeCloseRace;

internal static class Program
{
    private const string Exe = @"artifacts\release\RokidDiyStation.exe";
    private const string PipeName = "RokidDiyStation.ShowWindow";

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
        Console.WriteLine("=== the app is running ===");
        Thread.Sleep(9000);
        Report("after the app started");

        Check("the spatial display is open",
            DisplayConfigController.IsVirtualActive());

        // THE RACE.
        //
        // A topology change is provoked at the SAME time as the close,
        // which is what plugging a monitor in does.  The close takes
        // over a second, and the reconciliation that the topology
        // change provokes wakes up 600ms into it.
        Console.WriteLine();
        Console.WriteLine("=== the close, racing a topology change ===");

        var timeline = new List<(int Ms, bool Active)>();
        var watch = Stopwatch.StartNew();
        bool watching = true;

        var watcher = new Thread(() =>
        {
            while (watching)
            {
                lock (timeline)
                {
                    timeline.Add(((int)watch.ElapsedMilliseconds,
                        DisplayConfigController.IsVirtualActive()));
                }
                Thread.Sleep(50);
            }
        }) { IsBackground = true };
        watcher.Start();

        // The topology change, and the close, together.
        //
        // THE TIMING IS THE POINT.
        //
        // The reconciliation sleeps 600ms before it reads the intent
        // flag.  So the close has to arrive AFTER that read - otherwise
        // the reconciliation sees "the display should be closed" and
        // does the right thing.
        //
        // The close is therefore signalled at 900ms: the
        // reconciliation has woken up (at ~720ms), read the flag
        // (still true), and is on its way to attaching the virtual
        // display - while the close is on its way to detaching it.
        var racer = new Thread(() =>
        {
            Thread.Sleep(50);
            RebuildTopology();
        }) { IsBackground = true };
        racer.Start();

        Thread.Sleep(900);
        bool signalled = Signal("close-spatial");
        Console.WriteLine($"  the close was signalled: {signalled}");

        Thread.Sleep(12000);
        watching = false;
        watcher.Join(2000);

        Console.WriteLine();
        Console.WriteLine("=== the timeline ===");
        PrintTimeline(timeline);

        Console.WriteLine();
        Console.WriteLine("=== the final state ===");
        Report("final");

        Check("the virtual display is OFF the desktop",
            !DisplayConfigController.IsVirtualActive(),
            "the virtual display is still on the desktop - the race lost");

        Check("the shell does NOT list a virtual monitor",
            !ShellHasVirtualMonitor(),
            "the shell still lists a virtual monitor");

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

    private static void PrintTimeline(List<(int Ms, bool Active)> timeline)
    {
        string last = "";
        lock (timeline)
        {
            foreach (var t in timeline)
            {
                string line = $"{t.Ms,6}ms  {(t.Active ? "ON " : "off")}";
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
        bool locator = VirtualDisplayLocator.FindVirtual() is not null;
        Console.WriteLine($"      ccdActive={active,-5} locatorFound={locator,-5} "
            + $"shellHasVirtual={ShellHasVirtualMonitor()}");
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? device, uint devNum, ref DISPLAY_DEVICE display, uint flags);

    private delegate bool MonitorEnumProc(IntPtr h, IntPtr dc, ref RECT r, IntPtr data);

    /// <summary>
    /// Whether the shell lists a monitor rectangle for the virtual
    /// display's adapter.
    /// </summary>
    private static bool ShellHasVirtualMonitor()
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if (!(dd.DeviceID ?? "").Contains("MttVDD", StringComparison.OrdinalIgnoreCase))
                continue;

            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint j = 0; EnumDisplayDevices(dd.DeviceName, j, ref mon, 0); j++)
            {
                if (HasShellRectangle(mon.DeviceName ?? ""))
                    return true;
            }
        }
        return false;
    }

    private static bool HasShellRectangle(string deviceName)
    {
        bool found = false;

        bool Collect(IntPtr h, IntPtr dc, ref RECT r, IntPtr data)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi)
                && mi.szDevice.StartsWith(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
            }
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
