// SPDX-License-Identifier: MIT
//
// Tests that the panel actually lands ON the glasses' display.
//
// Why these tests exist
// ---------------------
// The panel renderer drew NOTHING for a long time: the back buffer
// measured 0% coverage and the user saw a blank screen.  The cause
// was a chain of matrix-convention bugs, and the reason they survived
// so long is that every one of them is INVISIBLE at zero pitch and
// zero yaw — the easy case.  Only a realistic reading tilt exposed
// them.
//
// So these tests do what a screenshot cannot: they project the panel's
// four corners through the SAME matrix chain the renderer uses, and
// assert that the result lands inside the NDC viewport.  They sweep
// several head poses (level, pitch, yaw, roll) and several pitch
// biases, because the bug lived in the interaction between them.
//
// The matrix convention under test
// -------------------------------
// System.Numerics uses the ROW-vector convention: a point is
// transformed as v' = v * M, and a product M1 * M2 applies M1 FIRST.
// So the chain is:
//
//     v_clip = v_world * view * projection
//
// where view = T(-eye) * R^T.
//
// If any of those three orders is wrong, the panel ends up off-screen
// — but only at some poses.  Hence the sweep.

using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.Protocol;
using RokidDiyStation.Core.Spatial;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class PanelProjectionTests
{
    /// <summary>
    /// The four corners of the panel, projected to NDC.
    /// </summary>
    private static (float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ) Project(
        SpatialAnchorService service)
    {
        var view = service.GetView();
        var projection = service.GetProjection();

        // This is the chain the renderer uses, in the renderer's order.
        var mvp = view.View * projection;

        var minX = float.MaxValue; var maxX = float.MinValue;
        var minY = float.MaxValue; var maxY = float.MinValue;
        var minZ = float.MaxValue; var maxZ = float.MinValue;

        foreach (var corner in view.PanelCorners)
        {
            var clip = Vector4.Transform(new Vector4(corner, 1f), mvp);

            // W is the perspective depth: for a point at distance d in
            // front of the eye it is d (the projection matrix's third
            // row is [0, 0, -1, 0], so W = -z_view).  It must be
            // POSITIVE, which is what "in front of the camera" means
            // in clip space — a negative W means the point is behind
            // the eye and gets clipped away.
            Assert.True(clip.W > 0f,
                $"the panel must be in front of the eye, but the clip-space W was {clip.W}");

            var x = clip.X / clip.W;
            var y = clip.Y / clip.W;
            var z = clip.Z / clip.W;

            minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
            minY = MathF.Min(minY, y); maxY = MathF.Max(maxY, y);
            minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z);
        }

        return (minX, maxX, minY, maxY, minZ, maxZ);
    }

    /// <summary>
    /// Build a service settled at a given head pose, with the panel
    /// anchored at that pose.
    /// </summary>
    private static SpatialAnchorService SettleAndAnchor(float pitchDeg, float yawDeg, float rollDeg, float biasDeg)
    {
        var service = new SpatialAnchorService(new SpatialAnchorOptions
        {
            PitchBiasDegrees = biasDeg,
            PanelDistance = 1.5f,
            PanelFovFraction = 0.5f,
            NeckModelScale = 0.3f,
        });

        // The accelerometer reads the reaction to gravity, so at rest it
        // points along the device's own +Y expressed in world terms.
        var q = SyntheticImuSource.FromEulerZYX(rollDeg, pitchDeg, yawDeg);
        var accel = Vector3.Transform(new Vector3(0f, 9.80665f, 0f), Quaternion.Inverse(q));

        var ts = 1_000_000_000UL;
        for (var i = 0; i < 2000; i++)
        {
            service.Update(new ImuSample(ts, accel, Vector3.Zero, Vector3.Zero, true, ts));
            ts += 2_287_000UL;
        }

        service.AnchorHere();
        return service;
    }

    /// <summary>
    /// The panel must be fully inside the NDC viewport for every
    /// realistic head pose.
    /// </summary>
    /// <remarks>
    /// NDC is the [-1, 1] square.  Anything outside it is clipped
    /// away by the rasteriser, so a panel that is even partly outside
    /// is a panel the user cannot read.
    /// </remarks>
    [Theory]
    [InlineData(0f, 0f, 0f)]        // level
    [InlineData(-15f, 0f, 0f)]      // a slight downward look
    [InlineData(-29.5f, 0f, 0f)]    // the real worn reading tilt
    [InlineData(-40f, 0f, 0f)]      // looking down at a keyboard
    [InlineData(0f, 20f, 0f)]       // glancing right
    [InlineData(0f, -35f, 0f)]      // glancing left
    [InlineData(0f, 0f, 25f)]       // head rolled to one side
    [InlineData(-29.5f, 15f, 10f)]  // all three at once
    public void The_Panel_Is_Inside_The_Viewport_At_Every_Head_Pose(
        float pitch, float yaw, float roll)
    {
        var service = SettleAndAnchor(pitch, yaw, roll, biasDeg: 0f);
        var (minX, maxX, minY, maxY, minZ, maxZ) = Project(service);

        Assert.True(minX >= -1f && maxX <= 1f,
            $"the panel is outside the viewport horizontally: ndc.x [{minX:F3}, {maxX:F3}]");
        Assert.True(minY >= -1f && maxY <= 1f,
            $"the panel is outside the viewport vertically: ndc.y [{minY:F3}, {maxY:F3}]");
        Assert.True(minZ >= 0f && maxZ <= 1f,
            $"the panel is outside the depth range: ndc.z [{minZ:F3}, {maxZ:F3}]");
    }

    /// <summary>
    /// The panel must be CENTRED on the line of sight.
    /// </summary>
    /// <remarks>
    /// The panel is anchored along the gaze, so it must project to the
    /// middle of the screen.  This catches a whole class of subtly
    /// wrong matrix chains that still land inside the viewport — for
    /// example a view matrix whose translation was rotated into the
    /// eye frame, which shifts the panel sideways or downwards.
    /// </remarks>
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(-29.5f, 0f, 0f)]
    [InlineData(0f, 20f, 0f)]
    [InlineData(-29.5f, 15f, 10f)]
    public void The_Panel_Is_Centred_On_The_Line_Of_Sight(float pitch, float yaw, float roll)
    {
        var service = SettleAndAnchor(pitch, yaw, roll, biasDeg: 0f);
        var view = service.GetView();
        var centre = (view.PanelCorners[0] + view.PanelCorners[2]) * 0.5f;

        var clip = Vector4.Transform(new Vector4(centre, 1f), view.View * service.GetProjection());
        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;

        Assert.True(MathF.Abs(ndcX) < 0.02f,
            $"the panel's centre should be horizontally centred, but ndc.x was {ndcX:F4}");
        Assert.True(MathF.Abs(ndcY) < 0.02f,
            $"the panel's centre should be vertically centred, but ndc.y was {ndcY:F4}");
    }

    /// <summary>
    /// The panel's depth must be inside the viewport's depth range.
    /// </summary>
    /// <remarks>
    /// The D3D projection matrix maps the near plane to 0 and the far
    /// plane to 1.  A panel behind the near plane has a negative depth
    /// and is clipped away entirely, which is what happened when the
    /// matrix chain was wrong.
    /// </remarks>
    [Fact]
    public void The_Panel_Sits_Between_The_Near_And_Far_Planes()
    {
        var service = SettleAndAnchor(-29.5f, 0f, 0f, biasDeg: 0f);
        var (_, _, _, _, minZ, maxZ) = Project(service);

        Assert.True(minZ >= 0f, $"the panel is behind the near plane: ndc.z was {minZ:F4}");
        Assert.True(maxZ <= 1f, $"the panel is beyond the far plane: ndc.z was {maxZ:F4}");
    }

    /// <summary>
    /// The pitch bias must never push the panel off the bottom of the
    /// display.
    /// </summary>
    /// <remarks>
    /// This is the bug the hardware snapshot exposed: the default -10
    /// degree bias put the panel's bottom edge at 114% of the display
    /// height, so the bottom of the captured window was clipped off.
    /// The bias is now clamped to what fits, so even an absurd setting
    /// has to be safe.
    /// </remarks>
    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(-10f)]
    [InlineData(-15f)]
    [InlineData(-30f)]
    [InlineData(-90f)]
    public void A_Pitch_Bias_Can_Never_Push_The_Panel_Off_Screen(float bias)
    {
        var service = SettleAndAnchor(-29.5f, 0f, 0f, bias);
        var (minX, maxX, minY, maxY, _, _) = Project(service);

        Assert.True(minX >= -1f && maxX <= 1f,
            $"a {bias} degree bias put the panel outside the viewport horizontally: ndc.x [{minX:F3}, {maxX:F3}]");
        Assert.True(minY >= -1f && maxY <= 1f,
            $"a {bias} degree bias put the panel outside the viewport vertically: ndc.y [{minY:F3}, {maxY:F3}]");
    }

    /// <summary>
    /// The default settings must put the whole panel on the display.
    /// </summary>
    [Fact]
    public void The_Default_Settings_Put_The_Whole_Panel_On_The_Display()
    {
        var service = SettleAndAnchor(-29.5f, 0f, 0f, biasDeg: SpatialAnchorOptions.Default.PitchBiasDegrees);
        var (minX, maxX, minY, maxY, _, _) = Project(service);

        Assert.True(minX >= -1f && maxX <= 1f,
            $"the default settings put the panel outside the viewport horizontally: ndc.x [{minX:F3}, {maxX:F3}]");
        Assert.True(minY >= -1f && maxY <= 1f,
            $"the default settings put the panel outside the viewport vertically: ndc.y [{minY:F3}, {maxY:F3}]");
    }

    /// <summary>
    /// The panel must subtend the fraction of the field of view the
    /// user asked for.
    /// </summary>
    /// <remarks>
    /// "Fraction of the field of view" is an ANGULAR quantity: a panel
    /// covering half the horizontal FOV subtends half the horizontal
    /// angle.  It is deliberately NOT half the NDC width, because the
    /// perspective projection is non-linear: a panel whose half-angle
    /// is theta_h * f / 2 lands at NDC half-width
    /// tan(theta_h * f / 2) / tan(theta_h / 2), which is 0.481 rather
    /// than 0.5 for f = 0.5.  The analytic sizing in
    /// <see cref="RokidDisplayProfile"/> uses the angle, so this test
    /// uses the angle too — that is what ties the panel's SIZE to the
    /// projected size so the two cannot drift apart.
    /// </remarks>
    [Fact]
    public void The_Panel_Covers_The_Requested_Fraction_Of_The_Field_Of_View()
    {
        const float fraction = 0.5f;
        var service = new SpatialAnchorService(new SpatialAnchorOptions
        {
            PitchBiasDegrees = 0f,
            PanelDistance = 1.5f,
            PanelFovFraction = fraction,
        });

        var q = Quaternion.Identity;
        var accel = Vector3.Transform(new Vector3(0f, 9.80665f, 0f), Quaternion.Inverse(q));
        var ts = 1_000_000_000UL;
        for (var i = 0; i < 2000; i++)
        {
            service.Update(new ImuSample(ts, accel, Vector3.Zero, Vector3.Zero, true, ts));
            ts += 2_287_000UL;
        }
        service.AnchorHere();

        var view = service.GetView();
        var centre = (view.PanelCorners[0] + view.PanelCorners[2]) * 0.5f;

        // The horizontal edge midpoint, i.e. the point on the panel's
        // right-hand edge at the same height as its centre.  Measuring
        // to a CORNER instead would measure the DIAGONAL of the panel
        // and overestimate the horizontal angle by the vertical
        // half-angle.
        var bottomEdge = view.PanelCorners[1] - view.PanelCorners[0];
        var halfWidth = bottomEdge.Length() * 0.5f;
        var right = Vector3.Normalize(bottomEdge);
        var edge = centre + right * halfWidth;

        // The angle the panel actually subtends, measured from the
        // eye through its centre to the horizontal edge.
        var toCentre = Vector3.Normalize(centre - view.EyePosition);
        var toEdge = Vector3.Normalize(edge - view.EyePosition);
        var subtended = MathF.Acos(Math.Clamp(Vector3.Dot(toCentre, toEdge), -1f, 1f)) * 2f
                        * 180f / MathF.PI;

        var expected = fraction * RokidDisplayProfile.HorizontalFovDegrees();

        Assert.True(MathF.Abs(subtended - expected) < 0.25f,
            $"a panel covering {fraction} of the FOV should subtend {expected:F2} degrees, but it subtended {subtended:F2}");
    }
}
