// SPDX-License-Identifier: MIT
//
// THE TOPOLOGY-CHANGE PROBE.
//
// The user reports that the virtual display fails to switch off with
// TWO physical monitors, while one and three work.  The XP-Pen
// display was plugged in so that the two-monitor case could be
// tested.
//
// WHAT THE LOG SHOWS
// ------------------
// The app's own diagnostic log, at the moment the XP-Pen was plugged
// in:
//
//   [21:32:23.067] The display layout changed and the spatial display
//                   went with it - putting it back.
//   [21:32:23.165] The virtual display went away.
//   [21:32:23.720] Display config: SetDisplayConfig with 4 path(s):
//     validate[0x26A0] = 87
//     validate[0x6A0] = 0
//     apply[0x6A0] = 0
//   [21:32:23.727] The spatial display is back.
//
// And when the spatial display was closed:
//
//   [21:33:54.645] The virtual display went away.
//   [21:33:54.683] Display config: SetDisplayConfig with 4 path(s):
//     ...
//     apply[0x6A0] = 0
//   [21:33:56.218] Spatial display closed.
//
// THE TWO OBSERVATIONS
// --------------------
// 1. The attach at 21:32:23 sent FOUR paths for a THREE-monitor
//    desktop.  One path too many.
//
// 2. The detach at 21:33:54 ALSO sent four paths - and it is the
//    same count as the attach, which means the detach and the attach
//    are building their path array the same way.
//
// WHERE THE EXTRA PATH COMES FROM
// -------------------------------
// The attach takes the paths from the ACTIVE query and drops every
// virtual one, then adds its own virtual path back:
//
//     foreach (var p in active.Paths)
//     {
//         if ((p.flags & PATH_ACTIVE) == 0) continue;
//         if (IsVirtualTarget(...)) continue;
//         paths.Add(p);
//     }
//     ...
//     paths.Add(newPath);
//
// The detach does the same, except it KEEPS the virtual path with
// its flags cleared:
//
//     if (IsVirtualTarget(...))
//     {
//         disconnected.flags &= ~PATH_ACTIVE;
//         ...
//         paths.Add(disconnected);      // <-- KEPT
//         continue;
//     }
//     paths.Add(p);
//
// So the detach sends: every active path, PLUS the disconnected
// virtual path.  With two physical monitors and one virtual one that
// is THREE paths, not four.
//
// Four means there was a SECOND virtual path in the topology - and
// the IddCx driver is exactly the thing that creates one, because it
// re-publishes its monitor when the topology changes and Windows can
// keep the old path alongside the new one.
//
// THAT is the bug the user's symptom is made of:
//
//   * the detach disconnects the FIRST virtual path;
//   * the SECOND virtual path stays active;
//   * the display stays on the desktop;
//   * and the detach reports success, because it only looks at the
//     path it disconnected.
//
// This probe MEASURES the path count at every step, so the extra
// path can be seen rather than inferred.

using System.Diagnostics;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeTopologyChange;

internal static class Program
{
    private static int failures = 0;

    private static int Main()
    {
        Console.WriteLine("=== the state before anything ===");
        Dump("before");

        Console.WriteLine();
        Console.WriteLine("=== 1. attach ===");
        DisplayConfigController.Attach(-1920, 0);
        Thread.Sleep(1200);
        Dump("after the attach");

        Console.WriteLine();
        Console.WriteLine("=== 2. a topology rebuild - what plugging a monitor in does ===");
        RebuildTopology();
        Thread.Sleep(1500);
        Dump("after the rebuild");

        Console.WriteLine();
        Console.WriteLine("=== 3. and another one ===");
        RebuildTopology();
        Thread.Sleep(1500);
        Dump("after the second rebuild");

        Console.WriteLine();
        Console.WriteLine("=== 4. detach ===");
        DisplayConfigController.Detach();
        Thread.Sleep(1500);
        Dump("after the detach");

        Console.WriteLine();
        Console.WriteLine("=== 5. a rebuild after the detach ===");
        RebuildTopology();
        Thread.Sleep(1500);
        Dump("after the rebuild following the detach");

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");
        return failures == 0 ? 0 : 1;
    }

    private static void Dump(string when)
    {
        Console.WriteLine($"  --- {when} ---");

        var active = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var modes);
        if (active is null) { Console.WriteLine("      the query failed"); return; }

        int virtualActive = 0;
        int virtualTotal = 0;
        var lines = new List<string>();

        for (int i = 0; i < active.Length; i++)
        {
            var p = active[i];
            bool isVirtual = Ccd.IsVirtual(p);
            bool isActive = (p.flags & 0x1) != 0;
            if (isVirtual) { virtualTotal++; if (isActive) virtualActive++; }

            string pos = "";
            if (p.sourceInfo.modeInfoIdx != uint.MaxValue
                && p.sourceInfo.modeInfoIdx < modes.Length)
            {
                var m = modes[p.sourceInfo.modeInfoIdx];
                if (m.infoType == 1)
                    pos = $" ({m.sourceMode.position.x},{m.sourceMode.position.y})";
            }

            lines.Add($"      [{i}] active={(isActive ? "Y" : "N")} "
                + $"target={p.targetInfo.id} source={p.sourceInfo.id}{pos}"
                + (isVirtual ? "   <== THE VIRTUAL DISPLAY" : ""));
        }

        foreach (var l in lines) Console.WriteLine(l);

        Console.WriteLine($"      total paths={active.Length}  "
            + $"virtual paths={virtualTotal}  of them active={virtualActive}");

        Check("there is at most ONE virtual path in the topology",
            virtualTotal <= 1,
            $"the topology has {virtualTotal} virtual paths - the IddCx driver left a duplicate");

        Check("the virtual path count matches the active count",
            virtualActive == virtualTotal,
            $"{virtualActive} of {virtualTotal} virtual paths are active");
    }

    /// <summary>
    /// Force Windows to rebuild the display topology from the database.
    /// </summary>
    /// <remarks>
    /// This is the same work a monitor hotplug does, and it is what
    /// makes the IddCx driver re-publish its monitor.
    /// </remarks>
    private static void RebuildTopology()
    {
        var paths = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var modes);
        if (paths is null) return;
        Ccd.Apply(paths, modes);
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"
            + (detail is null ? "" : $"  ({detail})"));
        if (!ok) failures++;
    }
}
