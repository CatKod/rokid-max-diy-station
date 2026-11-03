// SPDX-License-Identifier: MIT
//
// THE DATABASE-PERSISTENCE PROBE.
//
// THE FINDING FROM THE APP'S OWN LOG
// ----------------------------------
// The diagnostic log never contains
//
//   "Detach: the first attempt was refused; retrying with the
//    survivors packed from the origin."
//
// so the FIRST attempt always succeeds.  The detach is never the
// thing that fails.
//
// What DOES happen, repeatedly, is
//
//   "Detach: no active virtual display path was found in the topology."
//   "Detach: the virtual display is already off the desktop."
//
// which means somebody asked for a detach when the virtual display
// was ALREADY off the desktop - the reconciler, or the replacement
// instance after a reload.
//
// And yet the user sees the virtual display on their desktop.
//
// THE HYPOTHESIS
// --------------
// The detach succeeds in the SESSION, but the DATABASE still records
// the virtual display as active.  Windows rebuilds the topology from
// the database - which is exactly what happens when a monitor is
// plugged or unplugged - and the virtual display comes back.
//
// That would make the symptom exactly what the user describes: it
// works with one monitor, it works with three, and with two it comes
// back, because two is the topology whose database record is stale.
//
// THE QUESTION
// ------------
// After a successful detach, what does the DATABASE say about the
// virtual display, and does a topology rebuild bring it back?

using System.Runtime.InteropServices;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeDatabasePersistence;

internal static class Program
{
    private static int failures = 0;

    private static int Main()
    {
        Console.WriteLine("=== the state before anything ===");
        Report("before");

        Console.WriteLine();
        Console.WriteLine("=== attach ===");
        Check("the attach succeeds", DisplayConfigController.Attach(-1920, 0));
        Thread.Sleep(1500);
        Report("after the attach");

        Console.WriteLine();
        Console.WriteLine("=== detach ===");
        Check("the detach succeeds", DisplayConfigController.Detach());
        Thread.Sleep(1500);
        Report("after the detach");

        Console.WriteLine();
        Console.WriteLine("=== what the DATABASE says ===");
        int dbVirtual = CountDatabaseVirtualPaths();
        int dbActive = CountDatabaseVirtualPaths(activeOnly: true);
        Console.WriteLine($"    the database has {dbVirtual} virtual path(s), "
            + $"{dbActive} of them active");

        Check("the database does NOT record the virtual display as active",
            dbActive == 0,
            $"the database still records {dbActive} active virtual path(s) - "
            + "Windows will put it back on the next topology rebuild");

        Console.WriteLine();
        Console.WriteLine("=== and after a topology rebuild ===");
        RebuildTopology();
        Thread.Sleep(2000);
        Report("after the rebuild");

        Check("the virtual display is still off after the rebuild",
            !DisplayConfigController.IsVirtualActive(),
            "the virtual display came back - the database brought it back");

        Check("the shell does not list a virtual monitor",
            !ShellHasVirtualMonitor());

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

    private static int CountDatabaseVirtualPaths(bool activeOnly = false)
    {
        var db = Ccd.Query(Ccd.QDC_DATABASE_CURRENT, out _);
        if (db is null) return -1;
        int n = 0;
        foreach (var p in db)
        {
            if (!Ccd.IsVirtual(p)) continue;
            if (activeOnly && (p.flags & 0x1) == 0) continue;
            n++;
        }
        return n;
    }

    private static bool ShellHasVirtualMonitor()
    {
        foreach (var m in VirtualDisplayLocator.Enumerate())
        {
            if (VirtualDisplayLocator.IsVirtualMonitorId(m.DeviceId)) return true;
        }
        return false;
    }

    private static void Report(string when)
    {
        Console.WriteLine($"  --- {when} ---");
        Console.WriteLine($"      ccdActive={DisplayConfigController.IsVirtualActive(),-5} "
            + $"locatorFound={VirtualDisplayLocator.FindVirtual() is not null,-5} "
            + $"dbVirtualPaths={CountDatabaseVirtualPaths(),-3} "
            + $"dbActiveVirtual={CountDatabaseVirtualPaths(true),-3} "
            + $"shellHasVirtual={ShellHasVirtualMonitor()}");
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"
            + (detail is null ? "" : $"  ({detail})"));
        if (!ok) failures++;
    }
}
