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
public sealed record DriveableApp(
    string Name,
    string Executable,
    string[]? Arguments = null,
    bool ProfileDirectory = true);

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
        var candidates = new[]
        {
            new DriveableApp("Visual Studio Code",
                @"C:\Users\kimvi\AppData\Local\Programs\Microsoft VS Code\Code.exe"),
            new DriveableApp("Visual Studio Code",
                @"C:\Program Files\Microsoft VS Code\Code.exe"),
            new DriveableApp("Notion",
                @"C:\Users\kimvi\AppData\Local\Programs\Notion\Notion.exe"),
            new DriveableApp("Discord",
                @"C:\Users\kimvi\AppData\Local\Programs\discord\Discord.exe"),
            new DriveableApp("Obsidian",
                @"C:\Users\kimvi\AppData\Local\Programs\Obsidian\Obsidian.exe"),
            new DriveableApp("Slack",
                @"C:\Users\kimvi\AppData\Local\Programs\Slack\slack.exe"),
            new DriveableApp("Spotify",
                @"C:\Users\kimvi\AppData\Roaming\Spotify\Spotify.exe"),
        };

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
            var profile = Path.Combine(ProfileRoot, Sanitize(app.Name));
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
