// SPDX-License-Identifier: MIT
//
// THE OVERLAP-REFUSAL PROBE.
//
// THE HYPOTHESIS
// --------------
// The user's symptom - the virtual display refusing to go away with
// two physical monitors - is caused by an OVERLAPPING rectangle.
//
// The display-configuration database records the virtual display at
// (1915, -1080) in the record for the topology that had three
// physical monitors.  The XP-Pen artist display sits at (1920, 0).
// The virtual display's x range 1915..3835 therefore overlaps the
// XP-Pen's 1920..3840.
//
// Windows forbids overlapping monitor rectangles outright.  So:
//
//   * the ATTACH, which sends the survivors' positions unchanged and
//     adds the virtual display at a computed position, is accepted
//     only if the SURVIVORS do not overlap each other;
//   * the DETACH, which sends the database's positions for the
//     survivors, is accepted only if those positions do not overlap.
//
// This probe tests the premise DIRECTLY: it builds a configuration
// with two overlapping displays and asks Windows to VALIDATE it.
//
// Only SDC_VALIDATE is used - nothing is applied - so the desktop is
// never disturbed by the test.
//
// THE VERDICT
// -----------
// If Windows answers 87 for the overlapping configuration and 0 for
// a non-overlapping one, the hypothesis holds and the fix is to make
// the attach and the detach both guarantee a non-overlapping layout.
//
// If Windows answers 0 for the overlapping configuration, the
// hypothesis is wrong and the cause is elsewhere.

using System.Diagnostics;
using System.Runtime.InteropServices;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeOverlapRefusal;

internal static class Program
{
    private static int failures = 0;

    private static int Main()
    {
        Console.WriteLine("=== the current layout ===");
        foreach (var m in VirtualDisplayLocator.Enumerate())
            Console.WriteLine($"    {m.DeviceName,-16} {m.Width}x{m.Height} at "
                + $"({m.Bounds.Left},{m.Bounds.Top})  virtual={m.IsVirtual}");

        Console.WriteLine();
        Console.WriteLine("=== does Windows refuse an overlapping layout? ===");

        // Build the configuration the DETACH would send, but with one
        // survivor deliberately moved on top of another.
        var verdict = TestOverlap();

        Console.WriteLine();
        Console.WriteLine($"    Windows refuses overlapping rectangles: {verdict}");

        Check("Windows refuses an overlapping layout",
            verdict,
            "Windows accepted an overlapping layout - the overlap is NOT the cause");

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Whether Windows validates a layout with two overlapping
    /// displays.
    /// </summary>
    /// <remarks>
    /// The configuration is validated with SDC_VALIDATE only, so
    /// nothing is applied and the desktop is left alone.
    ///
    /// The virtual display is put at (1915, -1080) - the stale
    /// position the registry carries - while the XP-Pen stays at
    /// (1920, 0).  That is the arrangement the user's machine is in
    /// when the bug bites.
    /// </remarks>
    private static bool TestOverlap()
    {
        var active = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var modes);
        if (active is null)
        {
            Console.WriteLine("    the query failed");
            return false;
        }

        // Collect the paths and modes, and move the virtual display
        // onto the XP-Pen.
        var paths = new List<Ccd.Path>();
        var modeList = new List<Ccd.ModeInfo>();

        for (int i = 0; i < active.Length; i++)
        {
            var p = active[i];
            if ((p.flags & 0x1) == 0) continue;
            paths.Add(p);
        }

        // Carry the modes over unchanged, keeping the indices aligned.
        var wanted = new HashSet<uint>();
        foreach (var p in paths)
        {
            if (p.sourceInfo.modeInfoIdx != uint.MaxValue) wanted.Add(p.sourceInfo.modeInfoIdx);
            if (p.targetInfo.modeInfoIdx != uint.MaxValue) wanted.Add(p.targetInfo.modeInfoIdx);
        }

        var remap = new Dictionary<uint, uint>();
        for (int m = 0; m < modes.Length; m++)
        {
            if (!wanted.Contains((uint)m)) continue;
            remap[(uint)m] = (uint)modeList.Count;
            modeList.Add(modes[m]);
        }

        for (int i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            if (p.sourceInfo.modeInfoIdx != uint.MaxValue
                && remap.TryGetValue(p.sourceInfo.modeInfoIdx, out var sIdx))
                p.sourceInfo.modeInfoIdx = sIdx;
            if (p.targetInfo.modeInfoIdx != uint.MaxValue
                && remap.TryGetValue(p.targetInfo.modeInfoIdx, out var tIdx))
                p.targetInfo.modeInfoIdx = tIdx;
            paths[i] = p;
        }

        // Now move the virtual display's source mode onto the XP-Pen.
        foreach (var p in paths)
        {
            if (!Ccd.IsVirtual(p)) continue;
            for (int m = 0; m < modeList.Count; m++)
            {
                var mode = modeList[m];
                if (mode.infoType != 1) continue;
                if (mode.adapterId.LowPart != p.sourceInfo.adapterId.LowPart
                    || mode.adapterId.HighPart != p.sourceInfo.adapterId.HighPart) continue;
                if (mode.id != p.sourceInfo.id) continue;

                // (1915, -1080) - the stale position.
                mode.sourceMode.position = new Ccd.POINTL { x = 1915, y = -1080 };
                modeList[m] = mode;
            }
        }

        // VALIDATE ONLY.
        int validate = Ccd.SetDisplayConfigRaw(
            (uint)paths.Count, paths.ToArray(),
            (uint)modeList.Count, modeList.ToArray(),
            0x00000040);   // SDC_VALIDATE

        Console.WriteLine($"    the validation of the overlapping layout answered: {validate}");
        return validate != 0;
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"
            + (detail is null ? "" : $"  ({detail})"));
        if (!ok) failures++;
    }
}
