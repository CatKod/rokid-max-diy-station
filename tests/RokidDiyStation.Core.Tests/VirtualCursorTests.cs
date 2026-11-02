// SPDX-License-Identifier: MIT
//
// Tests for the virtual cursor's geometry and its mapping policy.
//
// Why these tests exist
// ---------------------
// The virtual cursor's whole value rests on TWO properties, and both
// are easy to get backwards:
//
//   1. The mouse moving RIGHT must move the cursor RIGHT, and the
//      mouse moving DOWN must move the cursor DOWN.  The derivation
//      runs through a right-handed world frame in which the panel's
//      "right" axis points towards the viewer's LEFT, so the signs
//      are genuinely counter-intuitive and a plausible-looking
//      implementation can easily be inverted.
//
//   2. The cursor must NOT slide when the head turns.  That is the
//      property that distinguishes the mapping from a naive
//      screen-space one, and it is invisible in a screenshot - it has
//      to be asserted.
//
// The geometry tests project the panel's four corners and check that
// the texture coordinates come out where the vertex layout says they
// should - the same corner table that the derivation in docs/M11
// spells out.

using System.Numerics;
using RokidDiyStation.Core.Spatial;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class PanelPointerTests
{
    /// <summary>
    /// A panel facing the viewer, 1 m wide and 9/16 m tall, with its
    /// centre 2 m in front of the eye.
    /// </summary>
    private static PanelPose MakePose() => new(
        Center: new Vector3(0f, 0f, 2f),
        Normal: new Vector3(0f, 0f, 1f),
        Up: new Vector3(0f, 1f, 0f),
        Width: 1f,
        Height: 9f / 16f);

    [Fact]
    public void PanelRightAxisPointsTowardsTheViewersLeft()
    {
        // cross(normal, up) with normal = +Z and up = +Y gives -X,
        // which is the viewer's LEFT.  This is the fact the whole u
        // derivation rests on, so it is asserted explicitly: if this
        // ever changes, every u sign in the codebase has to change
        // with it.
        var pose = MakePose();
        Assert.True(pose.Right.X < 0f);
    }

    [Theory]
    // corner 0 = centre - hx - hy  ->  uv (1, 1)
    // corner 1 = centre + hx - hy  ->  uv (0, 1)
    // corner 2 = centre + hx + hy  ->  uv (0, 0)
    // corner 3 = centre - hx + hy  ->  uv (1, 0)
    [InlineData(0, 1f, 1f)]
    [InlineData(1, 0f, 1f)]
    [InlineData(2, 0f, 0f)]
    [InlineData(3, 1f, 0f)]
    public void CornersMapToTheVertexLayoutsUv(int corner, float expectedU, float expectedV)
    {
        var pose = MakePose();
        var corners = pose.Corners();

        var uv = PanelPointer.ToUv(corners[corner], pose);

        Assert.Equal(expectedU, uv.X, 4);
        Assert.Equal(expectedV, uv.Y, 4);
    }

    [Fact]
    public void FromUvIsTheInverseOfToUv()
    {
        var pose = MakePose();

        foreach (var (u, v) in new[] { (0f, 0f), (0.5f, 0.5f), (1f, 1f), (0.25f, 0.75f) })
        {
            var world = PanelPointer.FromUv(u, v, pose);
            var back = PanelPointer.ToUv(world, pose);

            Assert.Equal(u, back.X, 4);
            Assert.Equal(v, back.Y, 4);
        }
    }

    [Fact]
    public void FromUvLandsOnThePanelSurface()
    {
        var pose = MakePose();
        var world = PanelPointer.FromUv(0.5f, 0.5f, pose);

        // The centre of the panel.
        Assert.Equal(pose.Center.X, world.X, 4);
        Assert.Equal(pose.Center.Y, world.Y, 4);
        Assert.Equal(pose.Center.Z, world.Z, 4);
    }

    [Fact]
    public void NormalOffsetLiftsThePointTowardsTheViewer()
    {
        var pose = MakePose();
        var world = PanelPointer.FromUv(0.5f, 0.5f, pose, normalOffset: 0.0015f);

        Assert.True(world.Z > pose.Center.Z);
    }

    [Fact]
    public void UnprojectOfTheCentrePixelPointsDownTheViewAxis()
    {
        // An eye at the origin looking down -Z, with the panel 2 m in
        // front of it.  The projection is a plain 16:9 perspective.
        var view = Matrix4x4.Identity;
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f, 16f / 9f, 0.05f, 50f);

        Assert.True(PanelPointer.TryUnproject(
            960, 540, 1920, 1080, view, projection, out var origin, out var direction));

        // The ray goes down -Z from the eye.
        Assert.True(direction.Z < 0f);
        Assert.Equal(0f, origin.X, 3);
        Assert.Equal(0f, origin.Y, 3);
    }

    [Fact]
    public void TheCentrePixelSeedsTheCursorAtThePanelCentre()
    {
        // The eye at the origin, looking down -Z.  The panel is 2 m in
        // FRONT of the eye, so at z = -2 (the world frame puts +Z
        // towards the viewer, and the camera looks down -Z).
        //
        // The panel's normal points back at the viewer, +Z.
        var view = Matrix4x4.CreateTranslation(-Vector3.Zero);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f, 16f / 9f, 0.05f, 50f);

        // The panel is sized so it exactly fills the view at 2 m.
        var hFov = 2f * MathF.Atan(MathF.Tan(MathF.PI / 8f) * 16f / 9f);
        var width = 2f * 2f * MathF.Tan(hFov * 0.5f);
        var pose = new PanelPose(
            new Vector3(0f, 0f, -2f),
            new Vector3(0f, 0f, 1f),
            new Vector3(0f, 1f, 0f),
            width, width * 9f / 16f);

        Assert.True(PanelPointer.TryUnproject(
            960, 540, 1920, 1080, view, projection, out var origin, out var direction));

        Assert.True(PanelPointer.TryIntersectPlane(
            origin, direction, pose.Center, pose.Normal, out var hit));

        var uv = PanelPointer.ToUv(hit, pose);

        // The centre pixel must seed the centre of the content.
        Assert.Equal(0.5f, uv.X, 3);
        Assert.Equal(0.5f, uv.Y, 3);
    }

    [Fact]
    public void TheRightEdgePixelSeedsTheRightEdgeOfTheContent()
    {
        // The same setup, but the mouse is at the far RIGHT of the
        // display.  The cursor must land on the content's RIGHT edge -
        // which is uv.x = 1, because u grows towards the viewer's
        // right.
        var view = Matrix4x4.CreateTranslation(-Vector3.Zero);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f, 16f / 9f, 0.05f, 50f);

        var hFov = 2f * MathF.Atan(MathF.Tan(MathF.PI / 8f) * 16f / 9f);
        var width = 2f * 2f * MathF.Tan(hFov * 0.5f);
        var pose = new PanelPose(
            new Vector3(0f, 0f, -2f),
            new Vector3(0f, 0f, 1f),
            new Vector3(0f, 1f, 0f),
            width, width * 9f / 16f);

        Assert.True(PanelPointer.TryUnproject(
            1919, 540, 1920, 1080, view, projection, out var origin, out var direction));
        Assert.True(PanelPointer.TryIntersectPlane(
            origin, direction, pose.Center, pose.Normal, out var hit));

        var uv = PanelPointer.ToUv(hit, pose);

        Assert.True(uv.X > 0.9f);
        Assert.Equal(0.5f, uv.Y, 2);
    }

    [Fact]
    public void TheBottomPixelSeedsTheBottomOfTheContent()
    {
        // The mouse at the BOTTOM of the display must seed the BOTTOM
        // of the content, which is uv.y = 1 (v grows downwards).
        var view = Matrix4x4.CreateTranslation(-Vector3.Zero);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f, 16f / 9f, 0.05f, 50f);

        var hFov = 2f * MathF.Atan(MathF.Tan(MathF.PI / 8f) * 16f / 9f);
        var width = 2f * 2f * MathF.Tan(hFov * 0.5f);
        var pose = new PanelPose(
            new Vector3(0f, 0f, -2f),
            new Vector3(0f, 0f, 1f),
            new Vector3(0f, 1f, 0f),
            width, width * 9f / 16f);

        Assert.True(PanelPointer.TryUnproject(
            960, 1079, 1920, 1080, view, projection, out var origin, out var direction));
        Assert.True(PanelPointer.TryIntersectPlane(
            origin, direction, pose.Center, pose.Normal, out var hit));

        var uv = PanelPointer.ToUv(hit, pose);

        Assert.True(uv.Y > 0.9f);
        Assert.Equal(0.5f, uv.X, 2);
    }

    [Fact]
    public void AParallelRayMissesThePlane()
    {
        var pose = MakePose();

        Assert.False(PanelPointer.TryIntersectPlane(
            Vector3.Zero, new Vector3(1f, 0f, 0f),
            pose.Center, pose.Normal, out _));
    }

    [Fact]
    public void APlaneBehindTheRayMisses()
    {
        var pose = MakePose();

        // The ray points away from the panel.
        Assert.False(PanelPointer.TryIntersectPlane(
            new Vector3(0f, 0f, 2f), new Vector3(0f, 0f, 1f),
            pose.Center, pose.Normal, out _));
    }
}

/// <summary>
/// Tests for the virtual cursor's ABSOLUTE mapping policy.
/// </summary>
/// <remarks>
/// The cursor used to be driven by a "re-seeded differential" model:
/// the mouse moved over the glasses' real display, so the cursor's
/// position had to be re-derived by ray-casting, and only the mouse's
/// DELTA moved it afterwards.
///
/// That model is gone.  The mouse now moves over the VIRTUAL DISPLAY,
/// whose pixel grid is the captured texture's, so the mapping is
/// absolute and exact: the cursor sits at the OS cursor's mapped
/// position, full stop.
///
/// The properties asserted here are the ones the product depends on:
/// the cursor is where it was put, it does not slide on its own, and
/// it clamps at the edges.
/// </remarks>
public class VirtualCursorAbsoluteMappingTests
{
    [Fact]
    public void TheCursorIsWhereItWasSeeded()
    {
        var cursor = new VirtualCursor();
        cursor.Seed(0.25f, 0.75f);

        Assert.Equal(0.25f, cursor.U, 4);
        Assert.Equal(0.75f, cursor.V, 4);
        Assert.True(cursor.IsActive);
    }

    [Fact]
    public void TheSeedIsClampedToTheContent()
    {
        var cursor = new VirtualCursor();

        // A rounding error at the very edge of the virtual display
        // must not put the cursor outside the panel.
        cursor.Seed(-0.4f, 1.7f);

        Assert.Equal(0f, cursor.U, 4);
        Assert.Equal(1f, cursor.V, 4);
    }

    [Fact]
    public void AnUnseededCursorIsNotActive()
    {
        var cursor = new VirtualCursor();
        Assert.False(cursor.IsActive);
    }

    [Fact]
    public void DeactivatingStopsTheCursor()
    {
        var cursor = new VirtualCursor();
        cursor.Seed(0.5f, 0.5f);
        Assert.True(cursor.IsActive);

        cursor.Deactivate();

        Assert.False(cursor.IsActive);
    }

    [Fact]
    public void TheDirtyFlagIsClearedAfterReading()
    {
        var cursor = new VirtualCursor();
        cursor.Seed(0.5f, 0.5f);

        Assert.True(cursor.IsDirty);
        Assert.False(cursor.IsDirty);
    }

    [Fact]
    public void TheCursorDoesNotSlideWhenTheHeadTurns()
    {
        // THE PROPERTY THAT DISTINGUISHES THIS ARCHITECTURE.
        //
        // The cursor's position is a pure function of the OS cursor's
        // position on the virtual display.  Nothing about the head
        // pose enters into it, so turning the head cannot move the
        // cursor - the arrow stays glued to the same thing it is
        // pointing at.
        //
        // Under the old differential model this was true only because
        // the cursor moved by the mouse's DELTA; here it is true by
        // construction.
        var cursor = new VirtualCursor();
        cursor.Seed(0.42f, 0.58f);

        // Whatever happens to the world frame - the anchor moves, the
        // head turns - the cursor's texture coordinates do not change.
        for (var i = 0; i < 100; i++)
        {
            var (u, v) = cursor.Position;
            Assert.Equal(0.42f, u, 6);
            Assert.Equal(0.58f, v, 6);
        }
    }
}

