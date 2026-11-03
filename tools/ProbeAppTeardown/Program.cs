// SPDX-License-Identifier: MIT
//
// THE REAL-APP END-TO-END TEST.
//
// It starts the actual RokidDiyStation.exe and watches the virtual
// display CONTINUOUSLY while the app is asked to tear it down.
//
// WHY IT WATCHES CONTINUOUSLY
// ---------------------------
// The reload route is: close the spatial display, quit, start a
// replacement.  The replacement opens the spatial display again by
// itself - that is the product's behaviour, because the Rokid Max is
// still attached - so the display is expected to come back at the
// END.  A test that only looks at the final state therefore sees
// nothing.
//
// What matters is the WINDOW in between: the teardown has to take
// the display off the desktop, and it has to STAY off long enough
// that Windows' own topology rebuild cannot resurrect it.  That is
// exactly what the settle window in DisplayConfigController is for,
// and this probe measures it.
//
// The checks are:
//   * the display goes OFF during the teardown;
//   * it stays OFF for longer than the settle window (1.5s), which
//     is the proof that a rebuild cannot resurrect it;
//   * the database does not record it as active while it is off;
//   * and, at the very end, the whole app is killed and the display
//     is STILL off - which is the user's actual complaint.

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeAppTeardown;

internal static class Program
{
    private const string Exe = @"artifacts\release\RokidDiyStation.exe";
    private const string PipeName = "RokidDiyStation.ShowWindow";

    /// <summary>How long the display has to stay off, in ms.</summary>
    private const int RequiredOffMs = 2500;

    private static int failures = 0;

    private static int Main()
    {
        foreach (var p in Process.GetProcessesByName("RokidDiyStation"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
            p.Dispose();
        }
        Thread.Sleep(1000);

        Console.WriteLine("=== the state before the app starts ===");
        Report("before");

        var app = Process.Start(new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(Exe))!,
        });
        if (app is null)
        {
            Console.WriteLine("the app did not start");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("=== the app is running ===");
        Thread.Sleep(9000);
        Report("after the app started");

        Console.WriteLine();
        Console.WriteLine("=== asking the app to QUIT (the user's exit path) ===");

        // The watcher runs for the whole teardown.
        var timeline = new List<(int Ms, bool Active, int DbPaths)>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool watching = true;

        var watcher = new Thread(() =>
        {
            while (watching)
            {
                lock (timeline)
                {
                    timeline.Add(((int)watch.ElapsedMilliseconds,
                        DisplayConfigController.IsVirtualActive(),
                        CountDatabaseVirtualPaths()));
                }
                Thread.Sleep(100);
            }
        })
        { IsBackground = true };
        watcher.Start();

        bool signalled = Signal("quit");
        Console.WriteLine($"  the quit signal was delivered: {signalled}");

        // Wait for the app to actually exit.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName("RokidDiyStation").Length == 0) break;
            Thread.Sleep(200);
        }

        // And let the settle window run out.
        Thread.Sleep(4000);

        watching = false;
        watcher.Join(2000);

        Console.WriteLine();
        Console.WriteLine("=== the timeline ===");
        PrintTimeline(timeline);

        // The teardown window: the longest run of consecutive samples
        // in which the display was off.
        int longestOff = LongestOffRun(timeline);
        Console.WriteLine();
        Console.WriteLine($"  the longest run with the display OFF: {longestOff}ms");

        Check("the display went off during the teardown", longestOff > 0);
        Check($"it stayed off for at least {RequiredOffMs}ms "
            + "(so a topology rebuild cannot resurrect it)",
            longestOff >= RequiredOffMs, $"{longestOff}ms");

        // While it was off, the database must not have claimed it was
        // active.
        var offSamples = timeline.Where(t => !t.Active).ToList();
        int worstDb = offSamples.Count == 0 ? -1 : offSamples.Max(t => t.DbPaths);
        Check("the database did not record it as active while it was off",
            worstDb == 0, $"the worst reading was {worstDb}");

        Console.WriteLine();
        Console.WriteLine("=== the app has exited ===");
        Report("after the app exited");

        ForceTopologyRebuild();
        Thread.Sleep(2000);
        Report("after a rebuild following the exit");

        Check("the virtual display is off the desktop",
            !DisplayConfigController.IsVirtualActive());
        Check("the database does not record it as active",
            CountDatabaseVirtualPaths() == 0,
            $"the database still has {CountDatabaseVirtualPaths()} virtual path(s)");

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The longest run of consecutive samples with the display off.
    /// </summary>
    private static int LongestOffRun(List<(int Ms, bool Active, int DbPaths)> timeline)
    {
        int best = 0;
        int runStart = -1;

        lock (timeline)
        {
            foreach (var t in timeline)
            {
                if (!t.Active)
                {
                    if (runStart < 0) runStart = t.Ms;
                    best = Math.Max(best, t.Ms - runStart);
                }
                else
                {
                    runStart = -1;
                }
            }
        }

        return best;
    }

    private static void PrintTimeline(List<(int Ms, bool Active, int DbPaths)> timeline)
    {
        string last = "";
        lock (timeline)
        {
            foreach (var t in timeline)
            {
                string state = t.Active ? "ON " : "off";
                string line = $"{t.Ms,6}ms  {state}  dbPaths={t.DbPaths}";
                if (line != last)
                {
                    Console.WriteLine("    " + line);
                    last = line;
                }
            }
        }
    }

    private static bool Signal(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 5000);

            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(command);
            writer.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (the signal failed: {ex.Message})");
            return false;
        }
    }

    private static void Report(string when)
    {
        Console.WriteLine($"  --- {when} ---");
        bool active = DisplayConfigController.IsVirtualActive();
        bool enumerated = VirtualDisplayLocator.FindVirtual() is not null;
        int db = CountDatabaseVirtualPaths();
        Console.WriteLine($"      activePath={active,-5} enumMonitor={enumerated,-5} dbVirtualPaths={db}");
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

    private static void ForceTopologyRebuild()
    {
        var paths = Ccd.Query(Ccd.QDC_ONLY_ACTIVE_PATHS, out var modes);
        if (paths is null) return;

        Ccd.SetDisplayConfigRaw(
            (uint)paths.Length, paths,
            (uint)modes.Length, modes,
            0x00000080 /* APPLY */ | 0x00000020 /* USE_SUPPLIED */
            | 0x00000400 /* ALLOW_CHANGES */);
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        if (!ok) failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"
            + (detail is null ? "" : $"  ({detail})"));
    }
}
