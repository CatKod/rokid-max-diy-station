// SPDX-License-Identifier: MIT
//
// THE DUPLICATE-PATH HYPOTHESIS.
//
// The app's log has a line that does not add up:
//
//   [18:59:53.386] Display config: SetDisplayConfig with 4 path(s):
//
// FOUR paths.  The machine has a laptop panel, the Rokid Max's
// display, and the virtual display - THREE.  So the app submitted a
// FOURTH path.
//
// WHY THAT WOULD BREAK THE DETACH
// ------------------------------
// The detach removes THE virtual path - the FIRST one it finds:
//
//   foreach (var p in active.Paths)
//   {
//       if (IsVirtualTarget(p.targetInfo.adapterId, p.targetInfo.id))
//       {
//           removed = true;
//           continue;      // <-- only this ONE is dropped
//       }
//       kept.Add(p);
//   }
//
// If the virtual display has TWO active paths - which happens when the
// IddCx driver re-publishes its monitor after a topology change, and
// Windows keeps the old path alongside the new one - then the detach
// drops one of them and KEEPS the other.  The virtual display is still
// on the desktop, and it is still an active path.
//
// That is the user's bug exactly: the VDD stays in Extended mode.
//
// THIS PROBE COUNTS THE VIRTUAL DISPLAY'S PATHS, in the active set and
// in the database, before and after an attach/detach cycle.

using System.Text;

using RokidDiyStation.App.Spatial;

namespace RokidDiyStation.Tools.ProbeVirtualDisplay;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("=== the initial state ===");
        CountPaths();

        Console.WriteLine();
        Console.WriteLine("=== an attach/detach cycle ===");

        var attached = DisplayConfigController.Attach(-1920, 0);
        Thread.Sleep(1200);
        Console.WriteLine($"  attach -> {attached}");
        CountPaths();

        var detached = DisplayConfigController.Detach();
        Thread.Sleep(1200);
        Console.WriteLine($"  detach -> {detached}");
        CountPaths();

        // And once more, to see whether the count grows.
        Console.WriteLine();
        Console.WriteLine("=== a second cycle ===");
        DisplayConfigController.Attach(-1920, 0);
        Thread.Sleep(1200);
        CountPaths();

        DisplayConfigController.Detach();
        Thread.Sleep(1200);
        CountPaths();

        // The probe's default ending is to put the virtual display
        // BACK, so that a run does not leave the machine without it.
        //
        // But "detach" has to be able to leave the machine clean too -
        // and that is the state the user cares about: the display must
        // STAY off, not just be off for the moment.  So "detach-only"
        // skips the restore and the run ends with the display off.
        if (args.Length > 0 && args[0] == "detach-only")
        {
            Console.WriteLine();
            Console.WriteLine("  ending with the virtual display detached (detach-only).");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"  restoring: attach -> {DisplayConfigController.Attach(-1920, 0)}");
        Thread.Sleep(1200);

        return 0;
    }

    /// <summary>
    /// Count the paths that lead to the virtual display, and show them.
    /// </summary>
    private static void CountPaths()
    {
        var sb = new StringBuilder();

        ReportPaths(sb, Ccd.QDC_ONLY_ACTIVE_PATHS, "the ACTIVE set");
        ReportPaths(sb, Ccd.QDC_ALL_PATHS, "the DATABASE");
        ReportPaths(sb, Ccd.QDC_DATABASE_CURRENT, "the DATABASE (current)");

        Console.Write(sb.ToString());
    }

    private static void ReportPaths(StringBuilder sb, uint flags, string label)
    {
        var paths = Ccd.Query(flags, out var modes);
        if (paths is null)
        {
            sb.AppendLine($"  {label}: the query failed.");
            return;
        }

        int virtualCount = 0;
        var lines = new List<string>();

        for (int i = 0; i < paths.Length; i++)
        {
            var p = paths[i];
            bool isVirtual = Ccd.IsVirtual(p);
            if (isVirtual) virtualCount++;

            string pos = "";
            if (p.sourceInfo.modeInfoIdx != uint.MaxValue
                && p.sourceInfo.modeInfoIdx < modes.Length)
            {
                var m = modes[p.sourceInfo.modeInfoIdx];
                if (m.infoType == 1)
                {
                    pos = $" ({m.sourceMode.position.x},{m.sourceMode.position.y})";
                }
            }

            lines.Add($"      [{i}] active={((p.flags & 0x1) != 0 ? "Y" : "N")}"
                + $" adapter={p.targetInfo.adapterId} target={p.targetInfo.id}"
                + $" source={p.sourceInfo.id}{pos}"
                + (isVirtual ? "   <== THE VIRTUAL DISPLAY" : ""));
        }

        sb.AppendLine($"  {label}: {paths.Length} path(s), {virtualCount} of them virtual");
        foreach (var line in lines) sb.AppendLine(line);
    }
}
