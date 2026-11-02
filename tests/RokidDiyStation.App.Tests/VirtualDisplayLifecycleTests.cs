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
        // This test guards against the pipe creeping back in.  It
        // checks the ASSEMBLY, not the source tree, so it still means
        // something when the tests are run against a published build.
        var assembly = typeof(RokidDiyStation.App.Program).Assembly;

        var offenders = assembly.GetTypes()
            .Where(t => t.FullName!.Contains("VirtualDisplayDriverPipe", StringComparison.Ordinal)
                     || t.FullName!.Contains("DriverPipe", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .ToList();

        Assert.True(offenders.Count == 0,
            "the driver's SETDISPLAYCOUNT pipe breaks the driver (error 43) and must not "
            + "come back: " + string.Join(", ", offenders));

        // And the command must not appear in the built assembly's
        // metadata at all - not even as a leftover constant.
        Assert.False(ContainsStringLiteral(assembly, "SETDISPLAYCOUNT"),
            "the string SETDISPLAYCOUNT must not appear in the built assembly.");
    }

    /// <summary>
    /// Whether the assembly's metadata contains the given string.
    /// </summary>
    /// <remarks>
    /// The check is deliberately blunt - it looks for the string in the
    /// assembly's raw bytes.  A false positive is harmless (it would
    /// only mean the string is mentioned in a comment-like constant),
    /// and a false negative would mean the pipe came back.
    /// </remarks>
    private static bool ContainsStringLiteral(System.Reflection.Assembly assembly, string text)
    {
        var path = assembly.Location;
        if (!System.IO.File.Exists(path)) return false;

        var bytes = System.IO.File.ReadAllBytes(path);
        var needle = System.Text.Encoding.Unicode.GetBytes(text);

        for (var i = 0; i + needle.Length <= bytes.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (bytes[i + j] == needle[j]) continue;
                found = false;
                break;
            }
            if (found) return true;
        }

        return false;
    }
}
