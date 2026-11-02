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
}
