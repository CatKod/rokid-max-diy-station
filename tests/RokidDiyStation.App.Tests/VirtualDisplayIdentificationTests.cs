// SPDX-License-Identifier: MIT
//
// Tests for the virtual display's identification.
//
// WHY THESE TESTS EXIST
// ---------------------
// The user's report: the virtual display could not be disabled once
// the number of physical monitors changed.  With three physical
// monitors the detach worked; with one or two, the VDD stayed on the
// desktop in Extended mode, usually repositioned above the other
// displays.
//
// Two separate things were wrong, and they are different in kind:
//
//   1. The CCD DETACH sent the wrong positions, so Windows refused
//      the whole configuration with ERROR_INVALID_PARAMETER (87).
//      That is the SetDisplayConfig half, and it is exercised by the
//      probe under tools/, because it needs a real topology.
//
//   2. The IDENTIFICATION was spread across several places, some of
//      which keyed off a POSITION rather than off the hardware.
//
// THIS FILE IS ABOUT THE SECOND ONE.
//
// THE RULE
// --------
// The virtual display is identified by HARDWARE IDENTITY - the EDID
// fingerprint in the device path, the friendly name, and the raw EDID
// manufacturer and product code.  It is NEVER identified by:
//
//   * a coordinate,
//   * an enumeration index ("\\.\DISPLAYn"),
//   * a source or target id,
//   * a path index in the array.
//
// All of those change when the machine's display topology changes,
// which is exactly when the code has to keep working.
//
// The one thing this file CAN test is the pure decision: given a
// device path, a friendly name and a pair of EDID ids, is this the
// virtual display?  So the decision logic is extracted into a testable
// pure function, and the Win32 wrapper calls it.

using RokidDiyStation.App.Spatial;

using Xunit;

namespace RokidDiyStation.App.Tests;

public class VirtualDisplayIdentificationTests
{
    // -----------------------------------------------------------------
    // The real device paths, taken from the machine's own topology.
    // -----------------------------------------------------------------

    /// <summary>
    /// The virtual display's target device path, as the driver reports
    /// it.
    /// </summary>
    private const string VirtualDevicePath =
        @"\\?\DISPLAY#MTT1337#1&28a6823a&9&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    /// <summary>The XP-Pen Artist 13.3's target device path.</summary>
    private const string ArtistDevicePath =
        @"\\?\DISPLAY#UGD1302#4&80abe25&0&UID4145#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    /// <summary>The laptop panel's target device path.</summary>
    private const string LaptopDevicePath =
        @"\\?\DISPLAY#CMN1521#4&80abe25&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    // -----------------------------------------------------------------
    // The device path fingerprint.
    // -----------------------------------------------------------------

    [Fact]
    public void TheVirtualDisplayIsFoundByItsDevicePathFingerprint()
    {
        Assert.True(VirtualDisplayIdentity.MatchesDevicePath(VirtualDevicePath));
    }

    [Fact]
    public void APhysicalDisplayIsNotFoundByTheDevicePathFingerprint()
    {
        Assert.False(VirtualDisplayIdentity.MatchesDevicePath(ArtistDevicePath));
        Assert.False(VirtualDisplayIdentity.MatchesDevicePath(LaptopDevicePath));
    }

    [Fact]
    public void TheFingerprintIsMatchedAnywhereInThePath()
    {
        // The part of the path AFTER the fingerprint is the port and
        // the instance - and THAT changes when the topology changes.
        // So the fingerprint has to be matched anywhere in the path,
        // not at a fixed offset.
        Assert.True(VirtualDisplayIdentity.MatchesDevicePath(
            @"\\?\DISPLAY#MTT1337#4&deadbeef&7&UID999#{some-guid}"));
    }

    [Fact]
    public void TheFingerprintIsMatchedRegardlessOfCase()
    {
        Assert.True(VirtualDisplayIdentity.MatchesDevicePath(
            @"\\?\DISPLAY#mtt1337#1&28a6823a&9&UID256#{guid}"));
    }

    [Fact]
    public void AnEmptyDevicePathIsNotTheVirtualDisplay()
    {
        // The Win32 wrapper returns "" when the API refuses the
        // request, and calling Contains on null throws.  So the empty
        // string has to be handled.
        Assert.False(VirtualDisplayIdentity.MatchesDevicePath(""));
        Assert.False(VirtualDisplayIdentity.MatchesDevicePath(null!));
    }

    // -----------------------------------------------------------------
    // The friendly name.
    // -----------------------------------------------------------------

    [Fact]
    public void TheVirtualDisplayIsFoundByItsFriendlyName()
    {
        // An INDEPENDENT second fingerprint, in case a driver version
        // changes the EDID.
        Assert.True(VirtualDisplayIdentity.MatchesFriendlyName("VDD by MTT"));
        Assert.True(VirtualDisplayIdentity.MatchesFriendlyName("vdd by mtt"));
    }

    [Fact]
    public void APhysicalDisplayIsNotFoundByTheFriendlyName()
    {
        Assert.False(VirtualDisplayIdentity.MatchesFriendlyName("Artist13.3pro"));
        Assert.False(VirtualDisplayIdentity.MatchesFriendlyName("Generic PnP Monitor"));
        Assert.False(VirtualDisplayIdentity.MatchesFriendlyName(""));
        Assert.False(VirtualDisplayIdentity.MatchesFriendlyName(null!));
    }

    // -----------------------------------------------------------------
    // The raw EDID ids.
    // -----------------------------------------------------------------

    [Fact]
    public void TheVirtualDisplayIsFoundByItsEdidIds()
    {
        // "MTT" packed into 15 bits, and the product code 0x1337.
        // These are the most hardware-like identity available: they
        // describe the MONITOR, not the port it is plugged into.
        Assert.True(VirtualDisplayIdentity.MatchesEdid(0x9436, 0x1337));
    }

    [Fact]
    public void APhysicalDisplayIsNotFoundByItsEdidIds()
    {
        Assert.False(VirtualDisplayIdentity.MatchesEdid(0xE454, 0x1302)); // the artist
        Assert.False(VirtualDisplayIdentity.MatchesEdid(0xAE0D, 0x1521)); // the laptop panel
        Assert.False(VirtualDisplayIdentity.MatchesEdid(0, 0));
    }

    // -----------------------------------------------------------------
    // The whole decision.
    // -----------------------------------------------------------------

    [Fact]
    public void TheWholeDecisionAcceptsTheVirtualDisplay()
    {
        Assert.True(VirtualDisplayIdentity.IsVirtual(VirtualDevicePath, "VDD by MTT", 0x9436, 0x1337));
    }

    [Fact]
    public void TheWholeDecisionRejectsPhysicalDisplays()
    {
        Assert.False(VirtualDisplayIdentity.IsVirtual(ArtistDevicePath, "Artist13.3pro", 0xE454, 0x1302));
        Assert.False(VirtualDisplayIdentity.IsVirtual(LaptopDevicePath, "", 0xAE0D, 0x1521));
    }

    [Fact]
    public void AnyOneFingerprintIsEnough()
    {
        // A driver that reports a different device path but the same
        // friendly name is still found, and vice versa.  That is what
        // makes the identification robust against a driver update.
        Assert.True(VirtualDisplayIdentity.IsVirtual("", "VDD by MTT", 0, 0));
        Assert.True(VirtualDisplayIdentity.IsVirtual(VirtualDevicePath, "", 0, 0));
        Assert.True(VirtualDisplayIdentity.IsVirtual("", "", 0x9436, 0x1337));
    }

    // -----------------------------------------------------------------
    // The thing that actually broke.
    // -----------------------------------------------------------------

    [Fact]
    public void NothingInTheIdentificationDependsOnAPosition()
    {
        // THIS IS THE REGRESSION TEST FOR THE USER'S BUG.
        //
        // The old locator fell back to "the monitor at negative
        // coordinates" when the EDID did not match.  That is a
        // POSITION, and a position is not an identity:
        //
        //   * the user rearranges the desktop, or Windows re-lays-out
        //     after a hot-plug, and the virtual display ends up
        //     somewhere else - which is EXACTLY what the user saw
        //     ("the VDD sat above the other displays");
        //   * and a physical monitor that happens to sit to the left
        //     of the origin would be mistaken for the virtual one,
        //     which would capture the wrong screen entirely.
        //
        // So the locator must NOT have a positional fallback.  The
        // only way to check that in a unit test is to assert that the
        // identification function has no positional input at all -
        // which is what the signature says.
        //
        // The compile-time check is the test: this method takes a
        // device path, a friendly name and two EDID ids, and nothing
        // else.  There is no coordinate to pass.

        // And the virtual display is found whatever its position.
        Assert.True(VirtualDisplayIdentity.IsVirtual(VirtualDevicePath, "VDD by MTT", 0x9436, 0x1337));
    }
}
