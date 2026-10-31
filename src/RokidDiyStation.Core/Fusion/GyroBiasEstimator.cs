// SPDX-License-Identifier: MIT
//
// Gyro bias estimator for the Rokid Max IMU.
//
// Even when the device is stationary, the gyroscope reports a small
// non-zero rate because of sensor offset, temperature drift and
// quantization.  We estimate that offset as a running average of the
// gyro reading taken over the samples where the device is clearly
// stationary, and subtract it from every subsequent sample so that a
// stationary device integrates to a constant orientation instead of a
// slow drift.
//
// The estimator only counts samples that are *clearly* stationary —
// small motions during the calibration period would otherwise bias the
// estimate.  The same notion of "stationary" is used by the position
// integrator's zero-velocity update, so the two pieces share a single
// detector.
//
// Why an EMA rather than a frozen average: the bias is not constant.
// The Rokid Max's display engine and USB controller heat the frame
// during the first minutes of use, and MEMS zero-rate offset is
// temperature-sensitive.  Measured on real hardware: the raw Y-axis
// bias was 0.00414 rad/s at the start of a run.  A frozen average
// therefore leaves a residual that grows with the warm-up, and the
// resulting yaw error accumulates linearly over a session.  An EMA with
// a slow time constant tracks the drift instead.
//
// The trade-off is that a slow EMA keeps some of the raw gyro noise in
// the estimate.  We accept that: the filter's proportional accel
// correction removes the noise it can, and the bias error is small
// compared with the drift we are cancelling.
using System.Numerics;

namespace RokidDiyStation.Core.Fusion;

/// <summary>
/// Adaptive gyro bias estimator.  Feed it every IMU sample that the
/// <see cref="StationaryDetector"/> has flagged as stationary; read the
/// running estimate from <see cref="Bias"/>.
/// </summary>
public sealed class GyroBiasEstimator
{
    private readonly double _tauSeconds;
    private Vector3 _bias;
    private int _sampleCount;
    private bool _primed;

    /// <summary>Running bias estimate (rad/s).  Zero until enough samples are collected.</summary>
    public Vector3 Bias => _bias;

    /// <summary>True once the estimator has collected enough samples to be trusted.</summary>
    public bool IsCalibrated => _primed;

    /// <summary>Number of stationary samples folded into the estimate.</summary>
    public int SampleCount => _sampleCount;

    /// <summary>Samples required before the estimate is considered usable.</summary>
    public const int MinSamplesForCalibration = 200;

    /// <param name="tauSeconds">
    /// Time constant of the exponential moving average, in seconds.  The
    /// estimate follows the raw gyro with this time constant while the
    /// device is stationary.  2 s is slow enough to average out sensor
    /// noise and fast enough to track thermal drift.
    /// </param>
    public GyroBiasEstimator(double tauSeconds = 2.0)
    {
        _tauSeconds = Math.Max(0.05, tauSeconds);
    }

    /// <summary>
    /// Add one stationary gyro sample.  No-op until the estimator has
    /// been primed; after priming it tracks the input with an EMA.
    /// </summary>
    public void Add(Vector3 gyro)
    {
        _sampleCount++;

        if (!_primed)
        {
            // Seed with a straight average of the first samples so a
            // single noisy reading cannot define the estimate.
            _bias = _sampleCount == 1 ? gyro : Vector3.Lerp(_bias, gyro, 1f / _sampleCount);
            if (_sampleCount >= MinSamplesForCalibration)
            {
                _primed = true;
            }
            return;
        }

        // EMA with a time constant.  The caller knows the sample rate, so
        // the step size is derived from it; see Add(gyro, dtSeconds).
        _bias = Vector3.Lerp(_bias, gyro, 1f - 1f / (float)Math.Max(1.0, _tauSeconds * 437.0));
    }

    /// <summary>
    /// Add one stationary gyro sample with an explicit timestep, which
    /// gives the correct EMA step size regardless of the sample rate.
    /// </summary>
    /// <param name="gyro">Raw (uncorrected) gyro reading.</param>
    /// <param name="dtSeconds">Time since the previous sample.</param>
    public void Add(Vector3 gyro, double dtSeconds)
    {
        _sampleCount++;

        if (!_primed)
        {
            _bias = _sampleCount == 1 ? gyro : Vector3.Lerp(_bias, gyro, 1f / _sampleCount);
            if (_sampleCount >= MinSamplesForCalibration)
            {
                _primed = true;
            }
            return;
        }

        var alpha = (float)Math.Clamp(dtSeconds / _tauSeconds, 0.0, 1.0);
        _bias = Vector3.Lerp(_bias, gyro, alpha);
    }

    /// <summary>Discard the current estimate.  Useful when the user re-calibrates.</summary>
    public void Reset()
    {
        _sampleCount = 0;
        _primed = false;
        _bias = Vector3.Zero;
    }
}
