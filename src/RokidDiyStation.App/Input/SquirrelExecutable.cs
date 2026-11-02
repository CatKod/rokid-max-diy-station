// SPDX-License-Identifier: MIT
//
// Finding the real executable of a Squirrel-installed application.
//
// THE TRAP
// --------
// Squirrel (the installer Electron apps like Slack, Discord and Figma
// use) keeps TWO executables:
//
//   %LOCALAPPDATA%\slack\slack.exe                  <- a 300 KB stub
//   %LOCALAPPDATA%\slack\app-4.51.180\slack.exe    <- the real 225 MB app
//
// The stub is a launcher: it finds the newest versioned folder and
// starts that.  But it does NOT forward Chromium's command line
// switches to the process it starts.  So when we launch the stub with
// "--remote-debugging-port=9223", the switch is silently dropped and
// the application never opens a DevTools endpoint.
//
// The symptom is confusing: the application is on the glasses, the
// virtual cursor is drawn on it and follows the mouse perfectly, and
// not one click reaches it.
//
// WE VERIFIED THIS ON HARDWARE with Slack - the stub produced no
// endpoint, the versioned executable produced one immediately.
//
// So the launcher must always target the versioned executable.
using System.IO;

namespace RokidDiyStation.App.Input;

/// <summary>
/// Locates the real executable inside a Squirrel install directory.
/// </summary>
public static class SquirrelExecutable
{
    /// <summary>
    /// Find the newest versioned executable in a Squirrel install.
    /// </summary>
    /// <param name="searchRoot">
    /// The directory to search under - it may be the install directory
    /// itself, or a parent of it.
    /// </param>
    /// <param name="folderName">
    /// The install folder's name, e.g. "slack".  Its subdirectories
    /// named "app-&lt;version&gt;" are searched.
    /// </param>
    /// <param name="executableName">The executable's file name.</param>
    /// <returns>
    /// The newest versioned executable, or null when there is none.
    /// The stub is never returned: it cannot be driven.
    /// </returns>
    public static string? Find(string searchRoot, string folderName, string executableName)
    {
        if (!Directory.Exists(searchRoot)) return null;

        // The install directory is either the search root itself or one
        // of its immediate subdirectories - Squirrel puts it under
        // %LOCALAPPDATA%, but some installers nest it one level deeper.
        var installRoot = Path.Combine(searchRoot, folderName);
        if (!Directory.Exists(installRoot))
        {
            installRoot = searchRoot;
        }

        string? best = null;
        var bestVersion = new Version(0, 0);

        foreach (var directory in Directory.EnumerateDirectories(installRoot, "app-*"))
        {
            var folder = Path.GetFileName(directory);
            var versionText = folder["app-".Length..];

            // The version is compared NUMERICALLY.  Sorting the folder
            // names as text would pick "app-4.9.0" over "app-4.9.10",
            // because "0" sorts before "1" - and launch the OLDER build.
            if (!Version.TryParse(versionText, out var version)) continue;

            var candidate = Path.Combine(directory, executableName);
            if (!File.Exists(candidate)) continue;

            if (version > bestVersion)
            {
                bestVersion = version;
                best = candidate;
            }
        }

        return best;
    }
}
