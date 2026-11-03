// SPDX-License-Identifier: MIT
//
// Tests for the virtual cursor's size.
//
// The user asked for the size of the rendered 3D arrow to be
// adjustable.  That setting used to be impossible to honour, because
// the cursor's size was two hard-coded constants in the renderer:
//
//     CursorWidthMetres  = 0.020f
//     CursorHeightMetres = 0.026f
//
// These tests pin down the behaviour of the replacement: a scale
// factor, clamped into a sane range, applied uniformly so the arrow is
// never stretched.

using RokidDiyStation.Core.Spatial;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class CursorSizeTests
{
    [Fact]
    public void The_Default_Scale_Reproduces_The_Default_Arrow()
    {
        var (w, h) = CursorSize.SizeMetres(CursorSize.DefaultScale);

        Assert.Equal(CursorSize.DefaultWidthMetres, w, 6);
        Assert.Equal(CursorSize.DefaultHeightMetres, h, 6);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    [InlineData(2.0f)]
    [InlineData(3.75f)]
    public void The_Scale_Is_Applied_Uniformly(float scale)
    {
        // Uniform means the arrow's ASPECT RATIO never changes: it is
        // scaled, not stretched.  A stretched arrow would look wrong in
        // a way that is hard to describe and easy to miss.
        var (w, h) = CursorSize.SizeMetres(scale);

        Assert.Equal(CursorSize.DefaultWidthMetres * scale, w, 6);
        Assert.Equal(CursorSize.DefaultHeightMetres * scale, h, 6);

        var expectedAspect = CursorSize.DefaultWidthMetres / CursorSize.DefaultHeightMetres;
        Assert.Equal(expectedAspect, w / h, 5);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-1.0f)]
    [InlineData(0.01f)]
    [InlineData(1000.0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void An_Out_Of_Range_Scale_Is_Clamped(float scale)
    {
        // A corrupt or hand-edited settings file must not be able to
        // produce a zero-sized cursor (which would make the arrow
        // invisible and the panel unusable) or an absurd one.
        var clamped = CursorSize.Clamp(scale);

        Assert.InRange(clamped, CursorSize.MinScale, CursorSize.MaxScale);
        Assert.True(float.IsFinite(clamped));
    }

    [Fact]
    public void The_Clamped_Scale_Still_Produces_A_Usable_Arrow()
    {
        var (w, h) = CursorSize.SizeMetres(0.0f);

        Assert.True(w > 0f, "the cursor must never collapse to zero width");
        Assert.True(h > 0f, "the cursor must never collapse to zero height");
    }

    [Fact]
    public void The_Half_Extents_Are_Half_The_Size()
    {
        // The renderer builds the quad from half-extents along the
        // viewer's right axis and the panel's up axis.
        var (halfW, halfH) = CursorSize.HalfExtents(2.0f);

        Assert.Equal(CursorSize.DefaultWidthMetres * 2.0f * 0.5f, halfW.X, 6);
        Assert.Equal(CursorSize.DefaultHeightMetres * 2.0f * 0.5f, halfH.Y, 6);
        Assert.Equal(0f, halfW.Y, 6);
        Assert.Equal(0f, halfW.Z, 6);
        Assert.Equal(0f, halfH.X, 6);
        Assert.Equal(0f, halfH.Z, 6);
    }

    [Fact]
    public void The_Supported_Range_Is_Wide_Enough_To_Be_Useful()
    {
        // The user asked for this feature, so the range has to actually
        // cover the difference between "tiny precise pointer" and "big
        // obvious arrow".  A quarter-scale arrow is barely visible; a
        // four-times arrow is a quarter of the panel's height.
        Assert.True(CursorSize.MinScale <= 0.5f,
            "the user must be able to make the arrow noticeably smaller");
        Assert.True(CursorSize.MaxScale >= 3.0f,
            "the user must be able to make the arrow noticeably bigger");
    }

    // -----------------------------------------------------------------
    // The arrow is sized against the PANEL, not against a fixed size
    // in metres.
    //
    // The panel's scale is a user setting (25 % to 100 % of the field
    // of view).  If the arrow had a fixed size in metres, changing that
    // setting would silently change the arrow's size RELATIVE to the
    // content, and the two settings would fight each other: the user
    // would have to re-tune the cursor size every time they changed
    // the panel's scale.
    // -----------------------------------------------------------------

    [Fact]
    public void The_Default_Arrow_Is_The_Same_On_The_Reference_Panel()
    {
        // The reference panel is the one the arrow's constants were
        // measured against, so the default appearance must not change
        // at all.
        var (w, h) = CursorSize.SizeMetres(
            CursorSize.DefaultScale, CursorSize.ReferencePanelWidthMetres);

        Assert.Equal(CursorSize.DefaultWidthMetres, w, 6);
        Assert.Equal(CursorSize.DefaultHeightMetres, h, 6);
    }

    [Fact]
    public void The_Arrow_Keeps_Its_Size_Relative_To_The_Content()
    {
        // The whole point: doubling the panel doubles the arrow, so the
        // arrow's size as a fraction of the content is unchanged.
        var reference = CursorSize.ReferencePanelWidthMetres;

        var (smallW, smallH) = CursorSize.SizeMetres(CursorSize.DefaultScale, reference * 0.5f);
        var (largeW, largeH) = CursorSize.SizeMetres(CursorSize.DefaultScale, reference * 2.0f);

        Assert.Equal(smallW * 4f, largeW, 5);
        Assert.Equal(smallH * 4f, largeH, 5);

        // The fraction of the panel's width is what stays constant.
        Assert.Equal(
            smallW / (reference * 0.5f),
            largeW / (reference * 2.0f),
            5);
    }

    [Fact]
    public void The_Arrow_Survives_A_Zero_Width_Panel()
    {
        // The renderer reads the pose from a struct that is default-
        // initialised before the first anchor, so the width can be
        // zero.  That must not produce an invisible cursor.
        var (w, h) = CursorSize.SizeMetres(CursorSize.DefaultScale, 0f);

        Assert.True(w > 0f, "the cursor must never collapse to zero width");
        Assert.True(h > 0f, "the cursor must never collapse to zero height");
        Assert.True(float.IsFinite(w));
        Assert.True(float.IsFinite(h));
    }

    [Fact]
    public void The_Arrow_Aspect_Ratio_Is_Unchanged_By_The_Panel_Size()
    {
        // A bigger panel must not stretch the arrow.
        var reference = CursorSize.ReferencePanelWidthMetres;

        var (smallW, smallH) = CursorSize.SizeMetres(1.5f, reference * 0.5f);
        var (largeW, largeH) = CursorSize.SizeMetres(1.5f, reference * 3.0f);

        var expectedAspect = CursorSize.DefaultWidthMetres / CursorSize.DefaultHeightMetres;

        Assert.Equal(expectedAspect, smallW / smallH, 5);
        Assert.Equal(expectedAspect, largeW / largeH, 5);
    }

    [Fact]
    public void The_Reference_Panel_Matches_The_Profile()
    {
        // The arrow's reference and the panel it is drawn on have to
        // come from the same source, or they would drift apart.
        var expected = RokidDisplayProfile.PanelWidth(
            CursorSize.ReferencePanelDistanceMetres,
            CursorSize.ReferencePanelFovFraction);

        Assert.Equal(expected, CursorSize.ReferencePanelWidthMetres, 6);
    }
}
