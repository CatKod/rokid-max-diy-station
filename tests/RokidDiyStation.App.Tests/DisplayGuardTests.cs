// SPDX-License-Identifier: MIT
//
// Tests for the display guard — the thing that stops the panel from
// blinding the user when the glasses are unplugged.
//
// The failure this guards against is Windows' behaviour, not ours:
// when a display disappears, DWM relocates every window on it to
// (0, 0) on the primary display.  Our panel is a borderless,
// topmost, 1920x1080 popup, so that relocation puts a black
// rectangle over the user's entire laptop screen that they cannot
// click through, Alt+Tab away, or dismiss.
//
// The guard's decision logic is a pure function so it can be tested
// without a GPU, a window, or a set of glasses.

using RokidDiyStation.App.Spatial;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class DisplayGuardTests
{
    // Fully qualified: there is a second, unrelated NativeMethods
    // class in the RokidDiyStation.App namespace (the hotkey one).
    private static RokidDiyStation.App.Spatial.NativeMethods.RECT Rect(int x, int y, int w, int h)
        => new() { left = x, top = y, right = x + w, bottom = y + h };

    [Fact]
    public void The_Glasses_Being_Gone_Means_Hide()
    {
        // The glasses are unplugged: there is no glasses' rectangle
        // at all.
        var decision = DisplayGuardLogic.Evaluate(
            windowBounds: Rect(1920, 0, 1920, 1080),
            onAMonitor: false,
            glassesBounds: null,
            onTheGlasses: false);

        Assert.True(decision.ShouldHide,
            "the panel must be hidden when the Rokid Max is unplugged");
    }

    [Fact]
    public void A_Vanished_Monitor_Means_Hide()
    {
        // The glasses' rectangle is still known (the layout has not
        // been re-enumerated yet), but the window's monitor is gone.
        // DWM is about to relocate the window — hide it first.
        var decision = DisplayGuardLogic.Evaluate(
            windowBounds: Rect(1920, 0, 1920, 1080),
            onAMonitor: false,
            glassesBounds: Rect(1920, 0, 1920, 1080),
            onTheGlasses: false);

        Assert.True(decision.ShouldHide,
            "the panel must be hidden when its monitor disappears");
    }

    [Fact]
    public void A_Window_On_The_Primary_Display_Means_Hide()
    {
        // The dangerous end state: the window has already been
        // relocated to (0, 0) on the primary display, which is exactly
        // where the glasses' rectangle is NOT.
        var decision = DisplayGuardLogic.Evaluate(
            windowBounds: Rect(0, 0, 1920, 1080),
            onAMonitor: true,
            glassesBounds: Rect(1920, 0, 1920, 1080),
            onTheGlasses: false);

        Assert.True(decision.ShouldHide,
            "the panel must be hidden when it has been moved off the Rokid Max's display");
    }

    [Fact]
    public void A_Window_On_The_Glasses_Means_Keep()
    {
        var decision = DisplayGuardLogic.Evaluate(
            windowBounds: Rect(1920, 0, 1920, 1080),
            onAMonitor: true,
            glassesBounds: Rect(1920, 0, 1920, 1080),
            onTheGlasses: true);

        Assert.False(decision.ShouldHide,
            "the panel must stay visible when it is on the Rokid Max's display");
    }

    [Fact]
    public void A_Window_Overlapping_The_Glasses_Means_Keep()
    {
        // The monitor could not be identified by fingerprint, but the
        // window is geometrically over the glasses.  Keep it.
        var decision = DisplayGuardLogic.Evaluate(
            windowBounds: Rect(1920, 0, 1920, 1080),
            onAMonitor: true,
            glassesBounds: Rect(1920, 0, 1920, 1080),
            onTheGlasses: false);

        Assert.False(decision.ShouldHide,
            "the panel must stay visible when it is over the Rokid Max's display");
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080, false)]
    [InlineData(1920, 1080, 1920, 1080, false)]
    [InlineData(3840, 0, 1920, 1080, false)]
    public void Non_Overlapping_Rectangles_Do_Not_Overlap(int x, int y, int w, int h, bool expected)
    {
        var a = Rect(x, y, w, h);
        var b = Rect(1920, 0, 1920, 1080);
        Assert.Equal(expected, DisplayGuardLogic.Overlaps(a, b));
    }

    [Fact]
    public void Overlapping_Rectangles_Overlap()
    {
        var a = Rect(1920, 0, 1920, 1080);
        var b = Rect(1920, 0, 1920, 1080);
        Assert.True(DisplayGuardLogic.Overlaps(a, b));
    }

    [Fact]
    public void A_Two_Pixel_Overlap_Is_Not_Enough()
    {
        // The two APIs can disagree by a pixel or two because of DPI
        // rounding, so a sliver of overlap must not count as "on the
        // glasses" — otherwise the guard would flap.
        //
        // The window starts 2 px before the glasses' right edge, so
        // only a 2 px sliver of the two rectangles coincides.
        var glasses = Rect(1920, 0, 1920, 1080);   // 1920..3840
        var window = Rect(3838, 0, 1920, 1080);    // 3838..5758, overlaps by 2 px
        Assert.False(DisplayGuardLogic.Overlaps(window, glasses));
    }
}
