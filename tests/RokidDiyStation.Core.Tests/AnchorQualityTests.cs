// SPDX-License-Identifier: MIT
//
// Tests for the 3DoF anchor-quality work.
//
// Three things are under test, all of them responses to the failure
// modes that make a 3DoF pinned screen feel broken:
//
//   1. The Mahony filter's ANGULAR rejection gate.  A linear
//      acceleration whose magnitude happens to stay near 1g (a slow
//      lean, walking's vertical component) fools the magnitude gate
//      but not the angle gate.  Without the angle gate the virtual
//      horizon tilts as the user shifts in their chair.
//
//   2. The Mahony filter's recovery timeout.  A filter that once
//      distrusted the accelerometer must not stay open-loop forever,
//      or the tilt drifts for the rest of the session.
//
//   3. The lazy follow.  Inside the comfort cone the panel must be
//      perfectly rigid (so reading text is stable); outside it the
//      panel must glide back towards the user's gaze rather than
//      crawling out of the field of view.

using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.Protocol;
using RokidDiyStation.Core.Spatial;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class AccelRejectionGateTests
{
    /// <summary>
    /// A constant lateral acceleration whose magnitude is still close to
    /// 1g.  The magnitude gate lets this through; the angle gate must
    /// not.
    /// </summary>
    private static readonly Vector3 SubtleLean = new(1.2f, 9.6f, 0f);

    [Fact]
    public void Angle_Gate_Rejects_A_Subtle_Linear_Acceleration()
    {
        // Sanity: the magnitude is inside the tolerance band, so the
        // magnitude gate on its own would NOT reject this.
        var norm = SubtleLean.Length();
        var magError = MathF.Abs(norm - 9.80665f);
        Assert.True(magError < 0.4f,
            $"the test sample must be inside the magnitude band, but the error is {magError}");

        var f = new MahonyFusion(useMagnetometer: false);
        var src = new SyntheticImuSource();
        var ts = 1_000_000_000UL;

        // Settle for a second at rest.
        foreach (var s in src.StepEuler(_ => (0f, 0f, 0f), seconds: 1.0f))
        {
            f.Update(s);
            ts = s.TimestampNs;
        }
        var before = f.Orientation;
        var (rollBefore, pitchBefore, _) = SyntheticImuSource.ToEulerZYX(before);

        // Now 3 s of a constant lean.  There is NO rotation, so the
        // only thing that could tilt the estimate is the accelerometer
        // being trusted when it should not be.
        for (var i = 0; i < 1300; i++)
        {
            f.Update(new ImuSample(ts, SubtleLean, Vector3.Zero, Vector3.Zero, true, 0));
            ts += 2_287_000UL;
        }

        var (rollAfter, pitchAfter, _) = SyntheticImuSource.ToEulerZYX(f.Orientation);

        // The tilt must barely move.  Without the angle gate this
        // drifts by several degrees, because the filter reads the lean
        // as a change in the gravity direction.
        Assert.InRange(MathF.Abs(pitchAfter - pitchBefore), 0f, 3f);
        Assert.InRange(MathF.Abs(rollAfter - rollBefore), 0f, 3f);
    }

    [Fact]
    public void Angle_Gate_Does_Not_Reject_A_Constant_Resting_Tilt()
    {
        // The counter-case that the earlier code got wrong.  When the
        // glasses are worn, the accelerometer is tilted relative to
        // world up by 20-30 degrees because the glasses sit back on
        // the nose.  That is a CONSTANT tilt, not a linear
        // acceleration: the filter must accept it and use it, because
        // that is the only thing pinning pitch and roll to gravity.
        //
        // Disabling the accelerometer while stationary (which the code
        // used to do) turned the filter into open-loop gyro
        // integration and produced a measured, linear ~0.3 deg/min of
        // roll drift on real Rokid Max hardware.
        var f = new MahonyFusion(useMagnetometer: false);
        var src = new SyntheticImuSource();
        var ts = 1_000_000_000UL;

        // A 25-degree resting tilt about the device X axis, expressed
        // as the accelerometer would read it.
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 25f * MathF.PI / 180f);
        var restingAccel = Vector3.Transform(new Vector3(0f, 9.80665f, 0f), Quaternion.Inverse(tilt));

        // Let the filter converge onto the tilt.
        for (var i = 0; i < 437 * 3; i++)
        {
            f.Update(new ImuSample(ts, restingAccel, Vector3.Zero, Vector3.Zero, true, 0));
            ts += 2_287_000UL;
        }

        var (roll, pitch, _) = SyntheticImuSource.ToEulerZYX(f.Orientation);
        // It should have converged onto the 25-degree tilt, not
        // ignored it.
        Assert.InRange(MathF.Abs(pitch), 15f, 35f);

        // And it must not drift away from it over the next 10 s.
        for (var i = 0; i < 437 * 10; i++)
        {
            f.Update(new ImuSample(ts, restingAccel, Vector3.Zero, Vector3.Zero, true, 0));
            ts += 2_287_000UL;
        }
        var (roll2, pitch2, _) = SyntheticImuSource.ToEulerZYX(f.Orientation);
        Assert.InRange(MathF.Abs(pitch2 - pitch), 0f, 3f);
        Assert.InRange(MathF.Abs(roll2 - roll), 0f, 3f);
    }

    [Fact]
    public void Recovery_Timeout_Brings_The_Filter_Back_Into_Gravity_Lock()
    {
        // Force a long period of accelerometer distrust, then check that
        // the filter recovers instead of drifting forever.
        var f = new MahonyFusion(useMagnetometer: false);
        var ts = 1_000_000_000UL;

        // 20 s of a strong linear acceleration: the magnitude gate
        // rejects every sample.
        for (var i = 0; i < 437 * 20; i++)
        {
            f.Update(new ImuSample(ts, new Vector3(6f, 6f, 0f), Vector3.Zero, Vector3.Zero, true, 0));
            ts += 2_287_000UL;
        }

        // Now the device is genuinely at rest again, at a tilted
        // posture.  The filter must lock onto it.
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 20f * MathF.PI / 180f);
        var restingAccel = Vector3.Transform(new Vector3(0f, 9.80665f, 0f), Quaternion.Inverse(tilt));

        for (var i = 0; i < 437 * 5; i++)
        {
            f.Update(new ImuSample(ts, restingAccel, Vector3.Zero, Vector3.Zero, true, 0));
            ts += 2_287_000UL;
        }

        var (_, pitch, _) = SyntheticImuSource.ToEulerZYX(f.Orientation);
        Assert.InRange(MathF.Abs(pitch), 10f, 30f);
    }

    [Fact]
    public void Sustained_Linear_Acceleration_Does_Not_Lock_On_A_False_Gravity()
    {
        // The tier-C "stale estimate" test needs the device to be at
        // rest.  A device that is being TRANSLATED for longer than the
        // rejection timeout has not gone stale — it is MOVING — and
        // locking onto its apparent gravity would tilt the horizon by
        // the full amount of the linear acceleration.
        //
        // This is the case that a naive "remove the magnitude
        // information entirely" reading of the research would get
        // wrong.
        var f = new MahonyFusion(useMagnetometer: false);
        var ts = 1_000_000_000UL;

        // Settle at rest first, so the filter's initial seed is a true
        // vertical and the measurement is about what happens DURING
        // the acceleration, not about the seeding.
        var rest = new Vector3(0f, 9.80665f, 0f);
        for (var i = 0; i < 437 * 2; i++)
        {
            f.Update(new ImuSample(ts, rest, Vector3.Zero, Vector3.Zero, true, 0));
            ts += 2_287_000UL;
        }

        // 30 s of a constant sideways acceleration of 4 m/s^2.  The
        // magnitude is |(4, 9.8, 0)| = 10.585, which is 0.78 above
        // gravity — comfortably outside the magnitude tolerance, so
        // the filter knows this is not a resting reading.
        var accel = new Vector3(4f, 9.8f, 0f);
        for (var i = 0; i < 437 * 30; i++)
        {
            f.Update(new ImuSample(ts, accel, Vector3.Zero, Vector3.Zero, true, 0));
            ts += 2_287_000UL;
        }

        // The filter must NOT have latched onto the false gravity.
        Assert.False(f.IsRecovering,
            "the filter latched onto a gravity direction that was really a linear acceleration");
        Assert.True(f.LastAccelTrust <= 0f,
            "the accelerometer must be distrusted while there is real linear acceleration");

        // And the horizon must not have tilted.
        var (roll, pitch, _) = SyntheticImuSource.ToEulerZYX(f.Orientation);
        Assert.InRange(MathF.Abs(pitch), 0f, 3f);
        Assert.InRange(MathF.Abs(roll), 0f, 3f);
    }

    [Fact]
    public void A_Small_Lean_Only_Bleeds_Off_Slowly()
    {
        // Tier B: the device is at rest, the magnitude is plausible,
        // but the accelerometer disagrees with the estimate by a small
        // amount.  The filter must NOT snap to the accelerometer — a
        // virtual screen whose horizon tilts every time the user
        // shifts in their chair is the most visible failure a 3DoF
        // anchor can produce — but it must also not ignore the
        // accelerometer forever, or the filter's own drift is never
        // corrected.
        var f = new MahonyFusion(useMagnetometer: false);
        var ts = 1_000_000_000UL;

        // A 12-degree resting tilt about the device X axis.
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 12f * MathF.PI / 180f);
        var restingAccel = Vector3.Transform(new Vector3(0f, 9.80665f, 0f), Quaternion.Inverse(tilt));

        // Settle so the filter agrees with the accelerometer.
        for (var i = 0; i < 437 * 5; i++)
        {
            f.Update(new ImuSample(ts, restingAccel, Vector3.Zero, Vector3.Zero, true, 0));
            ts += 2_287_000UL;
        }
        var (_, pitchAtRest, _) = SyntheticImuSource.ToEulerZYX(f.Orientation);

        // Now the device tilts a further 8 degrees and stops.  The
        // magnitude is unchanged (still exactly 1 g), so the filter
        // cannot tell this from drift — which is precisely the
        // ambiguity tier B exists to resolve.
        var newTilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 20f * MathF.PI / 180f);
        var leanedAccel = Vector3.Transform(new Vector3(0f, 9.80665f, 0f), Quaternion.Inverse(newTilt));

        for (var i = 0; i < 437 * 3; i++)
        {
            f.Update(new ImuSample(ts, leanedAccel, Vector3.Zero, Vector3.Zero, true, 0));
            ts += 2_287_000UL;
        }

        var (_, pitchAfter, _) = SyntheticImuSource.ToEulerZYX(f.Orientation);

        // Over three seconds the horizon must have moved by only a
        // fraction of a degree.  At 0.2 deg/s the slew rate can move
        // it by at most 0.6 deg; the disagreement is 8 degrees, so the
        // proportional alternative would have moved it all the way.
        Assert.True(MathF.Abs(pitchAfter - pitchAtRest) < 1.5f,
            $"the horizon tilted by {MathF.Abs(pitchAfter - pitchAtRest):F2} deg in 3 s " +
            "(it should have crept, not snapped)");
    }

    [Fact]
    public void The_Trust_Tier_Is_Visible_For_Diagnostics()
    {
        // The tray app's diagnostics panel needs to be able to say
        // WHY the filter is not correcting, so the tier has to be
        // observable.
        var f = new MahonyFusion(useMagnetometer: false);
        var ts = 1_000_000_000UL;

        // Agreeing: full trust, no disagreement.
        var accel = new Vector3(0f, 9.80665f, 0f);
        f.Update(new ImuSample(ts, accel, Vector3.Zero, Vector3.Zero, true, 0));
        ts += 2_287_000UL;
        for (var i = 0; i < 437; i++)
        {
            f.Update(new ImuSample(ts, accel, Vector3.Zero, Vector3.Zero, true, 0));
            ts += 2_287_000UL;
        }
        Assert.Equal(1f, f.LastAccelTrust);
        Assert.False(f.IsRecovering);
        Assert.True(f.LastResidualDegrees < 1f);
    }
}

public class LazyFollowTests
{
    private static Vector3 DirectionFromYawDegrees(float deg)
    {
        var rad = deg * MathF.PI / 180f;
        return Vector3.Normalize(new Vector3(MathF.Sin(rad), 0f, -MathF.Cos(rad)));
    }

    /// <summary>
    /// Drives the service's head pose with a continuous stream of
    /// synthetic IMU samples.
    /// </summary>
    /// <remarks>
    /// Two things make this necessary rather than merely convenient:
    ///
    ///   * The lazy follow compares the user's GAZE with the panel's
    ///     direction, so a test that wants to exercise it has to
    ///     actually move the head.  Computing a gaze vector and never
    ///     using it tests nothing at all — which is exactly what the
    ///     original version of these tests did.
    ///
    ///   * The timestamps must be CONTINUOUS.  The fusion filter
    ///     rejects any sample whose timestamp does not advance, so
    ///     creating a fresh SyntheticImuSource for each burst would
    ///     silently drop every sample after the first.
    /// </remarks>
    private sealed class HeadDriver
    {
        private const float Dt = 1f / 437f;
        private const ulong TsStep = 2_287_000UL;

        private readonly SpatialAnchorService _service;
        private ulong _ts = 1_000_000_000UL;
        private float _yawDeg;
        private float _prevYawDeg;

        public HeadDriver(SpatialAnchorService service) => _service = service;

        /// <summary>Settle the filter at rest, at the current yaw.</summary>
        public void Settle(float seconds = 1.0f) => Hold(_yawDeg, seconds);

        /// <summary>
        /// Ramp to <paramref name="yawDeg"/> over
        /// <paramref name="rampSeconds"/>, then hold there.
        /// </summary>
        public void TurnTo(float yawDeg, float rampSeconds = 0.5f, float holdSeconds = 1.0f)
        {
            var steps = (int)(rampSeconds * 437f);
            for (var i = 0; i < steps; i++)
            {
                var k = (i + 1f) / steps;
                Emit(_yawDeg + (yawDeg - _yawDeg) * k);
            }
            _yawDeg = yawDeg;
            Hold(yawDeg, holdSeconds);
        }

        /// <summary>Stay at <paramref name="yawDeg"/> for a while.</summary>
        public void Hold(float yawDeg, float seconds)
        {
            var steps = (int)(seconds * 437f);
            for (var i = 0; i < steps; i++)
            {
                Emit(yawDeg);
            }
            _yawDeg = yawDeg;
        }

        private void Emit(float yawDeg)
        {
            var yawRate = (_prevYawDeg - yawDeg) / Dt * MathF.PI / 180f;
            _prevYawDeg = yawDeg;

            // The device frame is X = pitch, Y = yaw, Z = roll, and a
            // pure yaw rotation about the world Y axis leaves gravity
            // pointing along the body Y axis, so the accelerometer
            // reads a constant +1 g.
            var q = SyntheticImuSource.FromEulerZYX(0f, 0f, yawDeg * MathF.PI / 180f);
            var accel = Vector3.Transform(new Vector3(0f, 9.80665f, 0f), Quaternion.Inverse(q));
            var gyro = new Vector3(0f, yawRate, 0f);

            _service.Update(new ImuSample(_ts, accel, gyro, Vector3.Zero, true, 0));
            _ts += TsStep;
        }
    }

    /// <summary>
    /// Build a service whose anchor is already at a known pose, so the
    /// lazy follow can be exercised without a live IMU stream.
    /// </summary>
    private static (SpatialAnchorService Service, PinnedPanel Panel, HeadDriver Driver) MakeService(
        float deadzoneDegrees = 15f,
        float ratePerSecond = 3f)
    {
        var anchor = new SpatialAnchor(useMagnetometer: false);
        var service = new SpatialAnchorService(
            new SpatialAnchorOptions
            {
                LazyFollowDeadzoneDegrees = deadzoneDegrees,
                LazyFollowRatePerSecond = ratePerSecond,
                PanelDistance = 1.5f,
                PanelFovFraction = 0.5f,
                PitchBiasDegrees = 0f,
            },
            anchor);

        // Settle the filter at rest first, so the anchor's pose is a
        // genuine identity pose rather than whatever the first sample
        // happened to seed from.
        var driver = new HeadDriver(service);
        driver.Settle();

        // Anchor at the identity pose: the panel points down -Z.
        service.AnchorHere();
        var panel = service.Panel!;
        return (service, panel, driver);
    }

    [Fact]
    public void Inside_The_Comfort_Cone_The_Panel_Is_Perfectly_Rigid()
    {
        var (service, panel, driver) = MakeService(deadzoneDegrees: 15f);

        // The head is looking 10 degrees to the right — inside the
        // cone.  The panel must not move at all.
        driver.TurnTo(10f);
        var view = service.GetView();
        Assert.Equal(4, view.PanelCorners.Length);

        // Compare against the panel's own immutable pose.
        var expected = panel.Corners();
        for (var i = 0; i < 4; i++)
        {
            var d = (view.PanelCorners[i] - expected[i]).Length();
            Assert.True(d < 1e-4f,
                $"corner {i} moved by {d:F5} m inside the comfort cone");
        }
    }

    [Fact]
    public void Outside_The_Comfort_Cone_The_Panel_Glides_Back()
    {
        var (service, panel, driver) = MakeService(deadzoneDegrees: 15f, ratePerSecond: 3f);

        // The head has drifted 40 degrees away (the classic 3DoF yaw
        // drift).  Over a simulated second, the lazy follow must pull
        // the panel back towards the cone edge — but it must NOT
        // teleport it there, and it must NOT carry it all the way onto
        // the gaze.
        driver.TurnTo(40f);

        var firstCorners = service.GetView().PanelCorners;
        var firstOffset = HorizontalOffset(firstCorners, panel);

        for (var i = 0; i < 60; i++)
        {
            service.GetView();
            Thread.Sleep(16);
        }

        var laterCorners = service.GetView().PanelCorners;
        var laterOffset = HorizontalOffset(laterCorners, panel);

        // The panel has moved towards the gaze...
        Assert.True(laterOffset > firstOffset,
            $"the panel should have moved towards the gaze: {firstOffset:F3} -> {laterOffset:F3}");

        // ...but it has not jumped all the way onto the gaze.  The gaze
        // is 40 degrees away from the anchor; sitting on the cone edge
        // is 25 degrees away.  A panel that chased the eyes would end
        // up 40 degrees off, at roughly 1.03 m of lateral offset for a
        // 1.5 m panel distance.
        var gazeOffset = panel.Distance * 2f * MathF.Sin(20f * MathF.PI / 180f);
        Assert.True(laterOffset < gazeOffset * 0.85f,
            $"the panel should have stopped at the cone edge, not chased the gaze: " +
            $"offset {laterOffset:F3} m vs gaze {gazeOffset:F3} m");
    }

    [Fact]
    public void The_Panel_Stops_At_The_Cone_Edge_And_Not_The_Gaze()
    {
        var (service, panel, driver) = MakeService(deadzoneDegrees: 15f, ratePerSecond: 3f);

        driver.TurnTo(40f);

        // Let it settle.
        for (var i = 0; i < 200; i++)
        {
            service.GetView();
            Thread.Sleep(16);
        }

        var offset = HorizontalOffset(service.GetView().PanelCorners, panel);

        // The gaze is 40 degrees from the anchor, so the cone edge is
        // 40 - 15 = 25 degrees away.
        var expected = panel.Distance * 2f * MathF.Sin(12.5f * MathF.PI / 180f);
        Assert.InRange(offset, expected * 0.9f, expected * 1.1f);
    }

    [Fact]
    public void Lazy_Follow_Can_Be_Disabled()
    {
        var (service, panel, driver) = MakeService(deadzoneDegrees: 0f);

        // With the cone at zero the panel must stay exactly where it
        // was anchored, no matter how far the head has turned.
        driver.TurnTo(40f);
        var view = service.GetView();
        var expected = panel.Corners();
        for (var i = 0; i < 4; i++)
        {
            var d = (view.PanelCorners[i] - expected[i]).Length();
            Assert.True(d < 1e-4f, $"corner {i} moved by {d:F5} m with lazy follow off");
        }
    }

    [Fact]
    public void The_Panel_Keeps_Its_Distance_From_The_Anchor_Eye()
    {
        var (service, panel, driver) = MakeService(deadzoneDegrees: 15f);

        // The lazy follow rotates the panel about the point where it
        // was pinned, so the distance from the anchor eye must not
        // change.
        driver.TurnTo(40f);
        for (var i = 0; i < 120; i++)
        {
            service.GetView();
            Thread.Sleep(16);
        }

        var corners = service.GetView().PanelCorners;
        var centre = (corners[0] + corners[2]) * 0.5f;
        var eye = panel.EyeAtAnchor;
        var distance = (centre - eye).Length();
        Assert.InRange(distance, panel.Distance - 0.01f, panel.Distance + 0.01f);
    }

    /// <summary>
    /// The horizontal distance between the rendered panel's centre and
    /// the panel's anchored centre, in metres.
    /// </summary>
    private static float HorizontalOffset(Vector3[] corners, PinnedPanel panel)
    {
        var centre = (corners[0] + corners[2]) * 0.5f;
        var anchored = (panel.Corners()[0] + panel.Corners()[2]) * 0.5f;
        var d = centre - anchored;
        return new Vector3(d.X, 0f, d.Z).Length();
    }
}
