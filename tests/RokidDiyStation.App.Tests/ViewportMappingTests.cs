// SPDX-License-Identifier: MIT
//
// Tests for the capture-to-viewport coordinate mapping.
//
// WHY THIS MATTERS ENOUGH TO TEST
// -------------------------------
// The click the user makes in the glasses has to land on the EXACT
// element they pointed at.  The whole chain is a series of coordinate
// conversions:
//
//   the cursor's (u, v) in the captured image
//     -> the browser's viewport rectangle inside that image
//     -> CSS pixels
//     -> a CDP Input.dispatchMouseEvent
//
// Every step is a chance to be off by a few pixels, and a few pixels
// is the difference between clicking a button and clicking the gap
// next to it.  So the arithmetic is checked here, against hand-
// computed numbers, rather than discovered on hardware.
//
// The geometry modelled here is the awkward one: a window with DWM's
// invisible resize border, so GetWindowRect and the visible frame
// differ, and the capture matches the VISIBLE frame rather than the
// window rect.
using RokidDiyStation.App.Input;
using RokidDiyStation.App.Spatial;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class ViewportMappingTests
{
    /// <summary>
    /// A window at (1920, 0), 1600x900 visible, with a client area
    /// that starts 0 pixels in (the --app mode case: no caption bar).
    /// </summary>
    private static BrowserWindowGeometry MakeGeometry(
        int captureWidth = 1600,
        int captureHeight = 900)
    {
        // GetWindowRect includes the invisible resize border, so it is
        // 8 pixels wider on each side than the visible frame.
        var windowRect = new NativeMethods.RECT
        {
            left = 1912,
            top = -8,
            right = 3512 + 8,
            bottom = 892 + 8,
        };

        var frameRect = new NativeMethods.RECT
        {
            left = 1920,
            top = 0,
            right = 1920 + captureWidth,
            bottom = captureHeight,
        };

        var clientRect = new NativeMethods.RECT
        {
            left = 1920,
            top = 0,
            right = 1920 + captureWidth,
            bottom = captureHeight,
        };

        return new BrowserWindowGeometry
        {
            Handle = new IntPtr(0x1234),
            CaptureWidth = captureWidth,
            CaptureHeight = captureHeight,
            DevicePixelRatio = 1.0,
            WindowRect = windowRect,
            FrameRect = frameRect,
            ClientRect = clientRect,
        };
    }

    [Fact]
    public void TheCaptureMatchesTheVisibleFrameNotTheWindowRect()
    {
        // The window rect is 1608x908 because of the invisible border;
        // the visible frame is 1600x900.  The mapping must pick the
        // frame, because that is what WGC captures.
        var geometry = MakeGeometry();
        var viewport = geometry.ComputeViewport(1600, 900);

        Assert.Equal(0, viewport.Left);
        Assert.Equal(0, viewport.Top);
        Assert.Equal(1600, viewport.Width);
        Assert.Equal(900, viewport.Height);
    }

    [Fact]
    public void TheTopLeftCornerMapsToThePagesOrigin()
    {
        var geometry = MakeGeometry();
        var viewport = geometry.ComputeViewport(1600, 900);

        Assert.True(viewport.ToCss(0f, 0f, 1.0, out var x, out var y));
        Assert.Equal(0.0, x, 3);
        Assert.Equal(0.0, y, 3);
    }

    [Fact]
    public void TheBottomRightCornerMapsToThePagesFarEdge()
    {
        var geometry = MakeGeometry();
        var viewport = geometry.ComputeViewport(1600, 900);

        Assert.True(viewport.ToCss(1f, 1f, 1.0, out var x, out var y));
        Assert.Equal(1600.0, x, 3);
        Assert.Equal(900.0, y, 3);
    }

    [Fact]
    public void TheCentreMapsToTheCentreOfThePage()
    {
        var geometry = MakeGeometry();
        var viewport = geometry.ComputeViewport(1600, 900);

        Assert.True(viewport.ToCss(0.5f, 0.5f, 1.0, out var x, out var y));
        Assert.Equal(800.0, x, 3);
        Assert.Equal(450.0, y, 3);
    }

    [Fact]
    public void TheDevicePixelRatioDividesTheCssCoordinates()
    {
        // On a 200% display the page's CSS viewport is half the
        // captured size, so the same texture coordinate has to map to
        // half as many CSS pixels.
        var geometry = MakeGeometry();
        geometry.DevicePixelRatio = 2.0;

        var viewport = geometry.ComputeViewport(800, 450);

        Assert.True(viewport.ToCss(1f, 1f, 2.0, out var x, out var y));
        Assert.Equal(800.0, x, 3);
        Assert.Equal(450.0, y, 3);
    }

    [Fact]
    public void AViewportOffsetByACaptionBarIsAccountedFor()
    {
        // A window with a 30-pixel title bar: the client area starts
        // 30 pixels below the frame's top, so the page's origin is
        // 30 pixels down inside the capture.
        var geometry = MakeGeometry();
        geometry.ClientRect = new NativeMethods.RECT
        {
            left = 1920,
            top = 30,
            right = 3520,
            bottom = 900,
        };

        var viewport = geometry.ComputeViewport(1600, 870);

        Assert.Equal(30, viewport.Top);

        // The page's top-left is 30 pixels down in the capture.
        Assert.True(viewport.ToCss(0f, 0f, 1.0, out var x, out var y));
        Assert.Equal(0.0, x, 3);
        Assert.Equal(30.0, y, 3);
    }

    [Fact]
    public void AnEmptyViewportIsRejectedRatherThanProducingNonsense()
    {
        var viewport = new ViewportRect(0, 0, 0, 0);
        Assert.False(viewport.ToCss(0.5f, 0.5f, 1.0, out var x, out var y));
        Assert.Equal(0.0, x);
        Assert.Equal(0.0, y);
    }

    [Fact]
    public void TheMappingIsMonotonicAcrossTheWholePanel()
    {
        // A mapping that is not monotonic would send a click to the
        // wrong place depending on where on the panel the user aimed,
        // which is the worst possible failure mode: it would look
        // random.
        var geometry = MakeGeometry();
        var viewport = geometry.ComputeViewport(1600, 900);

        var previousX = double.NegativeInfinity;
        var previousY = double.NegativeInfinity;

        for (var i = 0; i <= 100; i++)
        {
            var u = i / 100f;
            Assert.True(viewport.ToCss(u, u, 1.0, out var x, out var y));
            Assert.True(x > previousX, $"x went backwards at u={u}");
            Assert.True(y > previousY, $"y went backwards at u={u}");
            previousX = x;
            previousY = y;
        }
    }

    [Fact]
    public void TheModiferBitmaskMatchesTheProtocol()
    {
        // CDP's modifier bits: Alt = 1, Ctrl = 2, Meta = 4, Shift = 8.
        Assert.Equal(0, Input.CdpClient.ModifierMask(false, false, false, false));
        Assert.Equal(1, Input.CdpClient.ModifierMask(true, false, false, false));
        Assert.Equal(2, Input.CdpClient.ModifierMask(false, true, false, false));
        Assert.Equal(8, Input.CdpClient.ModifierMask(false, false, false, true));
        Assert.Equal(3, Input.CdpClient.ModifierMask(true, true, false, false));
        Assert.Equal(11, Input.CdpClient.ModifierMask(true, true, false, true));
    }
}
