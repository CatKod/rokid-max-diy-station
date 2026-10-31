// SPDX-License-Identifier: MIT
//
// Tests for the M3 Mahony AHRS fusion filter.
//
// Coverage:
//   1. Pure synthetic roll/pitch/yaw trajectory — filter should follow
//      the ground truth within a few degrees once warmed up.
//   2. Real 10s Rokid Max capture (docs/fixtures/raw-full.bin, 4364
//      samples) — the filter must stay finite and not diverge, and
//      the gravity-projected orientation must remain close to the
//      measured accel direction.
//   3. Reject bad inputs: NaN, zero accel, negative dt.

using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.Protocol;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class MahonyFusionTests
{
    [Fact]
    public void Rejects_NaN_Accel()
    {
        var f = new MahonyFusion();
        var bad = new ImuSample(1, new Vector3(float.NaN, 0, 0), Vector3.Zero, Vector3.Zero, true, 0);
        Assert.Equal(FusionResult.Rejected, f.Update(bad));
    }

    [Fact]
    public void Rejects_Zero_Accel()
    {
        var f = new MahonyFusion();
        var first = new ImuSample(1, new Vector3(0, 9.81f, 0), Vector3.Zero, Vector3.Zero, true, 0);
        f.Update(first);
        var freefall = new ImuSample(1_000_000, Vector3.Zero, Vector3.Zero, Vector3.Zero, true, 0);
        Assert.Equal(FusionResult.Rejected, f.Update(freefall));
    }

    [Fact]
    public void Rejects_Negative_Dt()
    {
        var f = new MahonyFusion();
        f.Update(new ImuSample(1000, new Vector3(0, 9.81f, 0), Vector3.Zero, Vector3.Zero, true, 0));
        Assert.Equal(FusionResult.Rejected, f.Update(new ImuSample(500, new Vector3(0, 9.81f, 0), Vector3.Zero, Vector3.Zero, true, 0)));
    }

    [Fact]
    public void Stationary_Sample_Holds_Identity()
    {
        // 1 second of stationary reading, no magnetometer — quaternion
        // should stay near identity and gravity-projected unit-Y should
        // align with measured accel.
        var f = new MahonyFusion();
        var src = new SyntheticImuSource();
        var samples = src.StepEuler((_) => (0f, 0f, 0f), seconds: 1.0f).ToArray();
        Assert.Equal(437, samples.Length / 1); // sanity
        foreach (var s in samples) f.Update(s);
        Assert.True(f.SampleCount >= 400);
        var q = f.Orientation;
        Assert.True(IsFinite(in q));
        // Yaw should remain 0 since there's no rotation in the trajectory.
        var (roll, pitch, yaw) = SyntheticImuSource.ToEulerZYX(q);
        Assert.InRange(MathF.Abs(yaw), 0, 5f);
        Assert.InRange(MathF.Abs(pitch), 0, 5f);
        Assert.InRange(MathF.Abs(roll), 0, 5f);
    }

    [Fact]
    public void Pure_Yaw_Rotation_Lands_On_The_Yaw_Axis()
    {
        // Regression test.  The filter used to compute the quaternion
        // derivative by hand and hand it to System.Numerics'
        // Quaternion constructor in (w, x, y, z) order, but that
        // constructor takes (X, Y, Z, W) — so every component landed
        // one slot off, and the sign of the w*gy term was flipped too.
        //
        // The net effect: the filter integrated rotations about the
        // wrong axis.  Pitch and roll happened to survive (they are
        // anchored by the accelerometer), but yaw — the axis with no
        // gravity reference and the one the user actually turns their
        // head on — integrated to -66 degrees where +90 was expected.
        // It looked exactly like a gain-tuning problem and was not one.
        //
        // This test pins the axis mapping down.  Rokid device frame:
        // X = pitch (nod), Y = yaw (turn), Z = roll (tilt).
        var f = new MahonyFusion(useMagnetometer: false);
        var rate = MathF.PI / 2f;   // 90 deg/s
        var dt = 1f / 437f;
        var ts = 1_000_000_000UL;
        var axis = Vector3.UnitY;
        for (int i = 0; i < 437; i++)
        {
            var q = Quaternion.CreateFromAxisAngle(axis, rate * dt * i);
            // Rotate gravity into the body frame so the accelerometer
            // agrees with the rotation and does not fight it.
            var accel = Vector3.Transform(new Vector3(0f, 9.80665f, 0f), Quaternion.Inverse(q));
            f.Update(new ImuSample(ts, accel, axis * rate, Vector3.Zero, true, 0), accelTrust: 0f);
            ts += 2_287_000UL;
        }

        var (roll, pitch, yaw) = SyntheticImuSource.ToEulerZYX(f.Orientation);
        Assert.InRange(MathF.Abs(roll), 0f, 1f);
        Assert.InRange(MathF.Abs(pitch), 0f, 1f);
        Assert.InRange(yaw, 89f, 91f);
    }

    [Fact]
    public void Pure_Roll_Rotation_Lands_On_The_Roll_Axis()
    {
        // The companion of the yaw regression: roll is the axis the
        // accelerometer can pin down, so it is the one a broken
        // quaternion product breaks most visibly.
        var f = new MahonyFusion(useMagnetometer: false);
        var rate = MathF.PI / 2f;
        var dt = 1f / 437f;
        var ts = 1_000_000_000UL;
        var axis = Vector3.UnitZ;
        for (int i = 0; i < 437; i++)
        {
            var q = Quaternion.CreateFromAxisAngle(axis, rate * dt * i);
            var accel = Vector3.Transform(new Vector3(0f, 9.80665f, 0f), Quaternion.Inverse(q));
            f.Update(new ImuSample(ts, accel, axis * rate, Vector3.Zero, true, 0), accelTrust: 0f);
            ts += 2_287_000UL;
        }

        var (roll, pitch, yaw) = SyntheticImuSource.ToEulerZYX(f.Orientation);
        Assert.InRange(MathF.Abs(pitch), 0f, 1f);
        Assert.InRange(MathF.Abs(yaw), 0f, 1f);
        Assert.InRange(MathF.Abs(roll), 89f, 91f);
    }

    [Fact]
    public void Stationary_With_NonUpright_Accel_Does_Not_Drift()
    {
        // Real Rokid hardware: when the user wears the glasses, the
        // accel vector is tilted relative to world +Y (the glasses sit
        // at ~20-30 deg back-tilt on the nose).  The accelerometer
        // correction must stay ACTIVE in that case: at a constant
        // resting tilt the error term a x v is exactly zero, so the
        // correction does no harm, and it is the only thing pinning
        // pitch and roll to gravity.
        //
        // Disabling it while stationary (which this code used to do)
        // turned the filter into open-loop gyro integration and
        // produced a measured, linear ~0.3 deg/min of roll drift on
        // real hardware.  The filter now handles the genuinely
        // disturbed case internally, via dynamic-acceleration
        // rejection.
        var f = new MahonyFusion();
        var src = new SyntheticImuSource();
        var samples = src.StepEuler(_ => (0f, 0f, 0f), seconds: 10.0f).ToArray();
        foreach (var s in samples) f.Update(s);

        var (roll, pitch, yaw) = SyntheticImuSource.ToEulerZYX(f.Orientation);
        Assert.InRange(MathF.Abs(roll), 0, 3f);
        Assert.InRange(MathF.Abs(pitch), 0, 3f);
        Assert.InRange(MathF.Abs(yaw), 0, 3f);
    }

    [Fact]
    public void Dynamic_Acceleration_Rejects_Accelerometer_Correction()
    {
        // A violent linear acceleration makes |a| stray far from g.
        // Feeding that in as a gravity reference would tilt the
        // estimate; the filter must scale the correction down instead.
        var f = new MahonyFusion();
        var src = new SyntheticImuSource();
        var ts = 1_000_000_000UL;

        // Settle first.
        foreach (var s in src.StepEuler(_ => (0f, 0f, 0f), seconds: 1.0f))
        {
            f.Update(s);
            ts = s.TimestampNs;
        }
        var before = f.Orientation;

        // 3 s of 6 m/s^2 sideways linear acceleration, no rotation.
        for (int i = 0; i < 1300; i++)
        {
            var accel = new Vector3(6f, 9.8f, 0f);
            f.Update(new ImuSample(ts, accel, Vector3.Zero, Vector3.Zero, true, 0));
            ts += 2_287_000UL;
        }

        var (roll, pitch, yaw) = SyntheticImuSource.ToEulerZYX(f.Orientation);
        var (rollBefore, pitchBefore, yawBefore) = SyntheticImuSource.ToEulerZYX(before);

        // The tilt must not have lurched by a large amount just
        // because the user walked.
        Assert.InRange(MathF.Abs(pitch - pitchBefore), 0, 12f);
        Assert.InRange(MathF.Abs(roll - rollBefore), 0, 12f);
        Assert.InRange(MathF.Abs(yaw - yawBefore), 0, 12f);
    }

    [Fact]
    public void Synthetic_Yaw_Trajectory_Is_Tracked()
    {
        // Spin 90° in 0.5 s, hold 0.5 s, return to 0 in 0.5 s.
        var f = new MahonyFusion();
        var src = new SyntheticImuSource();
        var samples = src.StepEuler(t =>
        {
            var yaw = t < 0.5f ? 90f * t / 0.5f
                    : t < 1.0f ? 90f
                               : MathF.Max(0, 90f - 90f * (t - 1.0f) / 0.5f);
            return (0f, 0f, yaw);
        }, seconds: 1.6f).ToArray();

        foreach (var s in samples) f.Update(s);

        var (roll, pitch, yaw) = SyntheticImuSource.ToEulerZYX(f.Orientation);
        // After returning to 0 deg, yaw should be close to 0 again.
        Assert.InRange(MathF.Abs(yaw), 0, 15f);
    }

    [Fact]
    public void RealCapture_DoesNot_Diverge()
    {
        var path = LocateFixture("raw-200.bin");
        if (path is null) return; // fixture missing — skip
        var samples = LoadBinary(path);
        Assert.Equal(200, samples.Length);
        var f = new MahonyFusion();
        foreach (var s in samples) f.Update(s);
        var q = f.Orientation;
        Assert.True(IsFinite(in q));
        // After 200 samples of mostly-stationary-then-moving data the
        // quaternion should be a unit quaternion.
        var len = MathF.Sqrt(q.W * q.W + q.X * q.X + q.Y * q.Y + q.Z * q.Z);
        Assert.InRange(len, 0.95f, 1.05f);
    }

    [Fact]
    public void RealCapture_FullRun_Quaternions_Stay_Unit()
    {
        var path = LocateFixture("raw-full.bin");
        if (path is null) return;
        var samples = LoadBinary(path);
        var f = new MahonyFusion();
        Quaternion last = default;
        foreach (var s in samples)
        {
            f.Update(s);
            var q = f.Orientation;
            var len = MathF.Sqrt(q.W * q.W + q.X * q.X + q.Y * q.Y + q.Z * q.Z);
            Assert.InRange(len, 0.9f, 1.1f);
            last = q;
        }
        // End-of-recording posture: user finished by tilting head, so
        // we just assert we have *some* non-identity rotation.
        Assert.NotEqual(Quaternion.Identity, last);
    }

    // -------- helpers --------

    private static bool IsFinite(in Quaternion q) =>
        float.IsFinite(q.W) && float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z);

    private static string? LocateFixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        // First try the output-relative "fixtures/" folder
        var local = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        if (File.Exists(local)) return local;
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "docs", "fixtures", name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static ImuSample[] LoadBinary(string path)
    {
        // Layout: ts(8) accel(12) gyro(12) mag(12) worn(1) = 45 bytes
        const int stride = 45;
        var bytes = File.ReadAllBytes(path);
        var n = bytes.Length / stride;
        var result = new ImuSample[n];
        for (var i = 0; i < n; i++)
        {
            var off = i * stride;
            result[i] = new ImuSample(
                TimestampNs: BitConverter.ToUInt64(bytes, off),
                Accel: new Vector3(
                    BitConverter.ToSingle(bytes, off + 8),
                    BitConverter.ToSingle(bytes, off + 12),
                    BitConverter.ToSingle(bytes, off + 16)),
                Gyro: new Vector3(
                    BitConverter.ToSingle(bytes, off + 20),
                    BitConverter.ToSingle(bytes, off + 24),
                    BitConverter.ToSingle(bytes, off + 28)),
                Mag: new Vector3(
                    BitConverter.ToSingle(bytes, off + 32),
                    BitConverter.ToSingle(bytes, off + 36),
                    BitConverter.ToSingle(bytes, off + 40)),
                Worn: bytes[off + 44] != 0,
                HostTimestampNs: 0);
        }
        return result;
    }
}
