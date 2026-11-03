// SPDX-License-Identifier: MIT
//
// THE OVERLAP PROBE.
//
// WHAT THE REGISTRY SHOWS
// -----------------------
// The display-configuration database on this machine, for the
// topology with TWO physical monitors plus the virtual display:
//
//   00 : (1920, 0)      the XP-Pen artist display
//   01 : (0, 0)         the laptop panel
//   02 : (1915, -1080)   THE VIRTUAL DISPLAY  <-- stale, and it OVERLAPS
//   03 : (3840, 0)      the Rokid Max
//
// (1915, -1080) means the virtual display occupies x = 1915..3835,
// y = -1080..0.  The XP-Pen occupies x = 1920..3840, y = 0..1080.
// The two overlap in x over 1915..3835.
//
// The user's symptom - the virtual display refusing to go away with
// two monitors - is the visible consequence of that overlap: Windows
// refuses any configuration with overlapping rectangles, so the
// detach's first attempt is rejected, and the layout it falls back
// to may be rejected too.
//
// THE QUESTION
// ------------
// Where does the stale position come from, and does it stop the
// detach?
//
// THE HYPOTHESIS
// --------------
// The attach position is COMPUTED - "immediately to the left of the
// leftmost display".  With two monitors the leftmost is at (0,0),
// so the computed position is (-1920, 0), and the virtual display
// would occupy x = -1920..0.  That does NOT overlap.
//
// So the stale (1915, -1080) did not come from the computed attach.
// It came from somewhere else - and the most likely place is the
// DATABASE's own record of the virtual path, which the attach
// reuses:
//
//     if (vSourceIdx >= 0 && vTargetIdx >= 0 && ...)
//     {
//         var sourceMode = database.Modes[vSourceIdx];
//         sourceMode.sourceMode.position = new POINTL { x = x, y = y };
//         ...
//     }
//
// The position IS overridden.  But the SIZE comes from the database,
// and if the database's virtual source mode is not 1920x1080 the
// virtual display ends up with the wrong size - which can make it
// overlap a neighbour that was placed assuming 1920x1080.
//
// This probe measures all of it: the computed attach position, the
// size the virtual display actually gets, and whether the resulting
// rectangle overlaps anything.

using System.Diagnostics;
using System.Runtime.InteropServices;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeOverlap;

internal static class Program
{
    private static int failures = 0;

    private static int Main()
    {
        Console.WriteLine("=== the physical layout ===");
        foreach (var m in VirtualDisplayLocator.Enumerate())
            Console.WriteLine($"    {m.DeviceName,-16} {m.Width}x{m.Height} at "
                + $"({m.Bounds.Left},{m.Bounds.Top})  virtual={m.IsVirtual}");

        Console.WriteLine();
        Console.WriteLine("=== where the app WOULD put the virtual display ===");
        var (x, y) = VirtualDisplayController.ComputeAttachPosition();
        Console.WriteLine($"    the computed position is ({x},{y})");
        Console.WriteLine($"    so the virtual display would occupy "
            + $"x = {x}..{x + VirtualDisplayController.Width}, "
            + $"y = {y}..{y + VirtualDisplayController.Height}");

        // Does that overlap anything?
        foreach (var m in VirtualDisplayLocator.Enumerate())
        {
            if (m.IsVirtual) continue;
            bool overlap = x < m.Bounds.Right && x + VirtualDisplayController.Width > m.Bounds.Left
                        && y < m.Bounds.Bottom && y + VirtualDisplayController.Height > m.Bounds.Top;
            if (overlap)
                Console.WriteLine($"    !! it OVERLAPS {m.DeviceName} at "
                    + $"({m.Bounds.Left},{m.Bounds.Top}) {m.Width}x{m.Height}");
        }

        Console.WriteLine();
        Console.WriteLine("=== what the DATABASE records for the virtual path ===");
        DumpDatabaseVirtualPath();

        Console.WriteLine();
        Console.WriteLine("=== attach, and measure what we actually get ===");
        if (!DisplayConfigController.Attach(x, y))
        {
            Console.WriteLine("  the attach FAILED");
            failures++;
        }
        else
        {
            DumpAfterAttach(x, y);
        }

        Console.WriteLine();
        Console.WriteLine("=== detach ===");
        Check("the detach succeeds", DisplayConfigController.Detach());
        Thread.Sleep(1500);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");
        return failures == 0 ? 0 : 1;
    }

    private static void DumpDatabaseVirtualPath()
    {
        var db = Ccd.Query(Ccd.QDC_DATABASE_CURRENT, out var modes);
        if (db is null) { Console.WriteLine("    the query failed"); return; }

        for (int i = 0; i < db.Length; i++)
        {
            var p = db[i];
            if (!Ccd.IsVirtual(p)) continue;

            Console.WriteLine($"    the virtual path: active={((p.flags & 0x1) != 0)} "
                + $"sourceModeIdx={p.sourceInfo.modeInfoIdx} targetModeIdx={p.targetInfo.modeInfoIdx}");

            if (p.sourceInfo.modeInfoIdx != uint.MaxValue
                && p.sourceInfo.modeInfoIdx < modes.Length)
            {
                var sm = modes[p.sourceInfo.modeInfoIdx].sourceMode;
                Console.WriteLine($"    its recorded source mode: {sm.Width}x{sm.Height} "
                    + $"at ({sm.position.x},{sm.position.y})");
            }
        }
    }

    private static void DumpAfterAttach(int wantX, int wantY)
    {
        foreach (var m in VirtualDisplayLocator.Enumerate())
        {
            if (!m.IsVirtual) continue;
            Console.WriteLine($"    the virtual display came up at "
                + $"({m.Bounds.Left},{m.Bounds.Top}) {m.Width}x{m.Height}");

            Check("it came up at the position that was asked for",
                m.Bounds.Left == wantX && m.Bounds.Top == wantY,
                $"asked for ({wantX},{wantY}) and got ({m.Bounds.Left},{m.Bounds.Top})");

            Check("it is 1920x1080", m.Width == 1920 && m.Height == 1080,
                $"it is {m.Width}x{m.Height}");

            // Does it overlap a physical display?
            foreach (var other in VirtualDisplayLocator.Enumerate())
            {
                if (other.IsVirtual) continue;
                bool overlap = m.Bounds.Left < other.Bounds.Right
                            && m.Bounds.Right > other.Bounds.Left
                            && m.Bounds.Top < other.Bounds.Bottom
                            && m.Bounds.Bottom > other.Bounds.Top;
                if (overlap)
                {
                    Console.WriteLine($"    !! it OVERLAPS {other.DeviceName} at "
                        + $"({other.Bounds.Left},{other.Bounds.Top})");
                    Check("it does not overlap any physical display", false,
                        "the virtual display overlaps a physical display");
                }
            }
        }
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"
            + (detail is null ? "" : $"  ({detail})"));
        if (!ok) failures++;
    }
}
