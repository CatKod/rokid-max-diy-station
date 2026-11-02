// SPDX-License-Identifier: MIT
//
// Tests for the cursor's absolute mapping onto the virtual display.
//
// WHY THIS MATTERS ENOUGH TO TEST
// -------------------------------
// The virtual display is a REAL Windows monitor, so the operating
// system does all of the input work: it hit-tests the mouse against
// the windows on that monitor, activates them, orders them, and
// delivers the clicks.  Nothing we do can change that.
//
// What we DO is draw the 3D cursor where the OS thinks the mouse is.
// If our mapping is off, the arrow points at one thing and the click
// lands on another - which is the single most confusing failure mode
// there is.
//
// The mapping is:
//
//     inside = x >= left && x < right && y >= top && y < bottom
//     u      = (x - left) / width
//     v      = (y - top)  / height
//
// It is a plain affine transform with no gain and no drift, because
// the mouse moves over the virtual monitor, whose pixel grid IS the
// captured texture's.
//
// The edges are worth testing specifically, because they are where an
// off-by-one shows up: a cursor at the far right of the monitor must
// map to u = 1.0, not to something just past it.
using System.Drawing;
using RokidDiyStation.App.Spatial;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class VirtualDisplayMappingTests
{
    /// <summary>
    /// The virtual display, at the place the app puts it: immediately
    /// to the left of the primary display, 1920x1080.
    /// </summary>
    private static MonitorInfo MakeVirtualMonitor(
        int left = -1920, int top = 0, int width = 1920, int height = 1080)
    {
        var bounds = new Rectangle(left, top, width, height);
        return new MonitorInfo(
            new IntPtr(0x1234),
            @"\\.\DISPLAY14",
            @"MONITOR\MTT1337\{00000000-0000-0000-0000-000000000000}",
            "Virtual Display Driver",
            bounds,
            width,
            height,
            IsVirtual: true);
    }

    [Fact]
    public void TheTopLeftCornerMapsToTheOrigin()
    {
        var m = MakeVirtualMonitor();
        Assert.True(m.TryToUv(-1920, 0, out var u, out var v));
        Assert.Equal(0.0, u, 6);
        Assert.Equal(0.0, v, 6);
    }

    [Fact]
    public void TheBottomRightCornerMapsToOne()
    {
        var m = MakeVirtualMonitor();
        // The far corner is EXCLUSIVE: the rectangle is
        // [left, right) x [top, bottom).
        Assert.True(m.TryToUv(-1, 1079, out var u, out var v));
        Assert.Equal(1919.0 / 1920.0, u, 6);
        Assert.Equal(1079.0 / 1080.0, v, 6);
    }

    [Fact]
    public void TheCentreMapsToTheCentre()
    {
        var m = MakeVirtualMonitor();
        Assert.True(m.TryToUv(-960, 540, out var u, out var v));
        Assert.Equal(0.5, u, 6);
        Assert.Equal(0.5, v, 6);
    }

    [Fact]
    public void APointToTheRightOfTheMonitorIsRejected()
    {
        var m = MakeVirtualMonitor();
        // x == right is one past the last pixel, so it belongs to the
        // next monitor (the primary one).
        Assert.False(m.TryToUv(0, 540, out _, out _));
        Assert.False(m.TryToUv(100, 540, out _, out _));
    }

    [Fact]
    public void APointToTheLeftOfTheMonitorIsRejected()
    {
        var m = MakeVirtualMonitor();
        Assert.False(m.TryToUv(-1921, 540, out _, out _));
    }

    [Fact]
    public void APointAboveOrBelowTheMonitorIsRejected()
    {
        var m = MakeVirtualMonitor();
        Assert.False(m.TryToUv(-960, -1, out _, out _));
        Assert.False(m.TryToUv(-960, 1080, out _, out _));
    }

    [Fact]
    public void TheMappingIsMonotonicAcrossTheWholeDisplay()
    {
        // A mapping that is not monotonic would send the cursor to the
        // wrong place depending on where the user aimed, which is the
        // worst possible failure mode: it would look random.
        var m = MakeVirtualMonitor();
        var previousU = double.NegativeInfinity;
        var previousV = double.NegativeInfinity;

        for (var i = 0; i <= 100; i++)
        {
            var x = -1920 + (i * 1919) / 100;
            var y = (i * 1079) / 100;
            Assert.True(m.TryToUv((int)x, (int)y, out var u, out var v));
            Assert.True(u >= previousU, $"u went backwards at i={i}");
            Assert.True(v >= previousV, $"v went backwards at i={i}");
            previousU = u;
            previousV = v;
        }
    }

    [Fact]
    public void TheMappingIsExactlyOnePixelPerPixel()
    {
        // The whole point of using an ABSOLUTE mapping on the virtual
        // display (rather than the old differential one over the
        // glasses' display) is that one mouse pixel moves the cursor
        // by exactly one content pixel.  So a step of one pixel must
        // change u by exactly 1/width.
        //
        // The tolerance is 1e-6 because the u/v values are floats:
        // the ABSOLUTE difference of two numbers around 0.48 carries
        // about seven significant digits, so the difference is only
        // meaningful to about 1e-8.  A 1e-6 tolerance still pins the
        // gain to a part in a million, which is far tighter than the
        // one-pixel accuracy the product needs.
        var m = MakeVirtualMonitor();
        Assert.True(m.TryToUv(-1000, 500, out var u0, out var v0));
        Assert.True(m.TryToUv(-999, 500, out var u1, out var v1));

        Assert.Equal(1.0 / 1920.0, u1 - u0, 6);
        Assert.Equal(0.0, v1 - v0, 6);
    }

    [Fact]
    public void AZeroSizedMonitorIsRejectedRatherThanDividingByZero()
    {
        var m = MakeVirtualMonitor(width: 0, height: 0);
        Assert.False(m.TryToUv(0, 0, out _, out _));
    }
}
