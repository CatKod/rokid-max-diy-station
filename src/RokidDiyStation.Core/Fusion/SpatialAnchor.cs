// SPDX-License-Identifier: MIT
//
// Spatial anchor: ties together MahonyFusion (orientation), the gyro
// bias estimator and the position integrator into a single
// "where is the user's head in the world" service.
//
// State machine:
//
//   Booting   -- priming first sample ----------------------------+
//                                                                |
//   Calibrating -- waiting for enough stationary samples --------+|
//                                                                ||
//   Running   -- normal operation; user can call SetAnchorHere() ||
//                at any time to reset the world origin             ||
//                                                                ||
//                                                                ||
//   (re-call SetAnchorHere or Reset() any time) ------------------+|
//
// On SetAnchorHere():
//   * the current orientation becomes the world identity (i.e. the
//     anchor's world-frame quaternion becomes identity),
//   * the current position becomes the world origin (anchor's
//     world-frame position becomes (0,0,0)),
//   * the velocity and gyro bias estimates are *preserved* so the
//     stream doesn't have to re-calibrate.
using System.Numerics;
using RokidDiyStation.Core.Protocol;

namespace RokidDiyStation.Core.Fusion;

public enum AnchorState
{
    /// <summary>First sample not yet received.</summary>
    Booting,
    /// <summary>Collecting stationary samples for gyro bias.</summary>
    Calibrating,
    /// <summary>Live pose output available.</summary>
    Running,
}

public sealed class SpatialAnchor
{
    private readonly MahonyFusion _fusion;
    private readonly GyroBiasEstimator _bias = new(windowSize: 1024);
    private readonly StationaryDetector _stationary = new();
    private readonly PositionIntegrator _position = new();

    private AnchorState _state = AnchorState.Booting;
    private int _samplesSinceReset;

    public SpatialAnchor(MahonyTuning? tuning = null, bool useMagnetometer = true)
    {
        _fusion = new MahonyFusion(tuning ?? MahonyTuning.Default, useMagnetometer);
    }

    public AnchorState State => _state;
    public int SampleCount => _fusion.SampleCount;
    public int CalibrationProgress => Math.Min(1024, _bias.SampleCount);
    public bool IsGyroCalibrated => _bias.IsCalibrated;
    public Vector3 GyroBias => _bias.Bias;
    public bool IsStationary => _stationary.State == StationaryState.Stationary;

    /// <summary>Head orientation in the *anchor* (world) frame.</summary>
    public Quaternion HeadOrientation { get; private set; } = Quaternion.Identity;

    /// <summary>Head position in the *anchor* (world) frame, in metres.</summary>
    public Vector3 HeadPosition { get; private set; } = Vector3.Zero;

    public MahonyFusion Fusion => _fusion;

    /// <summary>Force a fresh state: forget bias, clear position, restart calibration.</summary>
    public void Reset()
    {
        _fusion.Reset();
        _bias.Reset();
        _stationary.Reset();
        _position.Reset();
        _state = AnchorState.Booting;
        _samplesSinceReset = 0;
        _autoAnchored = false;
        _anchorInv = (Quaternion.Identity, Vector3.Zero);
        HeadOrientation = Quaternion.Identity;
        HeadPosition = Vector3.Zero;
    }

    /// <summary>
    /// Mark the current head pose as the world origin.  Subsequent
    /// <see cref="HeadOrientation"/> / <see cref="HeadPosition"/>
    /// values are expressed in a frame where "here, looking forward"
    /// is the identity pose at the origin.
    /// </summary>
    public void SetAnchorHere()
    {
        // Capture the current head pose in the fusion's world frame so
        // we can transform future poses into the new anchor frame.
        // Quaternion.Inverse divides by |q|², which amplifies the
        // drift in a non-unit quaternion — so renormalise first.
        var q = _fusion.Orientation;
        var n2 = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
        if (n2 > 0f && MathF.Abs(n2 - 1f) > 1e-3f)
        {
            var inv = 1f / MathF.Sqrt(n2);
            q = new Quaternion(q.X * inv, q.Y * inv, q.Z * inv, q.W * inv);
        }
        _anchorInv = (Quaternion.Inverse(q), _position.Position);
        _stationary.Reset();
        // Publish the rebased pose immediately so callers see the
        // anchor origin without having to push another sample.
        HeadOrientation = Quaternion.Identity;
        HeadPosition = Vector3.Zero;
    }

    private (Quaternion Orientation, Vector3 Position) _anchorInv =
        (Quaternion.Identity, Vector3.Zero);

    /// <summary>Feed one IMU sample.  Updates orientation, position, calibration, stationary state.</summary>
    public AnchorState Update(in ImuSample sample)
    {
        if (_state == AnchorState.Booting)
        {
            _state = AnchorState.Calibrating;
        }

        // 1. Stationary detection on raw gyro/accel (before bias subtraction).
        var stationary = _stationary.Update(sample.Gyro, sample.Accel, sample.TimestampNs);
        var isStationary = stationary == StationaryState.Stationary;

        // 2. Gyro bias: feed it only when stationary, freeze when full.
        if (isStationary)
        {
            _bias.Add(sample.Gyro);
        }

        // 3. Subtract the running bias (zero until calibrated) and feed Mahony.
        var gyroCorrected = sample.Gyro - _bias.Bias;
        var sampleCorrected = sample with { Gyro = gyroCorrected };
        _fusion.Update(sampleCorrected);

        // 4. Compute world-frame linear accel = R*(accel_device) - gravity.
        //    We rotate by q (the head's orientation in world frame) and
        //    subtract (0, G, 0).
        var accelWorld = Vector3.Transform(sample.Accel, _fusion.Orientation)
                         - new Vector3(0, StationaryDetector.GravityMagnitude, 0);

        // 5. Position integration + ZUPT
        _position.Update(accelWorld, isStationary, sample.TimestampNs);

        // 6. Project into the anchor frame.  The anchor frame is set
        //    by SetAnchorHere(); until then it's identity (so output
        //    matches the head's world pose).
        var headWorldQ = _fusion.Orientation;
        var headWorldP = _position.Position;
        // Transform world-frame head pose into anchor frame:
        //   q_head_in_anchor = q_anchor_in_world⁻¹ * q_head_in_world
        //   p_head_in_anchor = q_anchor_in_world⁻¹ * (p_head_in_world - p_anchor_in_world)
        var qAnchorInv = _anchorInv.Orientation;
        var pAnchorInv = _anchorInv.Position;
        var headInAnchorQ = Quaternion.Multiply(qAnchorInv, headWorldQ);
        var headInAnchorP = Vector3.Transform(headWorldP - pAnchorInv, qAnchorInv);

        HeadOrientation = headInAnchorQ;
        HeadPosition = headInAnchorP;

        if (_fusion.SampleCount > MinSamplesForStableEstimate && _state == AnchorState.Calibrating)
        {
            _state = AnchorState.Running;
        }
        _samplesSinceReset++;
        return _state;
    }

    private bool _autoAnchored;

    public const int MinSamplesForStableEstimate = MahonyFusion.MinSamplesForStableEstimate;
}
