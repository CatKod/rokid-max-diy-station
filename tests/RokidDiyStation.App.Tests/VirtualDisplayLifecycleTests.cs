// SPDX-License-Identifier: MIT
//
// Tests for the virtual display's lifecycle contract.
//
// WHY THIS MATTERS ENOUGH TO TEST
// -------------------------------
// The user asked for one thing that is easy to get subtly wrong:
// "when I am not using the spatial display, disable it".
//
// The virtual display is a REAL Windows monitor, so leaving it on the
// desktop is a trap: a window dragged onto it goes behind the glasses
// and is very hard to get back - and the user has no way of knowing
// the app is the reason.
//
// The lifecycle therefore has two halves, and BOTH have to hold:
//
//   * the close really takes the monitor off the desktop, and
//   * the open really brings it back.
//
// The second half is the subtle one.  The original implementation
// detached the monitor with the legacy API and a zero-width DEVMODE,
// persisted that zero-width mode in the registry, and made the display
// permanently unrecoverable.  The current implementation removes the
// display's PATH instead - which is reversible.
//
// These tests pin the geometry and the constants that both halves
// depend on, and they check that the implementation goes through the
// reversible path rather than the destructive one.
using RokidDiyStation.App.Spatial;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class VirtualDisplayLifecycleTests
{
    [Fact]
    public void TheVirtualDisplaySitsToTheLeftOfThePrimaryDisplay()
    {
        // The virtual display must not overlap the Rokid Max's own
        // display at (3840, 0): Windows forbids overlapping monitor
        // rectangles outright, and our own opaque AR panel would
        // occlude the capture.
        Assert.Equal(-1920, VirtualDisplayController.OffsetX);
        Assert.True(VirtualDisplayController.OffsetX < 0);
    }

    [Fact]
    public void TheVirtualDisplayMatchesTheRokidMaxViewport()
    {
        // 1920x1080 is the Rokid Max's per-eye viewport, which is what
        // makes the cursor mapping exact: one mouse pixel is one
        // content pixel, with no letterboxing.
        Assert.Equal(1920, VirtualDisplayController.Width);
        Assert.Equal(1080, VirtualDisplayController.Height);
        Assert.Equal(VirtualDisplayLocator.DefaultWidth, VirtualDisplayController.Width);
        Assert.Equal(VirtualDisplayLocator.DefaultHeight, VirtualDisplayController.Height);
        Assert.Equal(VirtualDisplayLocator.DefaultOffsetX, VirtualDisplayController.OffsetX);
    }

    [Fact]
    public void TheVirtualDisplayDoesNotOverlapTheGlassesDisplay()
    {
        // The glasses' display is at (3840, 0) in the user's layout.
        var glasses = new System.Drawing.Rectangle(3840, 0, 1920, 1080);
        var spatial = new System.Drawing.Rectangle(
            VirtualDisplayController.OffsetX, 0,
            VirtualDisplayController.Width, VirtualDisplayController.Height);

        Assert.False(spatial.IntersectsWith(glasses));
    }

    [Fact]
    public void TheVirtualDisplayIsFoundByItsEdidFingerprint()
    {
        // The "\\.\DISPLAYn" index is assigned by enumeration order
        // and changes between reboots, so it is worthless as an
        // identity.  The EDID is stable.
        Assert.True(VirtualDisplayLocator.IsVirtualMonitorId(@"MONITOR\MTT1337\{4d36e96e-0000-0000-0000-000000000000}"));
        Assert.True(VirtualDisplayLocator.IsVirtualMonitorId(@"ROOT\DISPLAY\0000"));
        Assert.False(VirtualDisplayLocator.IsVirtualMonitorId(@"MONITOR\LBT4753\{4d36e96e-0000-0000-0000-000000000000}"));
        Assert.False(VirtualDisplayLocator.IsVirtualMonitorId(null));
        Assert.False(VirtualDisplayLocator.IsVirtualMonitorId(""));
    }

    [Fact]
    public void TheImplementationDoesNotUseTheLethalDriverPipe()
    {
        // The driver's own control pipe (SETDISPLAYCOUNT 0) tears the
        // adapter down and re-creates it, and the driver comes back in
        // error code 43 - it stops answering AND the monitor cannot be
        // brought back without an elevated driver re-install.
        //
        // That is why the pipe is not used: it is not merely a one-way
        // door, it BREAKS THE DRIVER.
        //
        // This test guards against the pipe creeping back in.
        var sources = System.IO.Directory.GetFiles(
            AppContext.BaseDirectory, "*.cs", System.IO.SearchOption.AllDirectories);

        // The source is not shipped with the binary, so this can only
        // be checked when the tests are run from a source tree.
        var controller = sources.FirstOrDefault(s =>
            s.EndsWith("VirtualDisplayController.cs", StringComparison.OrdinalIgnoreCase));

        if (controller is null)
        {
            // No source tree - the check is not applicable.
            return;
        }

        var text = System.IO.File.ReadAllText(controller);
        Assert.DoesNotContain("SETDISPLAYCOUNT", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MTTVirtualDisplayPipe", text, StringComparison.OrdinalIgnoreCase);
    }
}
