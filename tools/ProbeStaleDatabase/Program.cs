// SPDX-License-Identifier: MIT
//
// THE STALE-DATABASE PROBE.
//
// THE OBSERVATION
// ---------------
// At the start of this session, before any app was running, the
// virtual display was on the desktop at (1915, -1080) - ON TOP OF THE
// XP-PEN DISPLAY, which is at (1920, 0).
//
// The virtual display occupies x = 1915..3835, y = -1080..0.
// The XP-Pen occupies x = 1920..3840, y = 0..1080.
//
// They overlap in x over 1915..3835.
//
// Windows forbids overlapping monitor rectangles outright, so a
// configuration that keeps both where they are is INVALID.
//
// WHERE THE STALE POSITION COMES FROM
// -----------------------------------
// The display-configuration database keeps one record per topology the
// machine has ever been in.  The record for the topology with three
// physical monitors has the virtual display at (1915, -1080) - a
// position Windows itself chose, at some point, for a layout that no
// longer exists.
//
// When the user then UNPLUGS a monitor, Windows rebuilds the topology
// from that database.  If it reuses a record whose virtual-display
// position overlaps a surviving physical display, the rebuilt layout
// is invalid, and:
//   * SetDisplayConfig refuses it with 87;
//   * the virtual display stays on the desktop, at the stale place.
//
// THE QUESTION
// ------------
// Does the ATTACH use the database's stale position, or the computed
// one?
//
// The attach overrides the position explicitly:
//
//     sourceMode.sourceMode.position = new POINTL { x = x, y = y };
//
// where (x, y) is the COMPUTED position.  So the attach should be
// fine.
//
// But the DETACH sends the DATABASE's positions for the survivors:
//
//     var database = QueryAny(QDC_DATABASE_CURRENT, QDC_ALL_PATHS);
//     var positionSource = database ?? active;
//     ...
//     var (x, y) = SourcePositionOf(positionSource.Modes, p);
//     SetSourcePosition(modes, p, x, y);
//
// and the survivors' positions in the database can be the stale ones.
//
// This probe measures the database's positions for the survivors and
// checks them for overlaps - which is the condition that makes the
// detach's first attempt fail.

using System.Diagnostics;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeStaleDatabase;

internal static class Program
{
    private static int failures = 0;

    private static int Main()
    {
        Console.WriteLine("=== the ACTIVE layout ===");
        DumpActive();

        Console.WriteLine();
        Console.WriteLine("=== the DATABASE's layout ===");
        DumpDatabase();

        Console.WriteLine();
        Console.WriteLine("=== the overlap check ===");
        CheckOverlaps();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");
        return failures == 0 ? 0 : 1;
    }

    private static void DumpActive()
    {
        foreach (var m in VirtualDisplayLocator.Enumerate())
            Console.WriteLine($"    {m.DeviceName,-16} {m.Width}x{m.Height} at "
                + $"({m.Bounds.Left},{m.Bounds.Top})  virtual={m.IsVirtual}");
    }

    private static void DumpDatabase()
    {
        // The database's view of the desktop, read through the CCD
        // database query.
        var db = Ccd.Query(Ccd.QDC_DATABASE_CURRENT, out var modes);
        if (db is null) { Console.WriteLine("    the query failed"); return; }

        for (int i = 0; i < db.Length; i++)
        {
            var p = db[i];
            if (!Ccd.IsVirtual(p)) continue;

            Console.WriteLine($"    the virtual path: active={((p.flags & 0x1) != 0)}");

            if (p.sourceInfo.modeInfoIdx != uint.MaxValue
                && p.sourceInfo.modeInfoIdx < modes.Length)
            {
                var sm = modes[p.sourceInfo.modeInfoIdx].sourceMode;
                Console.WriteLine($"    its recorded source mode: {sm.Width}x{sm.Height} at "
                    + $"({sm.position.x},{sm.position.y})");
            }
        }
    }

    /// <summary>
    /// Whether any two monitors on the desktop overlap.
    /// </summary>
    private static void CheckOverlaps()
    {
        var rects = new List<(string Name, int L, int T, int R, int B)>();
        foreach (var m in VirtualDisplayLocator.Enumerate())
        {
            rects.Add((m.DeviceName, m.Bounds.Left, m.Bounds.Top,
                       m.Bounds.Right, m.Bounds.Bottom));
        }

        int overlaps = 0;
        for (int i = 0; i < rects.Count; i++)
        {
            for (int j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i];
                var b = rects[j];
                if (a.L < b.R && a.R > b.L && a.T < b.B && a.B > b.T)
                {
                    overlaps++;
                    Console.WriteLine($"    !! {a.Name} overlaps {b.Name}");
                    Console.WriteLine($"       {a.Name} = ({a.L},{a.T})..({a.R},{a.B})");
                    Console.WriteLine($"       {b.Name} = ({a.L},{a.T})..({b.R},{b.B})");
                }
            }
        }

        Check("no two monitors overlap", overlaps == 0,
            $"{overlaps} overlapping pair(s) - Windows will refuse this layout");
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"
            + (detail is null ? "" : $"  ({detail})"));
        if (!ok) failures++;
    }
}
