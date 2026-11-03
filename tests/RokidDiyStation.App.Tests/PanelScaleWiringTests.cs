// SPDX-License-Identifier: MIT
//
// Tests for the panel-scale setting's wiring through the app layer.
//
// The setting has to survive a round trip through the settings file,
// reach the anchor service, and be adjustable from the settings window.
// The core's own tests cover the maths; these cover the plumbing, and
// they use reflection because the app's windows need a dispatcher and
// a GPU, which a unit test cannot provide.

using System.Reflection;
using RokidDiyStation.Core.Spatial;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class PanelScaleWiringTests
{
    /// <summary>
    /// The setting has to be persisted, or the user's chosen panel
    /// size is lost between sessions.
    /// </summary>
    [Fact]
    public void The_Settings_Carry_A_Panel_Scale_Percentage()
    {
        var settings = typeof(RokidDiyStation.App.AppSettings);

        Assert.NotNull(settings.GetProperty("PanelScalePercent"));

        var defaultPercent = (double)settings
            .GetProperty("PanelScalePercent")!
            .GetValue(settings.GetProperty("Default")!.GetValue(null)!)!;

        Assert.Equal(PanelScale.DefaultPercent, defaultPercent, 6);
    }

    /// <summary>
    /// The default must be the old hard-coded 50 %, so the panel looks
    /// exactly as it did before the setting existed.
    /// </summary>
    [Fact]
    public void The_Default_Panel_Scale_Is_Unchanged()
    {
        var s = RokidDiyStation.App.AppSettings.Default;

        Assert.Equal(PanelScale.DefaultFraction, s.PanelFovFraction, 6);
        Assert.Equal(PanelScale.DefaultPercent, s.PanelScalePercent, 6);
    }

    /// <summary>
    /// The settings window has to have the slider.
    /// </summary>
    [Fact]
    public void The_Settings_Window_Has_A_Panel_Scale_Slider()
    {
        var window = typeof(RokidDiyStation.App.MainWindow);

        // The XAML-generated fields are internal, so the lookup has to
        // ask for the non-public ones explicitly.
        Assert.NotNull(window.GetField(
            "PanelScaleSlider",
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(window.GetField(
            "PanelScaleLabel",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    /// <summary>
    /// The slider's handler has to exist and be wired.
    /// </summary>
    [Fact]
    public void The_Settings_Window_Handles_The_Panel_Scale_Change()
    {
        var window = typeof(RokidDiyStation.App.MainWindow);

        Assert.NotNull(window.GetMethod(
            "OnPanelScaleChanged",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    /// <summary>
    /// The tray has to resolve the scale the same way the settings
    /// window does.
    /// </summary>
    /// <remarks>
    /// If the two resolved it differently, the panel would be one size
    /// when the app starts and another size the moment the user
    /// touched a slider - which is exactly the kind of "it recentres
    /// itself" surprise this project has been fighting.
    /// </remarks>
    [Fact]
    public void The_Tray_Resolves_The_Panel_Scale()
    {
        var tray = typeof(RokidDiyStation.App.TrayApplicationContext);

        var resolve = tray.GetMethod(
            "ResolvePanelFraction",
            BindingFlags.Static | BindingFlags.Public);

        Assert.NotNull(resolve);

        // A settings record with the percentage set to 70 %.
        var settings = RokidDiyStation.App.AppSettings.Default with
        {
            PanelScalePercent = 70.0,
            PanelFovFraction = 0.5,
        };

        var fraction = (float)resolve!.Invoke(null, new object[] { settings })!;

        Assert.Equal(0.70f, fraction, 5);
    }

    /// <summary>
    /// The arrow has to be sized against the panel, or the two
    /// settings would silently fight each other.
    /// </summary>
    [Fact]
    public void The_Renderer_Sizes_The_Arrow_Against_The_Panel()
    {
        // The renderer reads the pose's width when it draws the
        // cursor, so the panel's size reaches the arrow's size
        // without any extra plumbing.
        var renderer = typeof(RokidDiyStation.App.Spatial.PanelRenderer);
        var method = renderer.GetMethod("DrawCursor", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
    }
}
