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
            // Accelerometer at rest measures the *reaction* to gravity,
            // i.e. +1g along world up expressed in body frame.  We
            // rotate the world-up vector (+Gravity) by R_world_to_body
            // which is q*.
            var accel = Vector3.Transform(Gravity, Quaternion.Inverse(q)) + AccelBias;
            if (AccelNoiseSigma > 0) accel += NoiseVector(AccelNoiseSigma);
            // mag = R^T * magField
            var mag = Vector3.Transform(MagField, Quaternion.Inverse(q));
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

    /// <summary>Quaternion from intrinsic Z-Y-X Euler (R = Rz(roll) · Ry(yaw) · Rx(pitch)).</summary>
    /// <remarks>
    /// System.Numerics' Quaternion.CreateFromYawPitchRoll matches this
    /// convention directly: rotation order is Z (roll), then Y (yaw),
    /// then X (pitch) applied to a column vector.  We use it as the
    /// canonical forward transform; the inverse (Euler decomposition)
    /// is in <see cref="ToEulerZYX"/>.
    /// </remarks>
    public static Quaternion FromEulerZYX(float roll, float pitch, float yaw)
    {
        return Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
    }

    /// <summary>Decompose a quaternion to Z-Y-X intrinsic Euler in degrees.</summary>
    /// <remarks>
    /// Matches <see cref="FromEulerZYX"/>: inverse of
    /// <see cref="Quaternion.CreateFromYawPitchRoll(float, float, float)"/>,
    /// which implements the rotation R = Ry(yaw) · Rx(pitch) · Rz(roll).
    /// </remarks>
    public static (float Roll, float Pitch, float Yaw) ToEulerZYX(Quaternion q)
    {
        // Renormalize defensively — a quaternion that has drifted away
        // from unit length (e.g. after many float-precision additions
        // in the fusion filter) will produce Euler angles outside the
        // canonical (-180, 180] range.  The cost is one sqrt and three
        // multiplies, and we only do this on the 200 ms UI tick.
        var n2 = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
        if (n2 is > 1.0001f or < 0.9999f && n2 > 0f)
        {
            var inv = 1f / MathF.Sqrt(n2);
            q = new Quaternion(q.X * inv, q.Y * inv, q.Z * inv, q.W * inv);
        }
        // From R = Ry · Rx · Rz:
        //   R[1,2] = -sin(pitch) = 2(yz - wx)   =>  pitch = -asin(2(yz - wx))
        //   R[0,2] = sin(yaw)·cos(pitch)        = 2(xz + wy)
        //   R[2,2] = cos(yaw)·cos(pitch)        = 1 - 2(x² + y²)
        //   R[1,0] = cos(pitch)·sin(roll)       = 2(xy + wz)
        //   R[1,1] = cos(pitch)·cos(roll)       = 1 - 2(x² + z²)
        var sinp = -2f * (q.Y * q.Z - q.W * q.X);
        var pitch = MathF.Asin(Math.Clamp(sinp, -1f, 1f));
        if (MathF.Abs(sinp) > 0.99999f)
        {
            // Gimbal lock
            var yawLock = 2f * MathF.Atan2(q.X, q.W);
            return (0f, pitch * 180f / MathF.PI, yawLock * 180f / MathF.PI);
        }
        var siny = 2f * (q.X * q.Z + q.W * q.Y);
        var cosy = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        var yaw = MathF.Atan2(siny, cosy);
        var sinr = 2f * (q.X * q.Y + q.W * q.Z);
        var cosr = 1f - 2f * (q.X * q.X + q.Z * q.Z);
        var roll = MathF.Atan2(sinr, cosr);
        return (
            roll  * 180f / MathF.PI,
            pitch * 180f / MathF.PI,
            yaw   * 180f / MathF.PI);
    }
}
