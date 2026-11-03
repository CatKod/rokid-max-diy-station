// SPDX-License-Identifier: MIT
//
// A diagnostic that exercises the virtual display's attach and detach
// CYCLE, repeatedly, and reports whether it always works.
//
// The user's bug: with three physical monitors the detach worked, but
// with one or two the VDD stayed on the desktop in Extended mode.
//
// This probe runs the cycle several times, and between the cycles it
// MOVES the physical displays around - which is what a topology
// change does - so the cycle is exercised against a desktop whose
// layout keeps changing.
//
// It is a diagnostic, not part of the test suite: it changes the
// machine's display topology.

using RokidDiyStation.App.Spatial;

namespace RokidDiyStation.Tools.ProbeVirtualDisplay;

internal static class Program
{
    private static int Main(string[] args)
    {
        var rounds = args.Length > 0 && int.TryParse(args[0], out var r) ? r : 3;
        var verbose = args.Length > 1 && args[1] == "-v";

        DisplayConfigController.OnDiagnostic += m =>
        {
            if (verbose) Console.WriteLine($"    [diag] {m}");
        };

        int failures = 0;

        for (int i = 1; i <= rounds; i++)
        {
            Console.WriteLine($"=== ROUND {i} ===");

            // Start from a known state: the VDD attached.
            if (!DisplayConfigController.IsVirtualActive())
            {
                var ok = DisplayConfigController.Attach(-1920, 0);
                Console.WriteLine($"  attach -> {ok}");
                Thread.Sleep(700);
            }

            // Shuffle the physical displays around.  This is what
            // Windows does when a monitor is plugged or unplugged, and
            // it is what invalidates the topology's mode indices.
            MovePhysicalDisplay(i);

            var before = VirtualDisplayLocator.FindVirtual();
            Console.WriteLine($"  the virtual display is at ({before?.Bounds.Left},{before?.Bounds.Top})");

            var detached = DisplayConfigController.Detach();
            Thread.Sleep(700);

            var after = VirtualDisplayLocator.FindVirtual();
            var active = DisplayConfigController.IsVirtualActive();

            if (detached && after is null && !active)
            {
                Console.WriteLine("  DETACH OK - the virtual display is off the desktop.");
            }
            else
            {
                failures++;
                Console.WriteLine($"  DETACH FAILED - detached={detached} active={active} "
                    + $"monitor={(after is null ? "gone" : $"still at ({after.Bounds.Left},{after.Bounds.Top})")}");
            }

            // And put it back, so the next round starts from the same
            // state.
            var reattached = DisplayConfigController.Attach(-1920, 0);
            Console.WriteLine($"  re-attach -> {reattached}");
            Thread.Sleep(700);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ALL ROUNDS PASSED."
            : $"{failures} of {rounds} rounds FAILED.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Move the physical displays around a little.
    /// </summary>
    private static void MovePhysicalDisplay(int round)
    {
        var paths = Ccd.QueryActive(out var modes);
        if (paths is null) return;

        bool moved = false;
        foreach (var p in paths)
        {
            if (Ccd.IsVirtual(p)) continue;

            string path = Ccd.TargetPath(Ccd.PathAdapter(p), Ccd.PathTargetId(p));
            if (!path.Contains("UGD1302", StringComparison.OrdinalIgnoreCase)) continue;

            var (x, y) = Ccd.SourcePosition(modes, p);
            // Alternate between a straight row and a stepped row, so
            // the desktop's shape really changes between the rounds.
            int nx = x + 100;
            int ny = (round % 2 == 0) ? 0 : -1080;

            Ccd.SetSourcePosition(modes, p, nx, ny);
            Ccd.Apply(paths, modes);
            moved = true;
            break;
        }

        if (moved) Thread.Sleep(500);
    }
}
