// SPDX-License-Identifier: MIT
//
// Regression tests for the "the panel recentres itself" bug.
//
// THE BUG
// -------
// The user reported: "when I move the mouse from another screen onto
// the spatial display, it seems to recentre itself".
//
// There were THREE separate paths that could move a pinned panel
// without the user asking for it:
//
//   1. The cursor crossing onto the virtual display called
//      AnchorHere(), which re-pinned the panel at the current gaze.
//
//   2. The captured content's shape changing (the user resizing the
//      virtual display) called AnchorHere() as well.
//
//   3. Changing the panel distance or FOV fraction re-pinned the
//      panel instead of merely resizing it.
//
// The common mistake behind all three: they treated "the panel's world
// pose" as a function of whatever the user happened to be doing RIGHT
// NOW.  But the panel's pose is a property of the ROOM.  A screen
// hanging on a wall does not jump to face you every time you walk
// past it, and it does not jump when you change its resolution.
//
// The fix is that the panel is pinned ONCE, deliberately, and then its
// pose is RIGID.  Anything that only changes the panel's SIZE must
// resize it in place.
//
// THE PROPERTY UNDER TEST
// -----------------------
// After the user pins the panel, the panel's world pose - its centre,
// its normal and its up axis - must not change on its own.  Only an
// explicit action (pin / forget / recentre) may move it.

using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.Protocol;
using RokidDiyStation.Core.Spatial;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class PanelRigidityTests
{
    /// <summary>
    /// A service with a live IMU stream, settled at rest.
    /// </summary>
    private static (SpatialAnchorService Service, ulong Ts) MakeService()
    {
        var service = new SpatialAnchorService(
            new SpatialAnchorOptions
            {
                WorldLock = WorldLockMode.Strict,
                PanelDistance = 1.5f,
                PanelFovFraction = 0.5f,
                PitchBiasDegrees = 0f,
            },
            new SpatialAnchor(useMagnetometer: false));

        var ts = 1_000_000_000UL;
        var accel = new Vector3(0f, 9.80665f, 0f);
        for (var i = 0; i < 2000; i++)
        {
            service.Update(new ImuSample(ts, accel, Vector3.Zero, Vector3.Zero, true, ts));
            ts += 2_287_000UL;
        }
        return (service, ts);
    }

    /// <summary>
    /// The panel's world pose, as a comparable triple.
    /// </summary>
    private static (Vector3 Center, Vector3 Normal, Vector3 Up) PoseOf(SpatialAnchorService service)
    {
        var panel = service.Panel!;
        return (panel.Center, panel.Normal, panel.Up);
    }

    private static void AssertSamePose(
        (Vector3 Center, Vector3 Normal, Vector3 Up) expected,
        (Vector3 Center, Vector3 Normal, Vector3 Up) actual,
        string what)
    {
        Assert.True(Vector3.Distance(expected.Center, actual.Center) < 1e-4f,
            $"{what}: the panel's CENTRE moved from {expected.Center} to {actual.Center}");
        Assert.True(Vector3.Distance(expected.Normal, actual.Normal) < 1e-4f,
            $"{what}: the panel's NORMAL moved from {expected.Normal} to {actual.Normal}");
        Assert.True(Vector3.Distance(expected.Up, actual.Up) < 1e-4f,
            $"{what}: the panel's UP axis moved from {expected.Up} to {actual.Up}");
    }

    [Fact]
    public void Changing_The_Content_Shape_Resizes_The_Panel_In_Place()
    {
        // The bug: the virtual display's resolution changed, and the
        // panel teleported to wherever the user was looking.  It must
        // instead keep its pose and change its shape.
        var (service, _) = MakeService();
        service.AnchorHere();
        var poseBefore = PoseOf(service);
        var widthBefore = service.Panel!.Width;
        var heightBefore = service.Panel.Height;

        // A 4:3 capture instead of the default 16:9.
        Assert.True(service.SetContentSize(1024, 768), "the content shape should have changed");

        AssertSamePose(poseBefore, PoseOf(service), "changing the content shape");

        // And the panel really did change shape.
        Assert.True(MathF.Abs(service.Panel!.Height - heightBefore) > 1e-3f,
            "a 4:3 capture should make the panel shorter");
        Assert.True(MathF.Abs(service.Panel!.Width - widthBefore) < 1e-3f,
            "the panel's width is fixed by the FOV, so it should not change");
    }

    [Fact]
    public void Changing_The_Content_Shape_Does_Not_Re_Pin_On_The_Gaze()
    {
        // The panel must not jump onto the gaze either.  After the
        // shape change it must still be exactly where it was pinned.
        var (service, _) = MakeService();
        service.AnchorHere();
        var poseBefore = PoseOf(service);

        service.SetContentSize(1024, 768);

        AssertSamePose(poseBefore, PoseOf(service), "changing the content shape");
    }

    [Fact]
    public void Changing_The_Panel_Distance_Resizes_Without_Moving_It()
    {
        // Same class of bug, reached through the settings window.
        var (service, _) = MakeService();
        service.AnchorHere();
        var poseBefore = PoseOf(service);
        var widthBefore = service.Panel!.Width;

        service.ApplyOptions(new SpatialAnchorOptions
        {
            WorldLock = WorldLockMode.Strict,
            PanelDistance = 2.0f,          // was 1.5
            PanelFovFraction = 0.5f,
            PitchBiasDegrees = 0f,
        });

        AssertSamePose(poseBefore, PoseOf(service), "changing the panel distance");

        // But the panel really did get bigger.
        Assert.True(service.Panel!.Width > widthBefore + 1e-3f,
            "a further-away panel at the same FOV should be wider in metres");
    }

    [Fact]
    public void Changing_The_Fov_Resizes_Without_Moving_It()
    {
        var (service, _) = MakeService();
        service.AnchorHere();
        var poseBefore = PoseOf(service);

        service.ApplyOptions(new SpatialAnchorOptions
        {
            WorldLock = WorldLockMode.Strict,
            PanelDistance = 1.5f,
            PanelFovFraction = 0.7f,        // was 0.5
            PitchBiasDegrees = 0f,
        });

        AssertSamePose(poseBefore, PoseOf(service), "changing the FOV fraction");
    }

    [Fact]
    public void The_Panel_Pose_Is_Rigid_While_The_Head_Turns()
    {
        // The world-lock property itself: turning the head slides the
        // panel across the display but does not move it in the room.
        var (service, ts) = MakeService();
        service.AnchorHere();
        var poseBefore = PoseOf(service);

        YawHead(service, ref ts, degrees: 30f);

        AssertSamePose(poseBefore, PoseOf(service), "turning the head");
    }

    [Fact]
    public void Explicitly_Anchoring_Still_Moves_The_Panel()
    {
        // The guard on the guard: the fix must not have broken the
        // deliberate action.  Pinning on demand still works.
        var (service, ts) = MakeService();
        service.AnchorHere();
        var poseBefore = PoseOf(service);

        // Turn the head away.
        YawHead(service, ref ts, degrees: 25f);

        service.AnchorHere();

        var poseAfter = PoseOf(service);
        Assert.True(Vector3.Distance(poseBefore.Center, poseAfter.Center) > 1e-3f,
            "an explicit re-pin should move the panel onto the new gaze");
    }

    /// <summary>
    /// Turn the head by the given number of degrees of yaw.
    /// </summary>
    /// <remarks>
    /// Yaw cannot be observed from the accelerometer - a rotation about
    /// the vertical axis does not change the gravity vector - so the
    /// yaw has to be integrated from the gyro.  The gyro's Y axis is
    /// the device's yaw rate; see SyntheticImuSource.StepEuler, which
    /// builds the body rates as (pitchRate, yawRate, rollRate).
    ///
    /// The MAGNETOMETER is off in this fixture, so the yaw is a pure
    /// integration and the result is exactly the angle asked for.
    /// </remarks>
    private static void YawHead(
        SpatialAnchorService service,
        ref ulong ts,
        float degrees,
        int samples = 900)
    {
        var totalRad = degrees * MathF.PI / 180f;
        var dt = 1f / 437f;
        var rate = totalRad / (samples * dt);   // rad/s about the Y axis

        for (var i = 0; i < samples; i++)
        {
            var gyro = new Vector3(0f, rate, 0f);
            var accel = new Vector3(0f, 9.80665f, 0f);
            service.Update(new ImuSample(ts, accel, gyro, Vector3.Zero, true, ts));
            ts += 2_287_000UL;
        }
    }
}
