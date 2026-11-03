// SPDX-License-Identifier: MIT
//
// THE FIX VERIFICATION PROBE.
//
// It exercises the SHIPPED code path - DisplayConfigController.Attach
// and DisplayConfigController.Detach - and checks the two things that
// matter:
//
//   1. the detach leaves the virtual display off the desktop, and it
//      STAYS off across a topology rebuild;
//   2. the database no longer records the virtual display as active,
//      so a rebuild cannot resurrect it.
//
// And then the reversibility: the attach brings it back, and the
// second detach takes it away again.
//
// The rebuild is provoked by SetDisplayConfig with SDC_APPLY and
// SDC_USE_SUPPLIED_DISPLAY_CONFIG - Windows re-evaluates the topology
// against the database, which is the same work a monitor hotplug
// does, and the moment the stale position used to come back.

using System.Diagnostics;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeVirtualStability;

internal static class Program
{
    private static int failures = 0;

    private static int Main()
    {
        Console.WriteLine("=== the state before anything ===");
        Ccd.Dump(Ccd.QDC_ONLY_ACTIVE_PATHS, "the ACTIVE set");
        Ccd.Dump(Ccd.QDC_DATABASE_CURRENT, "the DATABASE (current)");

        Console.WriteLine();
        Console.WriteLine("=== 1. attach ===");
        Check("the attach succeeds", DisplayConfigController.Attach(-1920, 0));
        Thread.Sleep(800);
        Check("the virtual display is active", DisplayConfigController.IsVirtualActive());

        Console.WriteLine();
        Console.WriteLine("=== 2. a topology rebuild, with the display attached ===");
        ForceTopologyRebuild();
        Thread.Sleep(1500);
        Check("the virtual display survives the rebuild",
            DisplayConfigController.IsVirtualActive());

        Console.WriteLine();
        Console.WriteLine("=== 3. detach ===");
        Check("the detach succeeds", DisplayConfigController.Detach());
        Thread.Sleep(800);
        Check("the virtual display is off the desktop",
            !DisplayConfigController.IsVirtualActive());
        Check("the database does not record it as active",
            CountDatabaseVirtualPaths() == 0,
            $"the database still has {CountDatabaseVirtualPaths()} virtual path(s)");

        Console.WriteLine();
        Console.WriteLine("=== 4. a topology rebuild, with the display detached ===");
        ForceTopologyRebuild();
        Thread.Sleep(1500);
        Check("the virtual display does NOT come back",
            !DisplayConfigController.IsVirtualActive());
        Check("the database still does not record it as active",
            CountDatabaseVirtualPaths() == 0,
            $"the database has {CountDatabaseVirtualPaths()} virtual path(s)");

        Console.WriteLine();
        Console.WriteLine("=== 5. the detach is idempotent ===");
        Check("a second detach succeeds", DisplayConfigController.Detach());
        Check("the virtual display is still off",
            !DisplayConfigController.IsVirtualActive());

        Console.WriteLine();
        Console.WriteLine("=== 6. it is reversible ===");
        Check("the attach brings it back", DisplayConfigController.Attach(-1920, 0));
        Thread.Sleep(800);
        Check("the virtual display is active again",
            DisplayConfigController.IsVirtualActive());
        Check("the second detach succeeds", DisplayConfigController.Detach());
        Thread.Sleep(800);
        Check("the virtual display is off again",
            !DisplayConfigController.IsVirtualActive());

        Console.WriteLine();
        Console.WriteLine("=== the final state ===");
        Ccd.Dump(Ccd.QDC_ONLY_ACTIVE_PATHS, "the ACTIVE set");
        Ccd.Dump(Ccd.QDC_DATABASE_CURRENT, "the DATABASE (current)");

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");

        return failures == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        if (!ok) failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"
            + (detail is null ? "" : $"  ({detail})"));
    }

    private static int CountDatabaseVirtualPaths()
    {
        var db = Ccd.Query(Ccd.QDC_DATABASE_CURRENT, out _);
        if (db is null) return -1;

        int n = 0;
        foreach (var p in db)
            if (Ccd.IsVirtual(p)) n++;
        return n;
    }

    /// <summary>
    /// Make Windows re-evaluate the topology against its database.
    /// </summary>
    private static void ForceTopologyRebuild()
    {
        var paths = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var modes);
        if (paths is null)
        {
            Console.WriteLine("    (the topology could not be queried)");
            return;
        }

        int rc = Ccd.SetDisplayConfigRaw(
            (uint)paths.Length, paths,
            (uint)modes.Length, modes,
            0x00000080 /* APPLY */ | 0x00000020 /* USE_SUPPLIED */
            | 0x00000400 /* ALLOW_CHANGES */);
        Console.WriteLine($"    the rebuild: SetDisplayConfig(0x4A0) = {rc}");
    }
}
