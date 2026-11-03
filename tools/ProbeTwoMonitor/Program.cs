// SPDX-License-Identifier: MIT
//
// THE TWO-MONITOR DETACH PROBE.
//
// The user reports that the virtual display fails to switch off with
// TWO physical monitors, while one and three work.
//
// THE QUESTION
// ------------
// After the detach, is the virtual monitor still on the desktop?
//
// The app answers that question in two places, and they can disagree:
//
//   * the CCD view - DisplayConfigController.IsVirtualActive(), which
//     asks whether the virtual display has an ACTIVE path;
//   * the Win32 view - VirtualDisplayLocator.FindVirtual(), which asks
//     whether EnumDisplayMonitors lists a monitor with the virtual
//     EDID.
//
// THE HYPOTHESIS
// --------------
// The Win32 view is wrong in a way that HIDES the bug.
//
// VirtualDisplayLocator.ResolveIdentity SKIPS any monitor whose
// DISPLAY_DEVICE.StateFlags does not have DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
// (0x1).  A virtual display that Windows has taken off the desktop
// reports 0x0 for that flag - so the identity resolution returns
// ("", ""), the EDID fingerprint never matches, and FindVirtual()
// reports "there is no virtual display" EVEN THOUGH EnumDisplayMonitors
// is still listing the monitor.
//
// This probe therefore reads the monitors ITSELF, with no attached-flag
// filter, so it can see what is really on the desktop.

using System.Diagnostics;
using System.Runtime.InteropServices;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeTwoMonitor;

internal static class Program
{
    private static int failures = 0;

    private static int Main()
    {
        Console.WriteLine("=== the state before anything ===");
        Report("before");

        Console.WriteLine();
        Console.WriteLine("=== 1. attach ===");
        Check("the attach succeeds", DisplayConfigController.Attach(-1920, 0));
        Thread.Sleep(1200);
        Report("after the attach");

        Console.WriteLine();
        Console.WriteLine("=== 2. detach ===");
        Check("the detach succeeds", DisplayConfigController.Detach());
        Thread.Sleep(1500);
        Report("after the detach");

        Console.WriteLine();
        Console.WriteLine("=== 3. what is REALLY on the desktop ===");

        var monitors = RawMonitors();
        Console.WriteLine($"  {monitors.Count} monitor(s) on the desktop:");
        foreach (var m in monitors)
            Console.WriteLine($"    {m}");

        var rawVirtual = monitors.Any(m => m.IsVirtual);
        Console.WriteLine();
        Console.WriteLine($"  a virtual monitor is on the desktop: {rawVirtual}");
        Console.WriteLine($"  the CCD view says active:           {DisplayConfigController.IsVirtualActive()}");
        Console.WriteLine($"  the locator says present:            {VirtualDisplayLocator.FindVirtual() is not null}");

        Check("the virtual monitor is OFF the desktop", !rawVirtual,
            "the virtual monitor is still on the desktop after the detach");

        Check("the CCD view and the locator agree",
            DisplayConfigController.IsVirtualActive()
                == (VirtualDisplayLocator.FindVirtual() is not null),
            "the two views disagree - the locator's identity resolution is wrong");

        Console.WriteLine();
        Console.WriteLine("=== 4. does it come back after a rebuild? ===");
        ForceTopologyRebuild();
        Thread.Sleep(1500);
        Report("after a rebuild");

        var after = RawMonitors();
        foreach (var m in after)
            Console.WriteLine($"    {m}");
        Check("the virtual monitor is still OFF after a rebuild",
            !after.Any(m => m.IsVirtual));

        Console.WriteLine();
        Console.WriteLine("=== 5. is it reversible? ===");
        Check("the attach brings it back", DisplayConfigController.Attach(-1920, 0));
        Thread.Sleep(1000);
        Check("the virtual monitor is back on the desktop",
            RawMonitors().Any(m => m.IsVirtual));
        Check("the detach takes it away again", DisplayConfigController.Detach());
        Thread.Sleep(1200);
        Check("the virtual monitor is off again",
            !RawMonitors().Any(m => m.IsVirtual));

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");

        return failures == 0 ? 0 : 1;
    }

    // -----------------------------------------------------------------
    // The raw Win32 read - NO attached-flag filter.
    // -----------------------------------------------------------------

    private sealed record RawMonitor(
        string DeviceName, string DeviceId, string Description,
        int Left, int Top, int Right, int Bottom, bool IsVirtual);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? device, uint devNum, ref DISPLAY_DEVICE display, uint flags);

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

    private static List<RawMonitor> RawMonitors()
    {
        var result = new List<RawMonitor>();

        bool Collect(IntPtr h, IntPtr dc, ref RECT r, IntPtr data)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi))
            {
                var (id, desc) = ResolveIdentity(mi.szDevice);
                result.Add(new RawMonitor(
                    mi.szDevice, id, desc,
                    mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right, mi.rcMonitor.Bottom,
                    IsVirtual(id)));
            }
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// The monitor's identity - WITHOUT the attached-to-desktop filter.
    /// </summary>
    /// <remarks>
    /// This is deliberately different from
    /// VirtualDisplayLocator.ResolveIdentity, which skips monitors
    /// whose StateFlags lack 0x1.  That filter is the defect: it makes
    /// a detached virtual display unidentifiable, so the app cannot
    /// tell "the display is off the desktop" from "the display is
    /// there and I just cannot see it".
    /// </remarks>
    private static (string Id, string Description) ResolveIdentity(string monitorDeviceName)
    {
        var adapterPath = monitorDeviceName;
        var idx = monitorDeviceName.LastIndexOf("\\Monitor", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) adapterPath = monitorDeviceName[..idx];

        var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint j = 0; EnumDisplayDevices(adapterPath, j, ref mon, 0); j++)
        {
            // NO FLAG FILTER - every monitor under the adapter counts.
            return (mon.DeviceID ?? "", mon.DeviceString ?? "");
        }

        return ("", "");
    }

    private static bool IsVirtual(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        return deviceId.Contains("MTT1337", StringComparison.OrdinalIgnoreCase)
            || deviceId.Contains("MttVDD", StringComparison.OrdinalIgnoreCase)
            || deviceId.StartsWith("ROOT\\DISPLAY", StringComparison.OrdinalIgnoreCase);
    }

    private static void Report(string when)
    {
        Console.WriteLine($"  --- {when} ---");
        var raw = RawMonitors();
        Console.WriteLine($"      ccdActive={DisplayConfigController.IsVirtualActive(),-5} "
            + $"locatorFound={VirtualDisplayLocator.FindVirtual() is not null,-5} "
            + $"shellMonitors={raw.Count,-3} shellHasVirtual={raw.Any(m => m.IsVirtual)}");
    }

    private static void ForceTopologyRebuild()
    {
        try
        {
            var paths = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var modes);
            if (paths is null) return;
            Ccd.Apply(paths, modes);
        }
        catch { }
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"
            + (detail is null ? "" : $"  ({detail})"));
        if (!ok) failures++;
    }
}
