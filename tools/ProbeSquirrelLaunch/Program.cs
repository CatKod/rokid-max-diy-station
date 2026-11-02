// SPDX-License-Identifier: MIT
//
// Probe: can the launcher actually make Slack drivable?
//
// This is the end-to-end check for the Squirrel-stub problem.  The
// launcher starts the application with --remote-debugging-port; the
// probe then asks the DevTools endpoint what it sees.
//
// A stub launch produces NO endpoint, which is exactly the bug: the
// application is on screen, the virtual cursor is drawn on it, and
// nothing can be clicked.
//
// Usage:
//   dotnet run --project tools/ProbeSquirrelLaunch
using System.Diagnostics;
using RokidDiyStation.App.Input;

internal static class Program
{
    private static int Main()
    {
        var apps = AppLauncher.KnownApps();
        var slack = apps.FirstOrDefault(a => a.Name.Equals("Slack", StringComparison.OrdinalIgnoreCase));
        if (slack is null)
        {
            Console.WriteLine("Slack is not installed.");
            return 1;
        }

        Console.WriteLine($"Slack executable : {slack.Executable}");

        var process = AppLauncher.Launch(slack);
        if (process is null)
        {
            Console.WriteLine("FAIL: the launcher could not start Slack.");
            return 1;
        }

        try
        {
            if (!AppLauncher.WaitForEndpointAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false).GetAwaiter().GetResult())
            {
                Console.WriteLine("FAIL: no DevTools endpoint on port " + AppLauncher.Port);
                return 1;
            }

            var pages = CdpDiscovery.ListPagesAsync(AppLauncher.Port).ConfigureAwait(false).GetAwaiter().GetResult();
            Console.WriteLine($"PASS: the endpoint is up and reports {pages.Count} page(s):");
            foreach (var page in pages)
            {
                Console.WriteLine($"    [{page.Type}] {page.Title}");
            }

            Console.WriteLine();
            Console.WriteLine("Slack is now drivable by the virtual cursor.");
            return 0;
        }
        finally
        {
            // The instance we started is left running so the user can
            // look at it; the probe only reports.
        }
    }
}
