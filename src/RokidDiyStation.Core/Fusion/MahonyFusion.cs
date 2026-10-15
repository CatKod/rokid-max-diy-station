// SPDX-License-Identifier: MIT
//
// Mahony AHRS filter for the Rokid Max 9-DoF IMU stream.
//
// Reference:  Sebastian Madgwick, "An efficient orientation filter for
//             inertial and inertial/magnetic sensor arrays" (2010),
//             Appendix A.  We follow the Mahony complementary filter
//             formulation (Mahony et al. 2008) as implemented in
//             Madgwick's open-source reference.
//
// All math uses the **device frame** as emitted by the Rokid Max parser:
//   accel in m/s²   (gravity vector ~ +Y when glasses are upright)
//   gyro  in rad/s
//   mag   in µT     (used only if useMagnetometer == true; indoors this is
//                    often unreliable, so callers can opt out)
//
// Quaternion convention: w + xi + yj + zk.  Identity quaternion represents
// the device in its rest orientation, i.e. the orientation at the first
// sample.  This keeps tests deterministic without having to know the
// absolute heading.

using System.Numerics;
using RokidDiyStation.Core.Protocol;

namespace RokidDiyStation.Core.Fusion;

public enum FusionResult
{
    /// <summary>Sample was consumed, orientation updated.</summary>
    Ok = 0,

    /// <summary>Sample was rejected (dt invalid, NaN, accel zero, etc.).</summary>
    Rejected,

    /// <summary>Filter is still warming up; output not reliable yet.</summary>
    Warmup,
}

/// <summary>
/// Tunable parameters for the Mahony filter.  Defaults are conservative
/// values that work well on the Rokid Max at 437 Hz.
/// </summary>
/// <param name="KpAccel">Proportional gain for accelerometer correction.</param>
/// <param name="KiAccel">Integral gain for accelerometer correction.</param>
/// <param name="KpMag">Proportional gain for magnetometer correction.</param>
/// <param name="KiMag">Integral gain for magnetometer correction.</param>
public readonly record struct MahonyTuning(
    float KpAccel = 2.0f,
    float KiAccel = 0.01f,
    float KpMag = 1.0f,
    float KiMag = 0.005f)
{
    public static readonly MahonyTuning Default = new();
}

/// <summary>
/// Stateful Mahony AHRS filter.  Not thread-safe: each tracking source
/// should own its own instance.
/// </summary>
public sealed class MahonyFusion
{
    private readonly MahonyTuning _tuning;
    private readonly bool _useMagnetometer;

    private Quaternion _q = Quaternion.Identity;
    private Vector3 _integralAccel;
    private Vector3 _integralMag;
    private ulong _prevTs;
    private bool _primed;
    private int _sampleCount;

    public MahonyFusion(MahonyTuning tuning = default, bool useMagnetometer = false)
    {
        _tuning = tuning;
        _useMagnetometer = useMagnetometer;
    }

    /// <summary>Current orientation estimate.  Stable only after <see cref="MinSamplesForStableEstimate"/> samples.</summary>
    public Quaternion Orientation => _q;

    /// <summary>Total samples successfully consumed.</summary>
    public int SampleCount => _sampleCount;

    /// <summary>How many samples we expect before the gyro bias estimate settles.</summary>
    public const int MinSamplesForStableEstimate = 200;

    public void Reset()
    {
        _q = Quaternion.Identity;
        _integralAccel = Vector3.Zero;
        _integralMag = Vector3.Zero;
        _prevTs = 0;
        _primed = false;
        _sampleCount = 0;
    }

    /// <summary>
    /// Feed one IMU sample.  Returns the status of the update.
    /// </summary>
    public FusionResult Update(in ImuSample sample)
    {
        // 1. dt
        if (!_primed)
        {
            if (!IsFinite(sample.Accel) || !IsFinite(sample.Gyro))
            {
                // Stay un-primed; caller can retry with a valid sample.
                return FusionResult.Rejected;
            }
            _prevTs = sample.TimestampNs;
            _primed = true;
            // First sample: seed orientation from accel so we don't get a
            // 90° startup transient.
            TrySeedFromAccel(in sample);
            _sampleCount++;
            return FusionResult.Warmup;
        }

        var dtNs = (long)sample.TimestampNs - (long)_prevTs;
        _prevTs = sample.TimestampNs;
        if (dtNs <= 0) return FusionResult.Rejected;
        var dt = dtNs * 1e-9f;
        if (!float.IsFinite(dt) || dt < 1e-4f || dt > 0.1f) return FusionResult.Rejected;

        // 2. Reference vectors
        var accel = sample.Accel;
        if (!IsFinite(accel)) return FusionResult.Rejected;
        var accelNorm = accel.Length();
        if (accelNorm < 1e-3f) return FusionResult.Rejected; // free-fall: skip
        var aHat = accel / accelNorm;

        Vector3? mHat = null;
        if (_useMagnetometer && IsFinite(sample.Mag) && sample.Mag.LengthSquared() > 1e-3f)
        {
            // Rotate magnetometer reading into the world frame so the
            // heading reference is in the horizontal plane only.
            var mWorld = Vector3.Transform(sample.Mag, _q);
            var mH = new Vector3(mWorld.X, 0, mWorld.Z);
            if (mH.LengthSquared() > 1e-3f)
            {
                mH = Vector3.Normalize(mH);
                mHat = Vector3.Transform(mH, Quaternion.Inverse(_q));
            }
        }

        // 3. Error terms
        //    f_a = q* ⊗ [0,a] ⊗ q - [0,g]; we work in device frame, so
        //    expected measurement is aHat (gravity projected to device),
        //    and the residual is the rotation that maps measured → expected.
        //    Madgwick's appendix A gives the closed-form cross products.
        var estUp = RotateByQuat(Vector3.UnitY, _q);
        var accelErr = Vector3.Cross(estUp, aHat);

        Vector3 magErr = Vector3.Zero;
        if (mHat is Vector3 mh)
        {
            var estNorth = RotateByQuat(Vector3.UnitX, _q);
            // Project both into horizontal plane (world Y=0)
            var measuredNorth = Vector3.Cross(Vector3.UnitY, Vector3.Cross(mh, Vector3.UnitY));
            if (measuredNorth.LengthSquared() > 1e-6f)
            {
                measuredNorth = Vector3.Normalize(measuredNorth);
                magErr = Vector3.Cross(estNorth, measuredNorth);
            }
        }

        var totalErr = accelErr * _tuning.KpAccel + magErr * _tuning.KpMag;
        _integralAccel += accelErr * _tuning.KiAccel * dt;
        _integralMag += magErr * _tuning.KiMag * dt;
        totalErr += _integralAccel + _integralMag;

        // 4. Apply correction to gyro (proportional + integral), then integrate
        var gyro = sample.Gyro;
        if (!IsFinite(gyro)) return FusionResult.Rejected;
        var corrected = gyro + totalErr;

        var gx = corrected.X * 0.5f * dt;
        var gy = corrected.Y * 0.5f * dt;
        var gz = corrected.Z * 0.5f * dt;

        // q_dot = 0.5 * q ⊗ [0, corrected]
        var dq = new Quaternion(
            -_q.X * gx - _q.Y * gy - _q.Z * gz,
             _q.W * gx + _q.Y * gz - _q.Z * gy,
            -_q.W * gy - _q.X * gz + _q.Z * gx,
             _q.W * gz + _q.X * gy - _q.Y * gx);

        _q = Quaternion.Normalize(_q + dq);
        _sampleCount++;
        return _sampleCount < MinSamplesForStableEstimate ? FusionResult.Warmup : FusionResult.Ok;
    }

    private void TrySeedFromAccel(in ImuSample sample)
    {
        var a = sample.Accel;
        if (!IsFinite(a)) return;
        var n = a.Length();
        if (n < 1e-3f) return;
        a /= n;
        // gravity points along +Y in rest pose; build a quaternion that
        // rotates UnitY onto `a`.
        var axis = Vector3.Cross(Vector3.UnitY, a);
        var axisLen = axis.Length();
        if (axisLen < 1e-6f)
        {
            // a == ±UnitY: either identity or 180° around X
            if (a.Y < 0)
            {
                _q = new Quaternion(1, 0, 0, 0); // 180° about X
            }
            return;
        }
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.UnitY, a), -1f, 1f));
        _q = Quaternion.CreateFromAxisAngle(axis / axisLen, angle);
    }

    private static Vector3 RotateByQuat(Vector3 v, Quaternion q)
    {
        // v' = q * v * q*
        var t = 2f * Vector3.Cross(new Vector3(q.X, q.Y, q.Z), v);
        return v + q.W * t + Vector3.Cross(new Vector3(q.X, q.Y, q.Z), t);
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
