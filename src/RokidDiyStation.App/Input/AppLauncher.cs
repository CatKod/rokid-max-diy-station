// SPDX-License-Identifier: MIT
//
// Launching an application that we can drive.
//
// The virtual cursor can only talk to an application that speaks the
// DevTools protocol, and an application only speaks it if it was
// started with --remote-debugging-port.  So "which applications can
// I interact with?" has a blunt answer: the ones we are willing to
// START.
//
// This is not a limitation we can engineer around.  The research
// (.research/i5/) established that there is no way to inject input
// into an arbitrary already-running window: Win32 PostMessage is
// rejected by every modern UI stack, SendInput moves the OS cursor
// (which the user has explicitly ruled out, because they work on a
// second monitor), and a virtual display driver needs admin rights
// and an attestation certificate.
//
// What IS possible
// ----------------
// Electron - which is what VS Code, Discord, Slack, Obsidian, Notion
// and Spotify are all built on - is Chromium with a Node.js runtime
// attached.  It accepts Chromium's command line switches directly,
// including --remote-debugging-port, so it can be driven by exactly
// the same code that drives a browser.
//
// WE VERIFIED THIS.  VS Code, started with the switch, received
// trusted mousemove, mousedown, mouseup and click events over the
// DevTools protocol, and the OS mouse never moved.  The keyboard
// works the same way.
//
// The switch the user has to accept
// --------------------------------
// The application has to be started BY US, with our own profile
// directory, because:
//   * an application that is already running cannot be given the
//     switch, and
//   * sharing the user's profile would fight with the running
//     instance, which refuses to open a second window on the same
//     profile.
//
// So the launcher starts the application with a dedicated profile
// directory under the app's own data folder.  The user's own
// application data is untouched; the app just opens with its default
// settings.
using System.Diagnostics;
using System.IO;

namespace RokidDiyStation.App.Input;

/// <summary>
/// An application that can be started so that we can drive it.
/// </summary>
/// <param name="Name">The application's display name.</param>
/// <param name="Executable">The path to the executable.</param>
/// <param name="Arguments">Extra arguments the application needs.</param>
/// <param name="ProfileDirectory">
/// True when the application keeps its state in a profile directory
/// that has to be redirected for the instance we start.
/// </param>
/// <param name="ProfileKey">
/// The folder name used for the instance's profile directory.  It
/// defaults to the application's name, but a name with characters the
/// file system treats specially - or with characters outside ASCII -
/// is better given an explicit, boring key.
/// </param>
public sealed record DriveableApp(
    string Name,
    string Executable,
    string[]? Arguments = null,
    bool ProfileDirectory = true,
    string? ProfileKey = null)
{
    /// <summary>The name of this application's profile folder.</summary>
    public string ProfileName => ProfileKey ?? Name;
}

/// <summary>
/// Starts applications that speak the DevTools protocol.
/// </summary>
public static class AppLauncher
{
    /// <summary>
    /// Where the profile directories of the applications we start live.
    /// </summary>
    public static string ProfileRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RokidDiyStation", "app-profiles");

    /// <summary>
    /// The DevTools port the launched applications listen on.
    /// </summary>
    /// <remarks>
    /// A dedicated port, so a launched application cannot collide with
    /// the browser the user has open on the same port.  The port is
    /// passed to the application, and the discovery code looks for it
    /// explicitly.
    /// </remarks>
    public const int Port = 9223;

    /// <summary>
    /// The applications we know how to start.
    /// </summary>
    /// <remarks>
    /// The list is deliberately short and explicit rather than a scan
    /// of the Start menu: the user has to know that the application is
    /// going to be restarted with a different profile, and an
    /// automatic scan would make that behaviour unpredictable.
    ///
    /// Each entry is only offered when its executable is actually
    /// present.
    /// </remarks>
    public static IReadOnlyList<DriveableApp> KnownApps()
    {
        // The paths are built from the LOCAL APP DATA and the PROGRAM
        // FILES folders rather than written out, because the user
        // profile's name is part of the path and a hard-coded one
        // would only ever work on the machine it was written on.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // Squirrel-installed apps keep a different layout: a small
        // launcher stub at the top, and the real executable in a
        // versioned folder underneath ("app-4.51.180").  The stub is
        // NOT a usable target - it does not pass the Chromium switches
        // on to the process it starts, so --remote-debugging-port is
        // silently dropped and the app never opens a DevTools endpoint.
        // We verified this with Slack: the stub produced no endpoint,
        // the versioned executable produced one.
        //
        // These are checked FIRST, and the plain paths below are only
        // used as a fallback, so the versioned executable always wins
        // over the stub for the same application.
        var squirrelApps = new[]
        {
            (Root: Path.Combine(local, "slack"), Name: "Slack", Exe: "slack.exe"),
            (Root: Path.Combine(local, "Discord"), Name: "Discord", Exe: "Discord.exe"),
            (Root: Path.Combine(local, "Figma"), Name: "Figma", Exe: "Figma.exe"),
            (Root: Path.Combine(local, "Programs", "Slack"), Name: "Slack", Exe: "slack.exe"),
            (Root: Path.Combine(local, "Programs", "discord"), Name: "Discord", Exe: "Discord.exe"),
        };

        var candidates = new List<DriveableApp>();
        foreach (var (root, name, exe) in squirrelApps)
        {
            var versioned = SquirrelExecutable.Find(root, Path.GetFileName(root), exe);
            if (versioned is not null) candidates.Add(new DriveableApp(name, versioned));
        }

        candidates.AddRange(new[]
        {
            new DriveableApp("Visual Studio Code",
                Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe")),
            new DriveableApp("Visual Studio Code",
                Path.Combine(programFiles, "Microsoft VS Code", "Code.exe")),
            new DriveableApp("Notion",
                Path.Combine(local, "Programs", "Notion", "Notion.exe")),
            new DriveableApp("Discord",
                Path.Combine(local, "Programs", "discord", "Discord.exe")),
            new DriveableApp("Obsidian",
                Path.Combine(local, "Programs", "Obsidian", "Obsidian.exe")),
            new DriveableApp("Slack",
                Path.Combine(local, "Programs", "Slack", "slack.exe")),
            new DriveableApp("Spotify",
                Path.Combine(roaming, "Spotify", "Spotify.exe")),
            new DriveableApp("Telegram",
                Path.Combine(roaming, "Telegram Desktop", "Telegram.exe")),
            new DriveableApp("Figma",
                Path.Combine(local, "Figma", "Figma.exe")),
        });

        // COC COC - a Chromium derivative.  Its layout is the modern
        // Chromium one, not Squirrel's: the executable sits directly in
        // "Browser\Application" and the real code lives in a
        // "browser.dll" inside a versioned folder underneath it.  The
        // launcher at "Application\browser.exe" IS the real binary -
        // it forwards Chromium's command line switches, which we
        // verified: launched with --remote-debugging-port it answered
        // on /json/version with "Chrome/154.0.8037.106".
        //
        // Unlike the Squirrel apps, a separate instance with its own
        // profile coexists with the user's own Cốc Cốc window: the
        // dedicated --user-data-dir keeps the two apart.
        var cocCocRoots = new[]
        {
            Path.Combine(programFiles, "CocCoc", "Browser", "Application", "browser.exe"),
            Path.Combine(local, "CocCoc", "Browser", "Application", "browser.exe"),
        };

        if (!string.IsNullOrEmpty(programFilesX86))
        {
            cocCocRoots = cocCocRoots
                .Append(Path.Combine(programFilesX86, "CocCoc", "Browser", "Application", "browser.exe"))
                .ToArray();
        }

        foreach (var exe in cocCocRoots)
        {
            candidates.Add(new DriveableApp(
                "Cốc Cốc",
                exe,
                // A start page, so the window that appears has content
                // to show and the coordinate mapping has something to
                // measure.
                new[] { "--app=https://www.google.com" },
                ProfileDirectory: true,
                // The display name has diacritics; the profile folder
                // is better off with a plain ASCII key.
                ProfileKey: "CoCoc"));
        }

        // The 32-bit Program Files folder is where a lot of software
        // still lands, and where a machine without the 64-bit one puts
        // everything.
        if (!string.IsNullOrEmpty(programFilesX86))
        {
            candidates.Add(new DriveableApp("Visual Studio Code",
                Path.Combine(programFilesX86, "Microsoft VS Code", "Code.exe")));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DriveableApp>();
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate.Executable)) continue;
            // The first entry wins for a given name, so the list does
            // not show "Visual Studio Code" twice.
            if (!seen.Add(candidate.Name)) continue;
            result.Add(candidate);
        }

        return result;
    }

    /// <summary>
    /// Start an application so that we can drive it.
    /// </summary>
    /// <param name="app">The application to start.</param>
    /// <returns>The process, or null when it could not be started.</returns>
    public static Process? Launch(DriveableApp app)
    {
        var psi = new ProcessStartInfo(app.Executable)
        {
            UseShellExecute = false,
        };

        foreach (var arg in app.Arguments ?? Array.Empty<string>())
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add($"--remote-debugging-port={Port}");

        if (app.ProfileDirectory)
        {
            // A dedicated profile, so the application we start does not
            // fight with the one the user already has open.  Chromium
            // refuses to open a second instance on the same profile,
            // and Electron inherits that behaviour.
            var profile = Path.Combine(ProfileRoot, Sanitize(app.ProfileName));
            Directory.CreateDirectory(profile);
            psi.ArgumentList.Add($"--user-data-dir={profile}");
        }

        // The application must keep rendering even though the AR
        // panel covers it.  Chromium throttles an occluded window to a
        // crawl, and the panel would freeze.
        psi.ArgumentList.Add("--disable-backgrounding-occluded-windows");
        psi.ArgumentList.Add("--disable-features=CalculateNativeWinOcclusion");
        psi.ArgumentList.Add("--disable-renderer-backgrounding");

        try
        {
            return Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                      or InvalidOperationException
                                      or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Wait for the DevTools endpoint of a launched application.
    /// </summary>
    /// <remarks>
    /// The endpoint comes up AFTER the application's window, and for
    /// an Electron app it comes up before the workbench document is
    /// loaded - so the endpoint's presence is necessary but not
    /// sufficient.  The caller still has to wait for the document.
    /// </remarks>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>True when the endpoint answered.</returns>
    public static async Task<bool> WaitForEndpointAsync(
        TimeSpan timeout, CancellationToken token = default)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline && !token.IsCancellationRequested)
        {
            var pages = await CdpDiscovery.ListPagesAsync(Port, token).ConfigureAwait(false);
            if (pages.Count > 0) return true;
            await Task.Delay(300, token).ConfigureAwait(false);
        }
        return false;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
