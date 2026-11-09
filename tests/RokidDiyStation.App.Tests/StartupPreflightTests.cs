// SPDX-License-Identifier: MIT
//
// Tests for the startup gate.
//
// WHY THIS MATTERS ENOUGH TO TEST
// -------------------------------
// The gate is the first thing the app does, and it decides whether
// the app runs at all.  Two things have to hold:
//
//   1. With the glasses attached, the app starts normally and the
//      virtual display is NOT touched - the gate must not get in the
//      way of the normal startup.
//
//   2. With the glasses absent, the app refuses to start, the
//      virtual display is taken off the desktop, and the user is told
//      what to do.
//
// The second half is the one that matters.  A leftover virtual
// monitor is a trap: it swallows windows, and a window dragged onto
// it ends up behind the glasses where it is very hard to get back.
// The gate is what makes the virtual display's lifecycle
// self-healing after a crash.
//
// The DECISION is tested with injected probes, because a test cannot
// unplug the glasses - but it can certainly say "pretend the glasses
// are absent" and check what the app does about it.
using Xunit;

namespace RokidDiyStation.App.Tests;

public class StartupPreflightTests
{
    /// <summary>
    /// With the glasses attached, the app starts and the virtual
    /// display is left alone.
    /// </summary>
    [Fact]
    public void The_App_Starts_When_The_Glasses_Are_Attached()
    {
        var cleanupCalls = 0;

        var outcome = StartupPreflight.Evaluate(
            probeDevice: () => (true, "USB HID: Rokid Max (PID=0x162F)"),
            cleanupVirtualDisplay: () =>
            {
                cleanupCalls++;
                return new VirtualDisplayCleanup(false, true, "not touched");
            });

        Assert.True(outcome.CanStart,
            "the app must start when the Rokid Max is plugged in");
        Assert.Equal(0, cleanupCalls);
    }

    /// <summary>
    /// With the glasses absent, the app refuses to start.
    /// </summary>
    [Fact]
    public void The_App_Refuses_To_Start_When_The_Glasses_Are_Absent()
    {
        var outcome = StartupPreflight.Evaluate(
            probeDevice: () => (false, "no device"),
            cleanupVirtualDisplay: () => new VirtualDisplayCleanup(false, true, "already off"));

        Assert.False(outcome.CanStart,
            "the app must not start without the Rokid Max");
        Assert.Contains("not connected", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rokid Max", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The virtual display is taken off the desktop BEFORE the app
    /// quits, so the user never sees the app refuse to start while a
    /// stray virtual monitor is still on the desktop.
    /// </summary>
    [Fact]
    public void The_Virtual_Display_Is_Taken_Off_The_Desktop_Before_Quitting()
    {
        var cleanupCalls = 0;

        var outcome = StartupPreflight.Evaluate(
            probeDevice: () => (false, "no device"),
            cleanupVirtualDisplay: () =>
            {
                cleanupCalls++;
                return new VirtualDisplayCleanup(
                    WasAttached: true,
                    IsDetached: true,
                    Detail: "taken off the desktop");
            });

        Assert.False(outcome.CanStart);
        Assert.Equal(1, cleanupCalls);
        Assert.Contains("taken off the desktop", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When the virtual display is already off the desktop, the
    /// message says so - and does not claim to have done anything.
    /// </summary>
    [Fact]
    public void An_Already_Clean_Desktop_Is_Not_Described_As_Cleaned()
    {
        var outcome = StartupPreflight.Evaluate(
            probeDevice: () => (false, "no device"),
            cleanupVirtualDisplay: () => new VirtualDisplayCleanup(false, true, "already off"));

        Assert.False(outcome.CanStart);
        Assert.Contains("already off the desktop", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("has been taken off", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// If the virtual display could NOT be taken off the desktop, the
    /// user has to be told - otherwise the app would be quietly
    /// leaving the trap it was supposed to remove.
    /// </summary>
    [Fact]
    public void A_Failed_Cleanup_Is_Reported_To_The_User()
    {
        var outcome = StartupPreflight.Evaluate(
            probeDevice: () => (false, "no device"),
            cleanupVirtualDisplay: () => new VirtualDisplayCleanup(
                WasAttached: true,
                IsDetached: false,
                Detail: "could not be taken off"));

        Assert.False(outcome.CanStart);
        Assert.Contains("could NOT", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Display Settings", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When the display's state could not be read at all, the message
    /// must not claim the desktop is clean - it is not known.
    /// </summary>
    [Fact]
    public void An_Unknown_State_Is_Not_Described_As_A_Clean_Desktop()
    {
        var outcome = StartupPreflight.Evaluate(
            probeDevice: () => (false, "no device"),
            cleanupVirtualDisplay: () => new VirtualDisplayCleanup(
                WasAttached: false,
                IsDetached: false,
                StateKnown: false,
                Detail: "the topology could not be queried"));

        Assert.False(outcome.CanStart);
        Assert.DoesNotContain("already off", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be read", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The exit code is not zero, so a launcher script can tell "the
    /// app ran" from "the app refused to start".
    /// </summary>
    [Fact]
    public void The_Refused_Start_Has_Its_Own_Exit_Code()
    {
        Assert.NotEqual(0, StartupPreflight.ExitCodeNoDevice);
    }
}
