// SPDX-License-Identifier: MIT
//
// THE POSITION-SOURCE PROBE.
//
// THE DETACH'S FIRST ATTEMPT
// ---------------------------
// The detach sends the SURVIVORS' positions - the physical displays
// that stay on the desktop - and it takes them from the DATABASE:
//
//     var database = QueryAny(QDC_DATABASE_CURRENT, QDC_ALL_PATHS);
//     var positionSource = database ?? active;
//
//     foreach (var path in paths)
//     {
//         if ((path.flags & PATH_ACTIVE) == 0) continue;
//         var (x, y) = SourcePositionOf(positionSource.Modes, path);
//         SetSourcePosition(modes, path, x, y);
//     }
//
// The lookup is by IDENTITY - the source mode whose adapter and id
// match the path's - and when there is NO SUCH MODE the lookup falls
// back to (0, 0).
//
// WHY THAT FALLBACK IS THE BUG
// ----------------------------
// (0, 0) is the laptop panel's own position.  So a survivor whose
// position cannot be looked up is sent to the ORIGIN, on top of the
// laptop - and Windows refuses overlapping rectangles with 87
// (ERROR_INVALID_PARAMETER).  The detach then silently does nothing,
// and the virtual display stays on the desktop.
//
// THE WHY IT ONLY HAPPENS WITH TWO MONITORS
// -----------------------------------------
// The database holds ONE RECORD PER TOPOLOGY the machine has ever
// been in.  Each record has its own paths and its own modes, and the
// records do not have to agree with one another.
//
// When the machine is in the two-physical-monitor topology - the
// laptop panel and the XP-Pen - the record Windows has persisted can
// be a DIFFERENT one: the record for the topology that also had the
// Rokid Max, say, or the record for the topology without the XP-Pen.
// The lookup then finds no source mode for one of the survivors, the
// fallback puts it at the origin, and the whole configuration is
// invalid.
//
// With one monitor, or with three, the database's current record
// happens to contain every survivor - so the lookup succeeds and the
// detach works.  That is exactly the asymmetry the user reported.
//
// WHAT THIS PROBE MEASURES
// ------------------------
// For every ACTIVE path, in the two-monitor state:
//
//   * the position the ACTIVE set has for it;
//   * the position the DATABASE has for it;
//   * whether the identity lookup finds a source mode at all.
//
// A path whose lookup FAILS is the bug: it will be sent to (0, 0).

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbePositionSource;

internal static class Program
{
    private static int failures = 0;

    private static int Main()
    {
        Console.WriteLine("=== the SHELL's view ===");
        foreach (var m in VirtualDisplayLocator.Enumerate())
            Console.WriteLine($"    {m.DeviceName,-16} {m.Width}x{m.Height} at "
                + $"({m.Bounds.Left},{m.Bounds.Top})  virtual={m.IsVirtual}");

        Console.WriteLine();
        Console.WriteLine("=== the ACTIVE set ===");
        var active = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var activeModes);
        if (active is null) { Console.WriteLine("    the query failed"); return 1; }

        Console.WriteLine();
        Console.WriteLine("=== the DATABASE ===");
        var db = Ccd.Query(Ccd.QDC_DATABASE_CURRENT, out var dbModes);
        if (db is null) { Console.WriteLine("    the query failed"); return 1; }

        Console.WriteLine();
        Console.WriteLine("=== for every active path: what would the DETACH send? ===");

        foreach (var p in active)
        {
            if ((p.flags & 0x1) == 0) continue;
            if (Ccd.IsVirtual(p)) continue;   // the virtual path is disconnected

            bool found = TryFindSourceMode(dbModes, p, out var dbPos);
            var livePos = TryFindSourceMode(activeModes, p, out var live) ? live : "(none)";

            Console.WriteLine($"    target={p.targetInfo.id} source={p.sourceInfo.id} "
                + $"(adapter {p.sourceInfo.adapterId})");
            Console.WriteLine($"        the live set says   {livePos}");
            Console.WriteLine($"        the database says   {(found ? dbPos : "NOT FOUND - the lookup would fall back to (0,0)")}");

            if (!found)
            {
                Console.WriteLine("        !! the identity lookup fails, so the detach would send (0,0) "
                    + "- on top of the laptop panel");
                failures++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== EVERY SURVIVOR'S POSITION CAN BE LOOKED UP ==="
            : $"=== {failures} SURVIVOR(S) WOULD BE SENT TO THE ORIGIN ===");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The position of a path's source mode, matched BY IDENTITY.
    /// </summary>
    /// <remarks>
    /// This mirrors the production code's SourcePositionOf exactly:
    /// the adapter and the id must both match.  The difference is
    /// that this one reports whether it FOUND anything, where the
    /// production one silently answers (0, 0).
    /// </remarks>
    private static bool TryFindSourceMode(
        Ccd.ModeInfo[] modes, Ccd.Path path, out string position)
    {
        for (int m = 0; m < modes.Length; m++)
        {
            var mode = modes[m];
            if (mode.infoType != 1) continue;
            if (mode.adapterId.LowPart != path.sourceInfo.adapterId.LowPart) continue;
            if (mode.adapterId.HighPart != path.sourceInfo.adapterId.HighPart) continue;
            if (mode.id != path.sourceInfo.id) continue;

            position = $"({mode.sourceMode.position.x},{mode.sourceMode.position.y}) "
                + $"{mode.sourceMode.Width}x{mode.sourceMode.Height}";
            return true;
        }

        position = "(none)";
        return false;
    }
}
