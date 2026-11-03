// SPDX-License-Identifier: MIT
//
// Tests for the panel's scale.
//
// The panel's angular size used to be hard-coded at 50 % of the
// glasses' field of view.  There was no way to change it: the value
// lived in the anchor service's default options and nowhere else, so
// the user could not make the panel bigger or smaller.
//
// These tests pin down the replacement:
//
//   * the range is 25 % to 100 %,
//   * the step is 5 %,
//   * the value is clamped and snapped wherever it is read,
//   * and the panel's size follows the fraction exactly.
//
// The last one is the one that matters: a slider that moves but a
// panel that does not is a broken setting, and it would look exactly
// like a rendering bug.

using RokidDiyStation.Core.Spatial;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class PanelScaleTests
{
    [Fact]
    public void The_Default_Is_Half_The_Field_Of_View()
    {
        // The default must not change: the panel looked right at 50 %
        // before the setting existed, and the new slider starts there.
        Assert.Equal(0.5f, PanelScale.DefaultFraction, 6);
        Assert.Equal(50.0, PanelScale.DefaultPercent, 6);
    }

    [Fact]
    public void The_Range_Covers_Quarter_To_Full()
    {
        // The user asked for 25 % to 100 %.
        Assert.Equal(25.0, PanelScale.MinPercent, 6);
        Assert.Equal(100.0, PanelScale.MaxPercent, 6);
    }

    [Fact]
    public void The_Step_Is_Five_Percent()
    {
        // The user asked for 5 % at a time.
        Assert.Equal(5.0, PanelScale.StepPercent, 6);
        Assert.Equal(0.05f, PanelScale.StepFraction, 6);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-1.0f)]
    [InlineData(0.1f)]
    [InlineData(1.5f)]
    [InlineData(1000.0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void An_Out_Of_Range_Fraction_Is_Clamped(float fraction)
    {
        // A hand-edited settings file must not be able to produce a
        // panel wider than the glasses can show, or one that is
        // impossibly small.
        var clamped = PanelScale.Clamp(fraction);

        Assert.InRange(clamped, PanelScale.MinFraction, PanelScale.MaxFraction);
        Assert.True(float.IsFinite(clamped));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    [InlineData(500.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_Out_Of_Range_Percentage_Is_Clamped(double percent)
    {
        var fraction = PanelScale.FromPercent(percent);

        Assert.InRange(fraction, PanelScale.MinFraction, PanelScale.MaxFraction);
        Assert.True(float.IsFinite(fraction));
    }

    [Theory]
    [InlineData(0.27f, 0.25f)]
    [InlineData(0.28f, 0.30f)]
    [InlineData(0.33f, 0.35f)]
    [InlineData(0.74f, 0.75f)]
    [InlineData(0.76f, 0.75f)]
    [InlineData(1.0f, 1.0f)]
    public void A_Fraction_Is_Snapped_To_The_Nearest_Step(float fraction, float expected)
    {
        // The slider snaps to its ticks, but a value that comes from
        // the settings file is not snapped - and the panel would then
        // sit at a size the slider cannot even represent.
        Assert.Equal(expected, PanelScale.Snap(fraction), 5);
    }

    [Fact]
    public void Every_Supported_Percentage_Is_Reachable_By_Snapping()
    {
        // The slider's ticks are 5 % apart from 25 % to 100 %, and
        // every one of them has to survive the round trip through the
        // fraction form unchanged.
        for (var percent = 25.0; percent <= 100.0; percent += 5.0)
        {
            var fraction = PanelScale.FromPercent(percent);

            Assert.Equal(percent, PanelScale.ToPercent(fraction), 4);
        }
    }

    [Fact]
    public void The_Panel_Grows_With_Its_Scale()
    {
        // This is the whole point of the setting: a bigger fraction has
        // to produce a bigger panel, or the slider does nothing.
        var distance = 2.0f;

        var small = RokidDisplayProfile.PanelWidth(distance, 0.25f);
        var medium = RokidDisplayProfile.PanelWidth(distance, 0.50f);
        var large = RokidDisplayProfile.PanelWidth(distance, 1.00f);

        Assert.True(small < medium, "a 25 % panel must be smaller than a 50 % one");
        Assert.True(medium < large, "a 50 % panel must be smaller than a 100 % one");
    }

    [Fact]
    public void The_Panel_Never_Exceeds_The_Field_Of_View()
    {
        // 100 % is the ceiling, and it has to be enforced in the
        // geometry itself: a panel wider than the field of view would
        // simply have its edges clipped off, and the user would be
        // looking at a cropped picture while believing they could see
        // all of it.
        var distance = 2.0f;
        var full = RokidDisplayProfile.PanelWidth(distance, 1.0f);

        // The panel's width is 2 * D * tan(theta_h / 2), and the field
        // of view at distance D spans 2 * D * tan(theta_h / 2) too.
        var fovWidth = 2f * distance
            * MathF.Tan(RokidDisplayProfile.HorizontalFovDegrees() * MathF.PI / 180f * 0.5f);

        Assert.True(full <= fovWidth * 1.0001f,
            $"a 100 % panel ({full:F4} m) must not be wider than the " +
            $"field of view ({fovWidth:F4} m)");

        // And a request for more than 100 % must be clamped, not
        // honoured.
        var tooBig = RokidDisplayProfile.PanelWidth(distance, 5.0f);
        Assert.Equal(full, tooBig, 5);
    }

    [Fact]
    public void The_Panel_Scale_Does_Not_Depend_On_The_Distance()
    {
        // The panel's angular size is the setting; the distance only
        // decides how BIG the panel is in metres, so that it subtends
        // the same angle from further away.  If the angle changed with
        // the distance, the setting would mean two different things
        // depending on where the panel was.
        var near = RokidDisplayProfile.PanelWidth(1.0f, 0.5f);
        var far = RokidDisplayProfile.PanelWidth(4.0f, 0.5f);

        var nearAngle = RokidDisplayProfile.SubtendedAngleDegrees(near, 1.0f);
        var farAngle = RokidDisplayProfile.SubtendedAngleDegrees(far, 4.0f);

        Assert.Equal(nearAngle, farAngle, 3);
    }

    [Fact]
    public void The_Percentage_Wins_When_It_Differs_From_The_Default()
    {
        // The settings file carries the scale in two forms.  The
        // percentage is what the user chose on the slider, so it is
        // the one that counts.
        var fraction = PanelScale.Resolve(0.5f, 70.0);

        Assert.Equal(0.70f, fraction, 5);
    }

    [Fact]
    public void The_Fraction_Is_Honoured_For_An_Older_Settings_File()
    {
        // An old settings.json has no percentage at all.  The fraction
        // it does have has to be used.
        var fraction = PanelScale.Resolve(0.65f, null);

        Assert.Equal(0.65f, fraction, 5);
    }

    [Fact]
    public void The_Default_Is_Used_When_Neither_Form_Says_Otherwise()
    {
        Assert.Equal(PanelScale.DefaultFraction, PanelScale.Resolve(null, null), 5);
        Assert.Equal(PanelScale.DefaultFraction, PanelScale.Resolve(0.5f, 50.0), 5);
    }

    [Fact]
    public void A_Resolved_Fraction_Is_Always_A_Supported_Step()
    {
        // Whatever the settings file says, the panel must end up at a
        // size the slider can represent.
        foreach (var (fraction, percent) in new[]
        {
            (0.37f, (double?)null),
            (0.37f, 37.0),
            (2.0f, (double?)null),
            (0.0f, 0.0),
        })
        {
            var resolved = PanelScale.Resolve(fraction, percent);

            var steps = (resolved - PanelScale.MinFraction) / PanelScale.StepFraction;
            Assert.True(MathF.Abs(steps - MathF.Round(steps)) < 1e-3f,
                $"{resolved} is not on a 5 % step");
            Assert.InRange(resolved, PanelScale.MinFraction, PanelScale.MaxFraction);
        }
    }
}
