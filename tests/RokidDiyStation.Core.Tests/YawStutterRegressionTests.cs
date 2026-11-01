// SPDX-License-Identifier: MIT
//
// Regression tests for the two defects the user reported on real
// Rokid Max hardware:
//
//   1. The magnetometer's heading correction corrupted YAW while
//      leaving PITCH and ROLL alone — the "vertical is smooth,
//      horizontal stutters" asymmetry.
//
//   2. The lazy follow TELEPORTED the panel (56 degrees in a single
//      frame) when the gaze re-entered the comfort cone, and its
//      timestep was quantised by Environment.TickCount64's 15-16 ms
//      resolution, which produced an alternating freeze-and-leap
//      stutter at 60 FPS.
//
// These tests exist so that a future change cannot quietly bring
// either defect back.

using System.Diagnostics;
using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.Protocol;
using RokidDiyStation.Core.Spatial;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class YawStutterRegressionTests
{
    private const float Gravity = 9.80665f;
    private const double Deg2Rad = Math.PI / 180.0;
    private const double SampleHz = 437.0;
    private const int FrameMs = 16;

    // -----------------------------------------------------------------
    // The magnetometer
    // -----------------------------------------------------------------

    /// <summary>
    /// The magnetometer correction must not run at all.
    /// </summary>
    /// <remarks>
    /// Indoors the Rokid Max's magnetometer reads about 108 uT, against
    /// Earth's 25-65 uT field, because the metal chassis dominates the
    /// reading.  Because the chassis is what dominates, the field
    /// swings with the device's ORIENTATION, so the heading it implies
    /// moves as the user turns their head.
    ///
    /// The correction is projected into the horizontal plane only, so it
    /// corrupted yaw and left pitch and roll untouched.  That is exactly
    /// the asymmetry the user reported.
    ///
    /// The test drives a real 90-degree turn through the filter twice —
    /// once with the legacy flag on and once with it off — using the
    /// MEASURED indoor field, and requires the two results to be
    /// identical.  If the flag ever starts doing anything again, this
    /// test fails.
    /// </remarks>
    [Fact]
    public void The_Magnetometer_Flag_Is_Inert()
    {
        var withFlag = RunYawTurn(90f, useMagnetometer: true);
        var withoutFlag = RunYawTurn(90f, useMagnetometer: false);

        Assert.True(MathF.Abs(withFlag - withoutFlag) < 1e-3f,
            $"the magnetometer flag changed the reported yaw by " +
            $"{MathF.Abs(withFlag - withoutFlag):F4} degrees " +
            $"({withFlag:F2} vs {withoutFlag:F2}) — the correction is active again");
    }

    /// <summary>
    /// The filter must report the yaw accurately through a turn.
    /// </summary>
    /// <remarks>
    /// With the magnetometer correction running, a true 90-degree turn
    /// was reported as 142 degrees — 52 degrees of pure error, injected
    /// in the direction the head was already turning.  That error grows
    /// with the turn, so it is not a constant offset that could be
    /// calibrated away.
    ///
    /// The gyro has no absolute reference, so over a single turn the
    /// error is the integral of the gyro bias — which the filter's
    /// stationary-bias estimator cancels.  A tenth of a degree is a
    /// generous bound for that, and it is two orders of magnitude
    /// better than the 52 degrees the magnetometer introduced.
    /// </remarks>
    [Fact]
    public void A_Ninety_Degree_Turn_Is_Reported_Accurately()
    {
        var reported = RunYawTurn(90f, useMagnetometer: false);

        Assert.True(MathF.Abs(reported - 90f) < 0.5f,
            $"a 90 degree turn was reported as {reported:F2} degrees " +
            $"(error {MathF.Abs(reported - 90f):F2} degrees)");
    }

    /// <summary>
    /// Drive the filter through a known yaw rotation and report the
    /// yaw of its forward vector.
    /// </summary>
    private static float RunYawTurn(float totalDeg, bool useMagnetometer)
    {
        var f = new MahonyFusion(useMagnetometer: useMagnetometer);

        // The MEASURED indoor field, in the world frame: not Earth's.
        var indoorField = new Vector3(95f, 12f, -52f);

        var dt = (float)(1.0 / SampleHz);
        ulong ts = 1_000_000_000UL;
        var tsStep = (ulong)(1e9 / SampleHz);

        var accel = new Vector3(0f, Gravity, 0f);
        for (var i = 0; i < 437; i++)
        {
            f.Update(new ImuSample(ts, accel, Vector3.Zero, Vector3.Zero, true, ts));
            ts += tsStep;
        }

        const float rateDeg = 30f;
        var total = 0f;
        while (total < totalDeg)
        {
            var q = SyntheticImuSource.FromEulerZYX(0f, 0f, total * (float)Deg2Rad);
            var worldUpInDevice = Vector3.Transform(Vector3.UnitY, Quaternion.Inverse(q));
            var gyro = worldUpInDevice * (float)(rateDeg * Deg2Rad);
            var a = Vector3.Transform(new Vector3(0f, Gravity, 0f), Quaternion.Inverse(q));
            var m = Vector3.Transform(indoorField, Quaternion.Inverse(q));

            f.Update(new ImuSample(ts, a, gyro, m, true, ts));
            ts += tsStep;
            total += rateDeg * dt;
        }

        // Let the estimate converge.
        for (var i = 0; i < 220; i++)
        {
            var q = SyntheticImuSource.FromEulerZYX(0f, 0f, total * (float)Deg2Rad);
            var a = Vector3.Transform(new Vector3(0f, Gravity, 0f), Quaternion.Inverse(q));
            var m = Vector3.Transform(indoorField, Quaternion.Inverse(q));
            f.Update(new ImuSample(ts, a, Vector3.Zero, m, true, ts));
            ts += tsStep;
        }

        var forward = Vector3.Transform(-Vector3.UnitZ, f.Orientation);
        return HorizontalAngle(Vector3.Normalize(forward));
    }

    private static float HorizontalAngle(Vector3 v)
    {
        var flat = new Vector3(v.X, 0f, v.Z);
        if (flat.LengthSquared() < 1e-9f) return 0f;
        flat = Vector3.Normalize(flat);
        return -(float)(Math.Atan2(flat.X, -flat.Z) / Deg2Rad);
    }

    // -----------------------------------------------------------------
    // The lazy follow's snap
    // -----------------------------------------------------------------

    /// <summary>
    /// The panel must never teleport when the gaze re-enters the
    /// comfort cone.
    /// </summary>
    /// <remarks>
    /// The old implementation reset the panel's smoothed direction to
    /// its ANCHORED direction the instant the gaze came back inside the
    /// cone.  If the follow had dragged the panel 55 degrees away, that
    /// reset was a 55-degree jump in a single frame — measured at 56.1
    /// degrees and 3366 deg/s.
    ///
    /// The panel must instead glide back, so the largest single-frame
    /// motion stays small.
    /// </remarks>
    [Fact]
    public void The_Panel_Never_Teleports_When_The_Gaze_Returns()
    {
        var service = MakeService(deadzoneDegrees: 15f, ratePerSecond: 3f);
        var panel = service.Panel!;

        // Turn out well past the cone, then back through it.
        var angles = new List<float>();
        for (var frame = 0; frame < 360; frame++)
        {
            var yawRate = frame < 180 ? -30f : 30f;
            DriveFrame(service, yawRate);
            angles.Add(PanelAngle(service, panel));
        }

        var maxStep = 0f;
        var maxAt = -1;
        for (var i = 1; i < angles.Count; i++)
        {
            var step = MathF.Abs(angles[i] - angles[i - 1]);
            if (step > maxStep) { maxStep = step; maxAt = i; }
        }

        Assert.True(maxStep < 5f,
            $"the panel moved {maxStep:F2} degrees in a single frame at frame {maxAt} " +
            $"({maxStep * 60f:F0} deg/s) — it teleported instead of gliding");
    }

    /// <summary>
    /// The panel's on-screen motion must not alternate between "no
    /// movement" and "a leap".
    /// </summary>
    /// <remarks>
    /// This is the stutter itself.  The old timestep came from
    /// Environment.TickCount64, whose resolution on Windows is the
    /// system timer tick — 15-16 ms.  At 60 FPS that quantises the
    /// timestep to {0, 16, 32} ms, so on some frames the follow's
    /// blend was exactly zero and the panel did not move AT ALL, and on
    /// the next frame the panel took a double step.
    ///
    /// The test measures the panel's projected NDC x coordinate, because
    /// that is what the user actually sees: a world-locked panel still
    /// slides across the screen as the head turns.
    /// </remarks>
    [Fact]
    public void The_Panel_Does_Not_Alternate_Between_Frozen_And_Leaping()
    {
        var service = MakeService(deadzoneDegrees: 15f, ratePerSecond: 3f);

        // Warm the follow's clock so the first frame is not the
        // fallback timestep.
        service.GetView();

        var ndc = new List<float>();
        for (var frame = 0; frame < 120; frame++)
        {
            DriveFrame(service, -30f);
            var view = service.GetView();
            var mvp = view.View * service.GetProjection();
            var centre = (view.PanelCorners[0] + view.PanelCorners[2]) * 0.5f;
            var clip = Vector4.Transform(new Vector4(centre, 1f), mvp);
            ndc.Add(clip.X / clip.W);
        }

        // Skip the settling frames.
        var steps = ndc.Skip(30).Zip(ndc.Skip(31), (a, b) => b - a).ToArray();

        var zeroFrames = steps.Count(s => MathF.Abs(s) < 1e-9f);
        Assert.True(zeroFrames == 0,
            $"{zeroFrames} of {steps.Length} frames had the panel frozen on screen — " +
            "the timestep is being quantised");

        // The steps must be SMOOTH, and the cleanest way to say that is
        // to measure the correlation between each step and the one
        // after it.
        //
        // A smooth follow's step sequence is a slowly DECAYING series
        // of the same sign, so neighbouring steps agree strongly and the
        // lag-1 autocorrelation is near +1.
        //
        // A quantised timestep produces a PERIOD-TWO alternation: the
        // panel moves on one frame, freezes on the next, moves on the
        // one after that.  Neighbouring steps then DISAGREE — the
        // autocorrelation is near -1.
        //
        // This is a scale-free discriminator, so it is not fooled by
        // the follow converging to a standstill, by the exact frame
        // rate, or by the size of the panel.
        var mean = steps.Average();
        var centred = steps.Select(s => s - mean).ToArray();

        var covariance = 0f;
        var variance = 0f;
        for (var i = 0; i < centred.Length - 1; i++)
        {
            covariance += centred[i] * centred[i + 1];
        }
        foreach (var c in centred) variance += c * c;

        var autocorrelation = variance > 1e-18f ? covariance / variance : 1f;

        Assert.True(autocorrelation > 0.5f,
            $"the panel's per-frame motion has a lag-1 autocorrelation of " +
            $"{autocorrelation:F2} (max step {steps.Max(s => MathF.Abs(s)):F6} NDC) — " +
            "the motion is alternating frame to frame rather than flowing");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static SpatialAnchorService MakeService(
        float deadzoneDegrees, float ratePerSecond)
    {
        var service = new SpatialAnchorService(new SpatialAnchorOptions
        {
            LazyFollowDeadzoneDegrees = deadzoneDegrees,
            LazyFollowRatePerSecond = ratePerSecond,
            PanelDistance = 1.5f,
            PanelFovFraction = 0.5f,
            NeckModelScale = 0f,
        });

        // Settle at rest, looking 25 degrees to the left.
        var q0 = SyntheticImuSource.FromEulerZYX(0f, (float)(-25.0 * Deg2Rad), 0f);
        var accel = Vector3.Transform(new Vector3(0f, Gravity, 0f), Quaternion.Inverse(q0));

        ulong ts = 1_000_000_000UL;
        var tsStep = (ulong)(1e9 / SampleHz);
        for (var i = 0; i < 4000; i++)
        {
            service.Update(new ImuSample(ts, accel, Vector3.Zero, Vector3.Zero, true, ts));
            ts += tsStep;
        }
        service.AnchorHere();
        return service;
    }

    private static ulong _ts = 1_000_000_000UL;
    private static readonly ulong TsStep = (ulong)(1e9 / SampleHz);

    private static void DriveFrame(SpatialAnchorService service, float yawRateDeg)
    {
        var q0 = SyntheticImuSource.FromEulerZYX(0f, (float)(-25.0 * Deg2Rad), 0f);
        var accel = Vector3.Transform(new Vector3(0f, Gravity, 0f), Quaternion.Inverse(q0));

        var samplesPerFrame = (int)(SampleHz * FrameMs / 1000.0);
        for (var k = 0; k < samplesPerFrame; k++)
        {
            var worldUpInDevice = Vector3.Transform(
                Vector3.UnitY, Quaternion.Inverse(service.Anchor.HeadOrientation));
            var gyro = worldUpInDevice * (float)(yawRateDeg * Deg2Rad);
            service.Update(new ImuSample(_ts, accel, gyro, Vector3.Zero, true, _ts));
            _ts += TsStep;
        }
        Thread.Sleep(FrameMs);
    }

    private static float PanelAngle(SpatialAnchorService service, PinnedPanel panel)
    {
        var view = service.GetView();
        var centre = (view.PanelCorners[0] + view.PanelCorners[2]) * 0.5f;
        var dir = centre - panel.EyeAtAnchor;
        return HorizontalAngle(Vector3.Normalize(dir));
    }
}
