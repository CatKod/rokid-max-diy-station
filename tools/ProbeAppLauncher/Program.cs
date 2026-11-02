// SPDX-License-Identifier: MIT
//
// Probe: which executable does the launcher actually pick for each
// application?
//
// This matters more than it looks.  Squirrel-installed apps (Slack,
// Discord, Figma) keep a small launcher stub at the top of the install
// directory and the real executable in a versioned folder underneath.
// The stub DROPS the Chromium switches, so launching it with
// --remote-debugging-port produces an application with no DevTools
// endpoint - which means the virtual cursor can see it but not touch
// it.
//
// Usage:
//   dotnet run --project tools/ProbeAppLauncher
using RokidDiyStation.App.Input;

internal static class Program
{
    private static int Main()
    {
        var apps = AppLauncher.KnownApps();
        if (apps.Count == 0)
        {
            Console.WriteLine("No applications were found.");
            return 1;
        }

        Console.WriteLine("The applications the launcher can drive:");
        foreach (var app in apps)
        {
            var info = new FileInfo(app.Executable);
            var folder = Path.GetFileName(Path.GetDirectoryName(app.Executable) ?? "");

            // A Squirrel stub is a small launcher; the real thing is
            // hundreds of megabytes.  The size is the tell.
            var kind = info.Length < 5_000_000 ? "STUB  <-- unusable" : "real";

            Console.WriteLine($"  {app.Name,-20} {kind}");
            Console.WriteLine($"      {app.Executable}");
            Console.WriteLine($"      {info.Length:N0} bytes, in \"{folder}\"");
        }

        return 0;
    }
}
