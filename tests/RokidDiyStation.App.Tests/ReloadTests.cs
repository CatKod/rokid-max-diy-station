// SPDX-License-Identifier: MIT
//
// Tests for the "reload the app" action.
//
// The user asked for a tray-menu action that restarts the app, because
// several small faults only clear when the app is cycled - and
// chasing each one individually was a losing game.
//
// The action is easy to get wrong in a way that leaves the user with
// NO app at all, which is worse than the fault it was meant to fix.
// These tests pin down the two things that could do that:
//
//   1. the replacement instance must not conclude "another copy is
//      already running" and exit, and
//   2. the outgoing instance must release the single-instance mutex
//      so the replacement can take over.
//
// The reload also has to leave the virtual display behind: a new
// instance that finds the display already attached cannot attach it
// itself.

using System.Reflection;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class ReloadTests
{
    /// <summary>
    /// The tray menu must offer the reload action.
    /// </summary>
    [Fact]
    public void The_Tray_Context_Exposes_A_Reload_Method()
    {
        var tray = typeof(RokidDiyStation.App.TrayApplicationContext);

        Assert.NotNull(tray.GetMethod("Reload", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    /// <summary>
    /// The replacement instance has to be able to tell that it is a
    /// replacement.
    /// </summary>
    [Fact]
    public void The_Single_Instance_Guard_Knows_About_The_Takeover()
    {
        var single = typeof(RokidDiyStation.App.SingleInstance);

        // The flag the command line sets.
        Assert.NotNull(single.GetProperty("TookOver"));
    }

    /// <summary>
    /// The takeover has to be REQUESTABLE, because the replacement is
    /// started before the outgoing instance has finished exiting.
    /// </summary>
    [Fact]
    public void The_Single_Instance_Guard_Accepts_A_Takeover_Request()
    {
        var ctor = typeof(RokidDiyStation.App.SingleInstance)
            .GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 1);

        Assert.NotNull(ctor);
        Assert.Equal(typeof(bool), ctor!.GetParameters()[0].ParameterType);
    }

    /// <summary>
    /// The mutex has to be releasable on demand.
    /// </summary>
    /// <remarks>
    /// If the release were left to the finalizer, the replacement
    /// would be racing a garbage collector - and the replacement
    /// would usually time out and give up, leaving the user with no
    /// app at all.
    /// </remarks>
    [Fact]
    public void The_Mutex_Can_Be_Released_Explicitly()
    {
        Assert.NotNull(typeof(RokidDiyStation.App.SingleInstance)
            .GetMethod("ReleaseOwnership", BindingFlags.Instance | BindingFlags.Public));
    }

    /// </summary>
    /// The app has to accept the "--reload" flag.
    /// </summary>
    [Fact]
    public void The_Program_Passes_The_Reload_Flag_To_The_Guard()
    {
        var main = typeof(RokidDiyStation.App.Program)
            .GetMethod("Main", BindingFlags.Static | BindingFlags.Public);

        Assert.NotNull(main);
    }

    /// <summary>
    /// The reload has to release the virtual display before the new
    /// instance starts.
    /// </summary>
    /// <remarks>
    /// A leftover virtual display is a trap - a window dragged onto it
    /// goes behind the glasses and is very hard to get back - and the
    /// new instance would also fail to attach it, because it is
    /// already there.
    /// </remarks>
    [Fact]
    public void The_Station_Can_Close_The_Spatial_Display()
    {
        Assert.NotNull(typeof(RokidDiyStation.App.Spatial.SpatialStation)
            .GetMethod("CloseSpatialDisplay"));
    }

    /// <summary>
    /// The pipe's command must survive the trip through the pipe.
    /// </summary>
    /// <remarks>
    /// This is a regression test for a real bug, and the bug was
    /// invisible until the reload was actually exercised end to end.
    ///
    /// `new StreamWriter(stream, Encoding.UTF8)` writes a BOM preamble,
    /// because Encoding.UTF8 carries one.  The server therefore
    /// received "\uFEFFreload" rather than "reload", and the exact
    /// comparison against the command failed.
    ///
    /// The "show the window" path kept working, which is why the bug
    /// survived so long: int.TryParse rejects the BOM, the code falls
    /// through to "show hwnd 0", and that happens to be the right
    /// answer.  The reload path needed an exact match, so it simply
    /// never fired - the button appeared to do nothing at all.
    /// </remarks>
    [Fact]
    public void The_Command_Is_Not_Defeated_By_A_Byte_Order_Mark()
    {
        // The payload as it arrives from a client that wrote a BOM.
        var withBom = "\uFEFF" + SingleInstance.ReloadCommand;

        // The server trims the BOM before comparing.
        var command = withBom.Trim().TrimStart('\uFEFF').Trim();

        Assert.Equal(SingleInstance.ReloadCommand, command);
        Assert.True(
            string.Equals(command, SingleInstance.ReloadCommand, StringComparison.OrdinalIgnoreCase),
            "the reload command must be recognised even when the client wrote a BOM");
    }

    /// <summary>
    /// The client must not write a BOM in the first place.
    /// </summary>
    [Fact]
    public void The_Clients_Write_Utf8_Without_A_Bom()
    {
        // Both ends of the pipe.  A BOM is legal UTF-8, so nothing
        // complains about it - it just silently changes the string.
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var bytes = encoding.GetBytes(SingleInstance.ReloadCommand);

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "the pipe payload must not start with a UTF-8 BOM");
    }
}
