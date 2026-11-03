// SPDX-License-Identifier: MIT
//
// A LIVE end-to-end test of the reload.
//
// The reload's whole difficulty is in the INTERACTION between two
// processes:
//
//   * the replacement has to WAIT for the outgoing instance to release
//     the single-instance mutex, and
//   * it has to end up running, with the pipe working.
//
// Neither can be checked from inside a single process.  This test
// starts the real app, asks it to reload through the same pipe the
// tray menu uses, and checks that a new process took over.
//
// It is in the test suite - and not left as a manual diagnostic -
// because the first version of the reload DID NOT WORK, and it did not
// work for a reason that no unit test would have caught: the pipe's
// payload carried a UTF-8 BOM, so the command never matched.  A test
// that exercises the real path is the only thing that would have
// found it.
//
// The test is skipped when the app's exe is not next to the test
// binary, because a test run from an IDE does not have it there.

using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class ReloadEndToEndTests
{
    private const string PipeName = "RokidDiyStation.ShowWindow";

    /// <summary>
    /// How long the outgoing instance needs to quit and the
    /// replacement needs to come up.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(25);

    private static string? FindAppExe()
    {
        // The publish output puts the exe beside the app's DLLs.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "RokidDiyStation.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "artifacts", "release", "RokidDiyStation.exe"),
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }

        return null;
    }

    private static int CountAppProcesses()
    {
        var n = 0;
        foreach (var p in Process.GetProcessesByName("RokidDiyStation"))
        {
            n++;
            p.Dispose();
        }
        return n;
    }

    private static bool Signal(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 3000);
            // No BOM: see SingleInstance.Dispatch.
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(command);
            writer.Flush();
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }

    [Fact]
    public void The_App_Reloads_Itself_In_Place()
    {
        var exe = FindAppExe();
        Assert.True(exe is not null,
            "the app exe was not found, so the live reload test cannot run");

        // Anything already running would confuse the process counts.
        foreach (var p in Process.GetProcessesByName("RokidDiyStation"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
            p.Dispose();
        }
        Thread.Sleep(1000);

        var first = Process.Start(new ProcessStartInfo(exe!)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe!)!,
        });
        Assert.NotNull(first);

        try
        {
            // Give it time to start, attach the display and open the
            // pipe.
            Thread.Sleep(7000);
            Assert.Equal(1, CountAppProcesses());

            // Ask it to reload, through the real path.
            Assert.True(Signal("reload"), "the reload command did not reach the app");

            // The outgoing instance has to quit and the replacement has
            // to take over.
            Thread.Sleep(12000);

            var originalExited = first.HasExited;
            Assert.True(originalExited,
                "the outgoing instance did not exit, so the app did not reload");

            // A replacement must be running.
            var replacementRunning = false;
            foreach (var p in Process.GetProcessesByName("RokidDiyStation"))
            {
                if (p.Id != first.Id) replacementRunning = true;
                p.Dispose();
            }

            Assert.True(replacementRunning,
                "the replacement instance did not take over - the app would have been left with nothing");

            // And the pipe must still work, so a later double-click can
            // show the window of the instance that took over.
            Assert.True(Signal("0"),
                "the replacement's pipe is not answering");
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName("RokidDiyStation"))
            {
                try { p.Kill(); } catch { }
                p.Dispose();
            }
        }
    }
}
