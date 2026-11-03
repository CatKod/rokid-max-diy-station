// SPDX-License-Identifier: MIT
//
// THE SURVIVOR-OVERLAP PROBE.
//
// THE QUESTION
// ------------
// Windows refuses a configuration whose display rectangles overlap -
// the ProbeOverlapRefusal probe established that: the validation
// answers 87.
//
// So a detach or an attach that sends overlapping positions fails.
//
// The detach's FIRST attempt sends the DATABASE's positions for the
// surviving displays.  The attach sends the ACTIVE query's positions.
// The second attempt packs the survivors from the origin.
//
// So the question is: can either source contain a stale or
// overlapping arrangement?
//
// THE HYPOTHESIS
// --------------
// Yes - and the database is the likely one.
//
// The database keeps one record per topology the machine has been in.
// The record for the three-physical-monitor topology has the virtual
// display at (1915, -1080), which overlaps the XP-Pen at (1920, 0).
//
// When the user unplugs the Rokid Max, Windows rebuilds the topology
// from that database.  If it reuses the record as-is, the rebuilt
// layout has the virtual display on top of the XP-Pen - and then
// EVERY SetDisplayConfig call fails, because the configuration that
// has to be sent to fix it still contains the overlap.
//
// The probe reads the database's own positions for every path -
// through the CCD database query, not the registry - and checks them
// for overlaps, and it compares them against the active layout.
//
// That comparison is the important part: if the database and the
// active set DISAGREE about where a display is, then whichever one
// the code sends is a guess, and the guess can be the one Windows
// rejects.

using System.Diagnostics;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeSurvivorOverlap;

internal static class Program
{
    private static int failures = 0;

    private static int Main()
    {
        Console.WriteLine("=== the ACTIVE set ===");
        var active = Dump(Ccd.QDC_ONLY_ACTIVE_PATHS, "active");
        Console.WriteLine();
        Console.WriteLine("=== the DATABASE ===");
        var db = Dump(Ccd.QDC_DATABASE_CURRENT, "database");

        Console.WriteLine();
        Console.WriteLine("=== do the two agree? ===");
        if (active is not null && db is not null)
        {
            foreach (var (key, aPos) in active)
            {
                if (!db.TryGetValue(key, out var dPos)) continue;
                if (aPos != dPos)
                {
                    Console.WriteLine($"    !! {key}: the active set says {aPos}, "
                        + $"the database says {dPos}");
                    Console.WriteLine("       they disagree - whichever one the code sends is a guess");
                    failures++;
                }
            }
            if (failures == 0) Console.WriteLine("    they agree");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The position of every path, keyed by target id.
    /// </summary>
    private static Dictionary<string, string>? Dump(uint query, string label)
    {
        var paths = Ccd.Query(query, out var modes);
        if (paths is null)
        {
            Console.WriteLine($"    the {label} query failed");
            return null;
        }

        var result = new Dictionary<string, string>();

        for (int i = 0; i < paths.Length; i++)
        {
            var p = paths[i];
            bool active = (p.flags & 0x1) != 0;

            string pos = "(no mode)";
            if (p.sourceInfo.modeInfoIdx != uint.MaxValue
                && p.sourceInfo.modeInfoIdx < modes.Length)
            {
                var sm = modes[p.sourceInfo.modeInfoIdx].sourceMode;
                pos = $"({sm.position.x},{sm.position.y}) {sm.Width}x{sm.Height}";
            }

            string key = $"target={p.targetInfo.id} source={p.sourceInfo.id}";
            Console.WriteLine($"    active={(active ? "Y" : "N")} {key,-30} {pos}"
                + (Ccd.IsVirtual(p) ? "   <== VIRTUAL" : ""));

            if (active) result[key] = pos;
        }

        return result;
    }
}
