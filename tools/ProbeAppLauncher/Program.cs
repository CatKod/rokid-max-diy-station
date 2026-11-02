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

            Console.WriteLine($"  {app.Name,-20} {DescribeKind(app.Executable)}");
            Console.WriteLine($"      {app.Executable}");
            Console.WriteLine($"      {info.Length:N0} bytes, in \"{folder}\"");
        }

        return 0;
    }

    /// <summary>
    /// Say whether an executable can be driven.
    /// </summary>
    /// <remarks>
    /// The size of the executable used to be the whole test, because a
    /// Squirrel STUB is a tiny launcher and the real application is
    /// hundreds of megabytes.  That is no longer enough: the modern
    /// Chromium layout (Cốc Cốc, and recent Chrome builds) puts a
    /// SMALL executable in the install directory and the real code in
    /// a "browser.dll" beside it.  Such an executable is perfectly
    /// usable - it forwards the Chromium switches - and we verified
    /// that it opens a DevTools endpoint.
    ///
    /// So the test is now: is there a big payload next to it?  For
    /// Squirrel that is the versioned executable in "app-&lt;version&gt;";
    /// for Chromium it is the DLL in the versioned folder underneath.
    /// </remarks>
    private static string DescribeKind(string executable)
    {
        var directory = Path.GetDirectoryName(executable);
        if (directory is null) return "?";

        var info = new FileInfo(executable);

        // The modern Chromium layout: a big DLL sits in a versioned
        // folder next to the executable.
        foreach (var versioned in Directory.EnumerateDirectories(directory))
        {
            foreach (var dll in Directory.EnumerateFiles(versioned, "*.dll"))
            {
                if (new FileInfo(dll).Length > 50_000_000)
                    return "real (Chromium layout)";
            }
        }

        // The classic layout: the executable itself is the payload.
        if (info.Length > 50_000_000) return "real";

        // The Squirrel stub: the payload is in "app-&lt;version&gt;".
        foreach (var versioned in Directory.EnumerateDirectories(directory, "app-*"))
        {
            foreach (var file in Directory.EnumerateFiles(versioned, "*.exe"))
            {
                if (new FileInfo(file).Length > 50_000_000) return "real (Squirrel)";
            }
        }

        return "STUB  <-- unusable";
    }
}
