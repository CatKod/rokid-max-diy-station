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

/// <summary>
/// Tests for the panel's anchoring behaviour when the user turns their
/// head away from it.
/// </summary>
/// <remarks>
/// The behaviour under test is the one the user asked for on real
/// hardware:
///
///   "when I test rotating horizontally, at small head rotations (the
///    screen stays within the FOV, that's fine) however when the
///    deviation gets too large (the rendered tab screen is missing
///    about 1/3) then the screen goes WITH the head instead of still
///    tracking the correct place in space."
///
/// The old implementation did exactly that: it dragged the panel back
/// towards the gaze whenever the gaze left a comfort cone, which during
/// an ordinary head turn parks the panel roughly a third of the way
/// off the display and drags it along with the skull.
///
/// The default mode is now STRICT WORLD LOCK: the panel's world
/// direction is immutable, and no head motion can move it.
/// </remarks>
public class WorldLockTests
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
    ///   * The anchoring compares the user's GAZE with the panel's
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
    /// anchoring can be exercised without a live IMU stream.
    /// </summary>
    private static (SpatialAnchorService Service, PinnedPanel Panel, HeadDriver Driver) MakeService(
        WorldLockMode mode = WorldLockMode.Strict,
        float coneDegrees = 15f,
        float ratePerSecond = 3f)
    {
        var anchor = new SpatialAnchor(useMagnetometer: false);
        var service = new SpatialAnchorService(
            new SpatialAnchorOptions
            {
                WorldLock = mode,
                ComfortConeDegrees = coneDegrees,
                ComfortConeRatePerSecond = ratePerSecond,
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

    // -----------------------------------------------------------------
    // Strict world lock — the default
    // -----------------------------------------------------------------

    /// <summary>
    /// The panel must NOT move when the head turns, however far.
    /// </summary>
    /// <remarks>
    /// This is the regression test for the user's report.  The old
    /// implementation dragged the panel towards the gaze once the gaze
    /// left the 15-degree comfort cone, so a 40-degree turn moved the
    /// panel by 25 degrees — a quarter of the way round the room.
    ///
    /// The assertion is deliberately on the panel's WORLD DIRECTION
    /// rather than on its on-screen position: a world-locked panel
    /// slides across the display as the head turns (that is correct and
    /// desirable), so its screen position is expected to change.  Its
    /// place in the ROOM must not.
    /// </remarks>
    [Fact]
    public void The_Panel_Does_Not_Move_When_The_Head_Turns()
    {
        var (service, panel, driver) = MakeService();

        driver.TurnTo(40f);
        for (var i = 0; i < 120; i++)
        {
            service.GetView();
            Thread.Sleep(16);
        }

        var offset = HorizontalOffset(service.GetView().PanelCorners, panel);
        Assert.True(offset < 1e-3f,
            $"the panel moved {offset:F4} m away from its anchored place when the head " +
            "turned 40 degrees — it is following the head instead of staying in the room");
    }

    /// <summary>
    /// The panel must not move even after a LARGE turn, which is where
    /// the old implementation dragged it hardest.
    /// </summary>
    /// <remarks>
    /// The user described the failure at the point where about a third
    /// of the panel was clipped.  The geometry puts that at roughly 11
    /// degrees of head rotation for a 50 %-FOV panel on a 44.24-degree
    /// display, and the old follow's steady-state lag of omega/lambda
    /// = 20 degrees parked the panel exactly there.
    ///
    /// A 90-degree turn is well past that point, so it exercises the
    /// regime where the old code was at its worst.
    /// </remarks>
    [Fact]
    public void The_Panel_Does_Not_Move_After_A_Large_Turn()
    {
        var (service, panel, driver) = MakeService();

        driver.TurnTo(90f, rampSeconds: 1.0f, holdSeconds: 2.0f);
        for (var i = 0; i < 120; i++)
        {
            service.GetView();
            Thread.Sleep(16);
        }

        var offset = HorizontalOffset(service.GetView().PanelCorners, panel);
        Assert.True(offset < 1e-3f,
            $"the panel moved {offset:F4} m from its anchored place after a 90-degree turn " +
            "— it came round with the head");
    }

    /// <summary>
    /// Turning away and back must leave the panel exactly where it
    /// was.
    /// </summary>
    /// <remarks>
    /// This is the part of the old behaviour that was most destructive.
    /// The follow measured the gaze against the panel's CURRENT
    /// direction, so after a 90-degree turn the panel sat at 75
    /// degrees from its anchor; turning back dragged it to 15 degrees.
    /// The anchor was destroyed by the act of looking away, and the
    /// panel never returned to where the user had put it.
    /// </remarks>
    [Fact]
    public void Turning_Away_And_Back_Leaves_The_Panel_Where_It_Was()
    {
        var (service, panel, driver) = MakeService();

        driver.TurnTo(90f, rampSeconds: 1.0f, holdSeconds: 1.0f);
        driver.TurnTo(0f, rampSeconds: 1.0f, holdSeconds: 1.0f);
        for (var i = 0; i < 120; i++)
        {
            service.GetView();
            Thread.Sleep(16);
        }

        var offset = HorizontalOffset(service.GetView().PanelCorners, panel);
        Assert.True(offset < 1e-3f,
            $"the panel ended up {offset:F4} m from its anchored place after looking away " +
            "and back — the anchor was destroyed by looking away");
    }

    /// <summary>
    /// The panel must slide ACROSS the display as the head turns.
    /// </summary>
    /// <remarks>
    /// The complement of the test above, and just as important: a
    /// panel that does not move on screen at all would be head-locked,
    /// which is the failure the whole product exists to avoid.
    ///
    /// The on-screen position is the panel's centre in normalised device
    /// coordinates.  Turning the head right must push the panel to the
    /// left (negative x) and vice versa.
    /// </remarks>
    [Fact]
    public void The_Panel_Slides_Across_The_Display_As_The_Head_Turns()
    {
        var (service, _, driver) = MakeService();

        var centred = NdcCentreX(service);
        driver.TurnTo(30f);
        for (var i = 0; i < 30; i++)
        {
            service.GetView();
            Thread.Sleep(16);
        }
        var turned = NdcCentreX(service);

        // Turning the head to the right must push the panel to the
        // LEFT of the display.  A head-locked panel would stay at 0.
        Assert.True(turned < centred - 0.1f,
            $"the panel did not slide across the display when the head turned: " +
            $"ndc.x {centred:F3} -> {turned:F3} — it is head-locked");
    }

    /// <summary>
    /// The DEFAULT options must be strict world lock.
    /// </summary>
    /// <remarks>
    /// Every other test in this file names the mode explicitly, so
    /// without this one the DEFAULT could silently be flipped back to
    /// the comfort cone and the whole suite would still pass.
    ///
    /// This is the test that would have failed on the code the user
    /// reported: the old default was a 15-degree comfort cone, which
    /// dragged the panel along with the head.
    /// </remarks>
    [Fact]
    public void The_Default_Options_Are_Strict_World_Lock()
    {
        Assert.Equal(WorldLockMode.Strict, SpatialAnchorOptions.Default.WorldLock);

        // And the default must actually behave that way.
        var anchor = new SpatialAnchor(useMagnetometer: false);
        var service = new SpatialAnchorService(null, anchor);
        var driver = new HeadDriver(service);
        driver.Settle();
        service.AnchorHere();
        var panel = service.Panel!;

        driver.TurnTo(60f, rampSeconds: 1.0f, holdSeconds: 2.0f);
        for (var i = 0; i < 60; i++)
        {
            service.GetView();
            Thread.Sleep(16);
        }

        var offset = HorizontalOffset(service.GetView().PanelCorners, panel);
        Assert.True(offset < 1e-3f,
            $"the DEFAULT options dragged the panel {offset:F4} m from its anchored " +
            "place — the default must be strict world lock");
    }

    /// <summary>
    /// Switching back to strict world lock must snap the panel home.
    /// </summary>
    /// <remarks>
    /// The comfort-cone mode leaves the panel's rendered direction
    /// dragged away from its anchor.  Turning strict mode back on has
    /// to undo that immediately, because strict mode's whole promise is
    /// that the panel's world direction is the anchor's.
    /// </remarks>
    [Fact]
    public void Switching_To_Strict_World_Lock_Snaps_The_Panel_Home()
    {
        var (service, panel, driver) = MakeService(mode: WorldLockMode.ComfortCone);

        driver.TurnTo(60f, rampSeconds: 1.0f, holdSeconds: 2.0f);
        for (var i = 0; i < 120; i++)
        {
            service.GetView();
            Thread.Sleep(16);
        }

        // In comfort-cone mode the panel really does move.
        var dragged = HorizontalOffset(service.GetView().PanelCorners, panel);
        Assert.True(dragged > 0.01f,
            $"the comfort cone did not drag the panel at all ({dragged:F4} m) — " +
            "the test is not exercising the mode");

        service.ApplyOptions(new SpatialAnchorOptions
        {
            WorldLock = WorldLockMode.Strict,
            ComfortConeDegrees = 15f,
            ComfortConeRatePerSecond = 3f,
            PanelDistance = 1.5f,
            PanelFovFraction = 0.5f,
            PitchBiasDegrees = 0f,
        });

        var offset = HorizontalOffset(service.GetView().PanelCorners, panel);
        Assert.True(offset < 1e-3f,
            $"after switching to strict world lock the panel was still {offset:F4} m from " +
            "its anchored place");
    }

    // -----------------------------------------------------------------
    // Comfort cone — the opt-in AR idiom
    // -----------------------------------------------------------------

    /// <summary>
    /// Inside the comfort cone the panel is perfectly rigid.
    /// </summary>
    [Fact]
    public void Inside_The_Comfort_Cone_The_Panel_Is_Perfectly_Rigid()
    {
        var (service, panel, driver) = MakeService(mode: WorldLockMode.ComfortCone);

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

    /// <summary>
    /// Outside the comfort cone the panel glides back.
    /// </summary>
    /// <remarks>
    /// The comfort-cone mode is retained as an option because it is the
    /// standard AR idiom (MRTK's <c>RadialView</c>), so it has to keep
    /// working — but it is the behaviour the user rejected, so it must
    /// NOT be the default.
    /// </remarks>
    [Fact]
    public void Outside_The_Comfort_Cone_The_Panel_Glides_Back()
    {
        var (service, panel, driver) = MakeService(mode: WorldLockMode.ComfortCone);

        // The head has drifted 40 degrees away (the classic 3DoF yaw
        // drift).  Over a simulated second, the comfort cone must pull
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

    /// <summary>
    /// The panel stops at the cone edge, not at the gaze.
    /// </summary>
    /// <remarks>
    /// The expected edge is the EFFECTIVE cone, which is the
    /// configured one clamped to what the display can show.  With the
    /// default 50 %-FOV panel on a 44.24-degree display the clamp is
    /// 11.06 degrees, so a 15-degree setting behaves as an 11.06-degree
    /// cone.
    /// </remarks>
    [Fact]
    public void The_Panel_Stops_At_The_Cone_Edge_And_Not_The_Gaze()
    {
        var (service, panel, driver) = MakeService(mode: WorldLockMode.ComfortCone);

        // Let it settle.
        driver.TurnTo(40f);

        for (var i = 0; i < 200; i++)
        {
            service.GetView();
            Thread.Sleep(16);
        }

        var offset = HorizontalOffset(service.GetView().PanelCorners, panel);

        // The gaze is 40 degrees from the anchor, so the cone edge is
        // 40 - cone degrees away.
        var effectiveCone = MathF.Min(
            15f,
            SpatialAnchorOptions.MaxUnclippedConeDegrees(panelFovFraction: 0.5f));
        var expected = panel.Distance * 2f * MathF.Sin((40f - effectiveCone) * 0.5f * MathF.PI / 180f);
        Assert.InRange(offset, expected * 0.9f, expected * 1.1f);
    }

    /// <summary>
    /// The comfort cone is clamped to what the display can show.
    /// </summary>
    /// <remarks>
    /// The Rokid Max has a 44.24-degree horizontal field of view, so
    /// the bezel is at 22.12 degrees.  A 50 %-FOV panel's own
    /// half-width is 11.06 degrees, so a panel offset by theta has
    /// its outer edge at theta + 11.06.
    ///
    /// A 15-degree cone therefore puts the panel's edge at 26.06
    /// degrees — 3.94 degrees PAST the bezel, which is 17.8 % of the
    /// panel clipped off — while the gaze is still inside the "rigid"
    /// cone.  The comfort cone would be making a promise it cannot
    /// keep.
    ///
    /// This test is what the Gemini deep research caught: the clamp
    /// was missing, and an earlier note in this repository had
    /// rejected the idea on the grounds that it would "break the
    /// documented deadzone semantics".  That rejection was wrong —
    /// the clamp is exactly the semantics a comfort cone is supposed
    /// to have.
    /// </remarks>
    [Fact]
    public void The_Comfort_Cone_Never_Exceeds_What_The_Display_Can_Show()
    {
        // The clamp itself.
        var maxCone = SpatialAnchorOptions.MaxUnclippedConeDegrees(panelFovFraction: 0.5f);
        Assert.Equal(11.06f, maxCone, 2);

        // And the behaviour: with a 15-degree cone configured, the
        // panel must never come to rest at an offset that clips it.
        var (service, panel, driver) = MakeService(
            mode: WorldLockMode.ComfortCone, coneDegrees: 15f);

        driver.TurnTo(40f);
        for (var i = 0; i < 200; i++)
        {
            service.GetView();
            Thread.Sleep(16);
        }

        var offset = HorizontalOffset(service.GetView().PanelCorners, panel);

        // The panel comes to rest at the cone edge, measured from the
        // GAZE.  The offset above is measured from the ANCHOR, so the
        // two have to be combined:
        //
        //   panel angle from anchor  = 2*asin(offset / (2*distance))
        //   gaze angle from anchor   = 40 degrees
        //   panel edge from the gaze = (gaze - panel) + panel half-width
        //
        // With the clamp the panel sits 11.06 degrees from the anchor
        // less than the gaze, so its outer edge is at
        // 11.06 + 11.06 = 22.12 degrees — exactly the bezel.
        var panelAngleFromAnchor =
            2f * MathF.Asin(MathF.Min(1f, offset / (2f * panel.Distance))) * 180f / MathF.PI;
        var gazeAngleFromAnchor = 40f;
        var outerEdge = (gazeAngleFromAnchor - panelAngleFromAnchor) + 11.06f;

        Assert.True(outerEdge <= 22.12f + 0.5f,
            $"the panel came to rest with its outer edge at {outerEdge:F2} degrees from " +
            $"the gaze, but the bezel is at 22.12 degrees — it is clipped even though " +
            "the gaze is inside the comfort cone");
    }

    /// <summary>
    /// The panel keeps its distance from the anchor eye.
    /// </summary>
    [Fact]
    public void The_Panel_Keeps_Its_Distance_From_The_Anchor_Eye()
    {
        var (service, panel, driver) = MakeService(mode: WorldLockMode.ComfortCone);

        // The comfort cone rotates the panel about the point where it
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

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

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

    /// <summary>
    /// The panel's centre x in normalised device coordinates, where 0
    /// is the middle of the display and +-1 is its edge.
    /// </summary>
    private static float NdcCentreX(SpatialAnchorService service)
    {
        var view = service.GetView();
        var mvp = view.View * service.GetProjection();
        var centre = (view.PanelCorners[0] + view.PanelCorners[2]) * 0.5f;
        var clip = Vector4.Transform(new Vector4(centre, 1f), mvp);
        return clip.X / clip.W;
    }
}
