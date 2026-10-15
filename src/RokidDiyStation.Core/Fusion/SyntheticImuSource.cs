// SPDX-License-Identifier: MIT
//
// Synthetic IMU generator for fusion unit tests.
//
// We integrate ground-truth Euler angles (Z-Y-X intrinsic) to produce
// ideal accel/gyro, then add optional noise and bias so the Mahony
// filter has something realistic to chase.  Magnetometer is generated
// from a fixed reference field rotated by the true orientation.
using System.Numerics;
using RokidDiyStation.Core.Protocol;

namespace RokidDiyStation.Core.Fusion;

public sealed class SyntheticImuSource
{
    private readonly Random _rng;
    public Vector3 Gravity { get; } = new(0, 9.80665f, 0);
    public Vector3 MagField { get; } = new(20f, 0f, -45f); // arbitrary µT
    public Vector3 GyroBias { get; set; } = Vector3.Zero;
    public Vector3 AccelBias { get; set; } = Vector3.Zero;
    public float GyroNoiseSigma { get; set; } = 0f;
    public float AccelNoiseSigma { get; set; } = 0f;
    public float MagNoiseSigma { get; set; } = 0f;
    public float RateHz { get; }
    public ulong StartTs { get; }

    private float _t;

    public SyntheticImuSource(float rateHz = 437f, ulong startTs = 1_000_000_000UL, int seed = 42)
    {
        RateHz = rateHz;
        StartTs = startTs;
        _rng = new Random(seed);
    }

    /// <summary>Move the simulation forward.  Returns samples covering the next window.</summary>
    public IEnumerable<ImuSample> StepEuler(
        Func<float, (float rollDeg, float pitchDeg, float yawDeg)> trajectory,
        float seconds)
    {
        var dt = 1f / RateHz;
        var n = (int)(seconds * RateHz);
        var prev = trajectory(0);
        for (var i = 0; i < n; i++)
        {
            _t += dt;
            var cur = trajectory(_t);
            // angular velocity in deg/s -> rad/s
            var rollRate = (cur.rollDeg - prev.rollDeg) / dt * MathF.PI / 180f;
            var pitchRate = (cur.pitchDeg - prev.pitchDeg) / dt * MathF.PI / 180f;
            var yawRate = (cur.yawDeg - prev.yawDeg) / dt * MathF.PI / 180f;
            // Approximate body rates for Z-Y-X Euler: (roll,pitch,yaw) =
            // (x=pitch-axis, y=yaw-axis, z=roll-axis) per Rokid device frame.
            var gyro = new Vector3(pitchRate, yawRate, rollRate) + GyroBias;
            if (GyroNoiseSigma > 0)
            {
                gyro += NoiseVector(GyroNoiseSigma);
            }
            prev = cur;

            // Build quaternion from current Euler (Z-Y-X intrinsic)
            var q = FromEulerZYX(cur.rollDeg * MathF.PI / 180f, cur.pitchDeg * MathF.PI / 180f, cur.yawDeg * MathF.PI / 180f);
            // accel = R^T * gravity
            var accel = Rotate(Vector3.Transform(-Gravity, q)) + AccelBias;
            if (AccelNoiseSigma > 0) accel += NoiseVector(AccelNoiseSigma);
            // mag = R^T * magField
            var mag = Rotate(Vector3.Transform(MagField, q));
            if (MagNoiseSigma > 0) mag += NoiseVector(MagNoiseSigma);

            yield return new ImuSample(
                TimestampNs: StartTs + (ulong)(_t * 1e9f),
                Accel: accel,
                Gyro: gyro,
                Mag: mag,
                Worn: true,
                HostTimestampNs: 0);
        }
    }

    private Vector3 NoiseVector(float sigma)
    {
        // Box-Muller
        var u1 = 1 - _rng.NextDouble();
        var u2 = 1 - _rng.NextDouble();
        var n = MathF.Sqrt(-2f * MathF.Log((float)u1));
        var x = n * MathF.Cos(2f * MathF.PI * (float)u2);
        var y = n * MathF.Sin(2f * MathF.PI * (float)u2);
        var z = MathF.Sqrt(-2f * MathF.Log((float)_rng.NextDouble())) * MathF.Cos(2f * MathF.PI * (float)_rng.NextDouble());
        return new Vector3(x, y, z) * sigma;
    }

    private static Vector3 Rotate(Vector3 v) => v; // already in world->body form

    /// <summary>Quaternion from Z-Y-X intrinsic Euler (yaw-pitch-roll applied in that order).</summary>
    public static Quaternion FromEulerZYX(float roll, float pitch, float yaw)
    {
        var cr = MathF.Cos(roll * 0.5f); var sr = MathF.Sin(roll * 0.5f);
        var cp = MathF.Cos(pitch * 0.5f); var sp = MathF.Sin(pitch * 0.5f);
        var cy = MathF.Cos(yaw * 0.5f); var sy = MathF.Sin(yaw * 0.5f);
        return new Quaternion(
            cr * cp * cy + sr * sp * sy,
            sr * cp * cy - cr * sp * sy,
            cr * sp * cy + sr * cp * sy,
            cr * cp * sy - sr * sp * cy);
    }

    /// <summary>Decompose a quaternion to Z-Y-X intrinsic Euler in degrees.</summary>
    public static (float Roll, float Pitch, float Yaw) ToEulerZYX(Quaternion q)
    {
        // Z-Y-X (yaw-pitch-roll) intrinsic, with axes x=pitch, y=yaw, z=roll (Rokid device frame)
        // For Tait-Bryan standard (X=pitch, Y=yaw, Z=roll), the matrix R = Rz * Ry * Rx.
        // Here we recover (roll, pitch, yaw) directly.
        // Use: roll = atan2(2(wz+xy), 1-2(z^2+x^2))
        //      pitch = -asin(2(wx - yz))  // extrinsic pitch = intrinsic x
        //      yaw = atan2(2(wy+xz), 1-2(x^2+y^2))
        var sinr = 2f * (q.W * q.Z + q.X * q.Y);
        var cosr = 1f - 2f * (q.Z * q.Z + q.X * q.X);
        var roll = MathF.Atan2(sinr, cosr);
        var sinp = 2f * (q.W * q.X - q.Y * q.Z);
        var pitch = MathF.Asin(Math.Clamp(sinp, -1f, 1f));
        var siny = 2f * (q.W * q.Y + q.Z * q.X);
        var cosy = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        var yaw = MathF.Atan2(siny, cosy);
        return (
            roll * 180f / MathF.PI,
            pitch * 180f / MathF.PI,
            yaw * 180f / MathF.PI);
    }
}
