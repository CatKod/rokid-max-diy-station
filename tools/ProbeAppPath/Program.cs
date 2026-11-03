// SPDX-License-Identifier: MIT
//
// THE APP-PATH DETACH PROBE.
//
// The CCD detach works perfectly on its own - ProbeTwoMonitor shows
// that the virtual monitor really does leave the desktop, and stays
// off across a topology rebuild.
//
// So the bug is NOT in the CCD layer.  It is in what the APP does
// around it.  This probe drives the app's own entry points -
// VirtualDisplayController.Open() and VirtualDisplayController.Close()
// - which is the code path the "Close the spatial display" menu item
// and the app's exit path both use.
//
// THE SCENARIO THAT FAILS
// -----------------------
// The user plugs the XP-Pen display in while the spatial display is
// open.  Windows re-lays-out the desktop, the virtual display's path
// is rebuilt, and the app's topology watcher reconciles.
//
// The watcher asks two questions:
//
//   bool shouldBeOpen = _virtualDisplayShouldBeOpen;
//   bool isOpen       = DisplayConfigController.IsVirtualActive();
//
// and acts on the DIFFERENCE.  So the watcher needs the CCD view and
// the Win32 view to agree about what "the virtual display is" - and
// they do not always.
//
// THE REAL SUSPECT
// ----------------
// VirtualDisplayLocator.ResolveIdentity SKIPS any monitor whose
// DISPLAY_DEVICE.StateFlags lacks DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
// (0x1).  A virtual display that has just been detached reports 0x0,
// so:
//
//   * ResolveIdentity returns ("", "");
//   * the EDID fingerprint cannot match;
//   * FindVirtual() returns null;
//   * so the app concludes "the virtual display is gone" and stops
//     the capture.
//
// That is the CORRECT conclusion for a display that is genuinely off.
// But it is the WRONG conclusion for a display whose adapter is
// briefly flagged as not-attached while it is still on the desktop -
// which is exactly the state Windows leaves the IddCx adapter in when
// it re-publishes the monitor after a topology change.
//
// The pump then logs "The virtual display went away", the capture
// stops, and the reconciler - which is looking at the CCD view and
// sees the path as still active - concludes the display "came back"
// and opens the spatial display again.
//
// This probe reproduces that by driving the app's own methods and
// watching all three views at once.

using System.Diagnostics;

using RokidDiyStation.App.Spatial;

namespace RokidDiyStation.Tools.ProbeAppPath;

internal static class Program
{
    private static int failures = 0;

    private static int Main()
    {
        Console.WriteLine("=== the state before anything ===");
        Report("before");

        Console.WriteLine();
        Console.WriteLine("=== 1. open, the way the app opens it ===");
        Check("the open succeeds", VirtualDisplayController.Open());
        Thread.Sleep(1500);
        Report("after the open");

        Console.WriteLine();
        Console.WriteLine("=== 2. close, the way the app closes it ===");
        Check("the close succeeds", VirtualDisplayController.Close());
        Thread.Sleep(1500);
        Report("after the close");

        Console.WriteLine();
        Console.WriteLine("=== 3. and it stays off ===");
        Thread.Sleep(2000);
        Report("2s after the close");

        Thread.Sleep(3000);
        Report("5s after the close");

        Console.WriteLine();
        Console.WriteLine("=== 4. open again - is it still reversible? ===");
        Check("the second open succeeds", VirtualDisplayController.Open());
        Thread.Sleep(1200);
        Report("after the second open");

        Console.WriteLine();
        Console.WriteLine("=== 5. close again ===");
        Check("the second close succeeds", VirtualDisplayController.Close());
        Thread.Sleep(1200);
        Report("after the second close");

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");

        return failures == 0 ? 0 : 1;
    }

    private static void Report(string when)
    {
        Console.WriteLine($"  --- {when} ---");
        bool ccd = DisplayConfigController.IsVirtualActive();
        bool locator = VirtualDisplayLocator.FindVirtual() is not null;
        bool adapter = VirtualDisplayLocator.FindVirtualAdapter() is not null;
        int shell = VirtualDisplayLocator.Enumerate().Count;

        Console.WriteLine($"      ccdActive={ccd,-5} locatorFound={locator,-5} "
            + $"virtualAdapterPresent={adapter,-5} shellMonitors={shell}");
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"
            + (detail is null ? "" : $"  ({detail})"));
        if (!ok) failures++;
    }
}
