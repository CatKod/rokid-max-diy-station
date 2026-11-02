// SPDX-License-Identifier: MIT
//
// Tests for the application launcher's path discovery.
//
// WHY THIS MATTERS ENOUGH TO TEST
// -------------------------------
// Squirrel-installed applications (Slack, Discord, Figma) keep a
// small launcher STUB at the top of their install directory and the
// real executable in a versioned folder underneath ("app-4.51.180").
//
// The stub is a trap.  It does not forward Chromium's command line
// switches to the process it starts, so "--remote-debugging-port" is
// dropped on the floor and the application never opens a DevTools
// endpoint.  The result is an application that is perfectly visible on
// the glasses, with the virtual cursor drawn on it, that cannot be
// clicked at all.
//
// We verified this on hardware with Slack: the stub produced no
// endpoint, the versioned executable produced one.
//
// So the launcher must prefer the versioned executable, and it must
// pick the NEWEST version - comparing the versions numerically,
// because "app-4.9.0" sorts before "app-4.9.10" as text but is the
// older one.
using System.IO;
using RokidDiyStation.App.Input;
using Xunit;

namespace RokidDiyStation.App.Tests;

public sealed class AppLauncherPathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rokid-launcher-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Create a fake Squirrel install: a stub at the root and a
    /// versioned folder beside it.
    /// </summary>
    private void MakeSquirrelInstall(
        string folderName, string version, string executableName, int stubBytes = 1024)
    {
        var root = Path.Combine(_root, folderName);
        Directory.CreateDirectory(root);

        // The stub.
        File.WriteAllBytes(Path.Combine(root, executableName), new byte[stubBytes]);

        // The real thing.
        var versioned = Path.Combine(root, "app-" + version);
        Directory.CreateDirectory(versioned);
        File.WriteAllBytes(Path.Combine(versioned, executableName), new byte[8 * 1024 * 1024]);
    }

    [Fact]
    public void Finds_the_versioned_executable_and_not_the_stub()
    {
        MakeSquirrelInstall("slack", "4.51.180", "slack.exe");

        var found = SquirrelExecutable.Find(_root, "slack", "slack.exe");

        Assert.NotNull(found);
        Assert.Contains("app-4.51.180", found);
    }

    [Fact]
    public void The_stub_alone_is_not_an_answer()
    {
        // Only the stub - no versioned folder.  Returning the stub
        // would produce an application that cannot be driven.
        var root = Path.Combine(_root, "slack");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "slack.exe"), new byte[1024]);

        var found = SquirrelExecutable.Find(_root, "slack", "slack.exe");

        Assert.Null(found);
    }

    [Fact]
    public void Picks_the_newest_version_numerically_not_alphabetically()
    {
        // "4.9.10" is newer than "4.9.0", but as TEXT "app-4.9.0" sorts
        // FIRST - the "0" comes before the "1".  Sorting the folder
        // names as text would launch the OLDER build.
        MakeSquirrelInstall("slack", "4.9.0", "slack.exe");
        MakeSquirrelInstall("slack", "4.9.10", "slack.exe");

        var found = SquirrelExecutable.Find(_root, "slack", "slack.exe");

        Assert.NotNull(found);
        Assert.Contains("app-4.9.10", found);
    }

    [Fact]
    public void Ignores_folders_that_are_not_a_version()
    {
        var root = Path.Combine(_root, "slack");
        Directory.CreateDirectory(Path.Combine(root, "app-notaversion"));
        File.WriteAllBytes(Path.Combine(root, "app-notaversion", "slack.exe"), new byte[1024]);

        var found = SquirrelExecutable.Find(_root, "slack", "slack.exe");

        Assert.Null(found);
    }

    [Fact]
    public void Ignores_a_missing_install_directory()
    {
        var found = SquirrelExecutable.Find(
            Path.Combine(_root, "does-not-exist"), "slack", "slack.exe");

        Assert.Null(found);
    }

    /// <summary>
    /// A display name with characters the file system treats specially
    /// still has to produce a usable profile folder.
    /// </summary>
    /// <remarks>
    /// The name is only a LABEL.  What goes on disk is the profile key,
    /// so an application whose name carries diacritics - Cốc Cốc - can
    /// use a plain ASCII key and keep its profile folder boring.
    /// </remarks>
    [Fact]
    public void The_profile_folder_uses_the_key_not_the_display_name()
    {
        var app = new DriveableApp("Cốc Cốc", "browser.exe", ProfileKey: "CoCoc");

        Assert.Equal("CoCoc", app.ProfileName);
    }

    [Fact]
    public void Without_a_key_the_display_name_is_the_profile_folder()
    {
        var app = new DriveableApp("Slack", "slack.exe");

        Assert.Equal("Slack", app.ProfileName);
    }
}
