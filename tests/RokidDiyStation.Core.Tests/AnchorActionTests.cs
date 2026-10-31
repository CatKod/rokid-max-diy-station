// SPDX-License-Identifier: MIT
//
// Tests for the three anchor ACTIONS the tray app exposes.
//
// The tray app offers the user three ways to move the panel:
//
//   Ctrl+Space  "pin panel here"   — re-pin the panel at the gaze
//   Ctrl+R      "forget the panel" — drop the panel, keep the fusion
//   Ctrl+C      "recentre"         — rebase the world origin AND
//                                    re-pin the panel
//
// These are three DIFFERENT operations, and they were originally all
// wired to the same thing: the anchor's SetAnchorHere().  That was
// wrong in a way that is easy to miss, because every one of them
// "moves the panel" — but they differ in what happens to the
// fusion's calibration and to the world origin, and getting that
// wrong makes the panel lurch sideways or makes the user wait a
// second for the filter to re-calibrate after every recentre.
//
// The distinguishing property is what the HEAD POSE reports afterwards:
//
//   * "pin panel here" must NOT move the world origin, so the head
//     pose is unchanged and the panel moves to the gaze.
//   * "forget the panel" must NOT move the world origin either, and
//     must not destroy the fusion's calibration, and there must be no
//     panel afterwards.
//   * "recentre" MUST move the world origin, so the head pose
//     becomes the identity, AND there must be a panel afterwards.
//
// Note that these tests drive the SpatialAnchorService directly.
// The wiring that connects the hotkeys to these calls lives in the
// WPF layer, which is not unit-testable without a message pump, so
// the mapping itself is asserted by reading the code.

using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.Protocol;
using RokidDiyStation.Core.Spatial;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class AnchorActionTests
{
    /// <summary>
    /// A service with a live IMU stream, settled at rest.
    /// </summary>
    private static (SpatialAnchorService Service, ulong Ts) MakeService()
    {
        var service = new SpatialAnchorService(
            new SpatialAnchorOptions
            {
                LazyFollowDeadzoneDegrees = 15f,
                LazyFollowRatePerSecond = 3f,
                PanelDistance = 1.5f,
                PanelFovFraction = 0.5f,
                PitchBiasDegrees = 0f,
            },
            new SpatialAnchor(useMagnetometer: false));

        var ts = 1_000_000_000UL;
        var accel = Vector3.Transform(
            new Vector3(0f, 9.80665f, 0f), Quaternion.Inverse(Quaternion.Identity));
        for (var i = 0; i < 2000; i++)
        {
            service.Update(new ImuSample(ts, accel, Vector3.Zero, Vector3.Zero, true, ts));
            ts += 2_287_000UL;
        }
        return (service, ts);
    }

    /// <summary>
    /// The yaw of the head, in degrees, as the service reports it.
    /// </summary>
    private static float YawOf(SpatialAnchorService service)
    {
        var (_, _, yaw) = SyntheticImuSource.ToEulerZYX(service.Anchor.HeadOrientation);
        return yaw;
    }

    /// <summary>
    /// The horizontal angle between the panel's direction and the
    /// user's gaze, in degrees.
    /// </summary>
    private static float PanelToGazeAngle(SpatialAnchorService service)
    {
        var panel = service.Panel!;
        var gaze = NeckModelCamera.Forward(service.Anchor.HeadOrientation);
        var flatPanel = new Vector3(panel.WorldDirection.X, 0f, panel.WorldDirection.Z);
        var flatGaze = new Vector3(gaze.X, 0f, gaze.Z);
        if (flatPanel.LengthSquared() < 1e-8f || flatGaze.LengthSquared() < 1e-8f) return 0f;
        var dot = Vector3.Dot(
            Vector3.Normalize(flatPanel), Vector3.Normalize(flatGaze));
        return MathF.Acos(Math.Clamp(dot, -1f, 1f)) * 180f / MathF.PI;
    }

    [Fact]
    public void Anchoring_Here_Leaves_The_World_Origin_Alone()
    {
        var (service, _) = MakeService();

        var yawBefore = YawOf(service);
        service.AnchorHere();

        Assert.True(MathF.Abs(YawOf(service) - yawBefore) < 0.01f,
            $"re-pinning the panel must not move the world origin, but the yaw moved from {yawBefore:F3} to {YawOf(service):F3}");
    }

    [Fact]
    public void Anchoring_Here_Places_The_Panel_On_The_Gaze()
    {
        var (service, _) = MakeService();
        service.AnchorHere();

        var angle = PanelToGazeAngle(service);
        Assert.True(angle < 0.5f,
            $"the panel should sit on the gaze, but it is {angle:F3} degrees away");
    }

    [Fact]
    public void Anchoring_Here_Keeps_The_Fusion_Calibrated()
    {
        var (service, _) = MakeService();
        Assert.True(service.Anchor.IsGyroCalibrated, "the fixture should be calibrated");

        service.AnchorHere();

        Assert.True(service.Anchor.IsGyroCalibrated,
            "re-pinning the panel must not throw away the gyro bias estimate");
    }

    [Fact]
    public void Forgetting_The_Panel_Removes_It_And_Keeps_The_Calibration()
    {
        var (service, _) = MakeService();
        service.AnchorHere();
        Assert.True(service.HasPanel);

        service.ForgetPanel();

        Assert.False(service.HasPanel, "the panel should be gone");
        Assert.True(service.Anchor.IsGyroCalibrated,
            "forgetting the panel must not throw away the gyro bias estimate");
    }

    [Fact]
    public void Forgetting_The_Panel_Leaves_The_World_Origin_Alone()
    {
        var (service, _) = MakeService();
        var yawBefore = YawOf(service);

        service.ForgetPanel();

        Assert.True(MathF.Abs(YawOf(service) - yawBefore) < 0.01f,
            $"forgetting the panel must not move the world origin, but the yaw moved from {yawBefore:F3} to {YawOf(service):F3}");
    }

    [Fact]
    public void Recentring_Moves_The_World_Origin_And_Re_Pins_The_Panel()
    {
        var (service, _) = MakeService();
        service.AnchorHere();

        service.Recenter();

        // The world origin moved, so the head pose is now the identity.
        Assert.True(MathF.Abs(YawOf(service)) < 0.01f,
            $"recentring must rebase the world origin, but the yaw is {YawOf(service):F3}");

        // And the panel is back on the gaze.
        Assert.True(service.HasPanel, "recentring must leave a pinned panel behind");
        var angle = PanelToGazeAngle(service);
        Assert.True(angle < 0.5f,
            $"the panel should sit on the gaze after recentring, but it is {angle:F3} degrees away");
    }

    [Fact]
    public void Recentring_Keeps_The_Fusion_Calibrated()
    {
        var (service, _) = MakeService();
        service.Recenter();

        Assert.True(service.Anchor.IsGyroCalibrated,
            "recentring rebases the origin; it must not throw away the gyro bias estimate");
    }
}
