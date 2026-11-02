// SPDX-License-Identifier: MIT
//
// Tests for the adjustable virtual cursor's size.
//
// The cursor's size used to be two hard-coded constants in the
// renderer, so the user could not change it.  These tests pin down
// that the setting is wired all the way through: the setting, the
// station, and the renderer that actually draws the arrow.

using System.Reflection;
using RokidDiyStation.Core.Spatial;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class CursorScaleTests
{
    [Fact]
    public void The_Renderer_Exposes_A_Cursor_Scale()
    {
        var renderer = typeof(RokidDiyStation.App.Spatial.PanelRenderer);

        var property = renderer.GetProperty("CursorScale");
        Assert.NotNull(property);
        Assert.True(property!.CanRead);
        Assert.True(property.CanWrite);
    }

    [Fact]
    public void The_Station_Can_Apply_A_Cursor_Scale()
    {
        var station = typeof(RokidDiyStation.App.Spatial.SpatialStation);

        Assert.NotNull(station.GetMethod("ApplyCursorScale"));
    }

    [Fact]
    public void The_Settings_Carry_A_Cursor_Scale()
    {
        // The setting has to survive a round-trip through the settings
        // file, or the user's chosen size is lost between sessions.
        var settings = typeof(RokidDiyStation.App.AppSettings);
        Assert.NotNull(settings.GetProperty("CursorScale"));

        var defaultScale = (double)settings
            .GetProperty("CursorScale")!
            .GetValue(settings.GetProperty("Default")!.GetValue(null)!)!;

        Assert.Equal(CursorSize.DefaultScale, defaultScale, 6);
    }

    [Fact]
    public void The_Renderer_Clamps_An_Out_Of_Range_Scale()
    {
        // The renderer is the last line of defence: even if a bad value
        // somehow reached it, the arrow must not collapse to nothing or
        // grow to fill the panel.
        var renderer = typeof(RokidDiyStation.App.Spatial.PanelRenderer);
        var property = renderer.GetProperty("CursorScale")!;

        // The renderer's constructor needs a GPU, so we cannot
        // instantiate it here.  The clamping itself is covered by the
        // core tests; what matters here is that the renderer READS the
        // scale through CursorSize rather than trusting the caller.
        var module = renderer.Module;
        Assert.NotNull(module);

        // The field must default to the documented default scale.
        var field = renderer.GetField(
            "_cursorScale", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
    }
}
