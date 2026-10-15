// SPDX-License-Identifier: MIT
//
// Gyro bias estimator for the Rokid Max IMU.
//
// Even when the device is stationary, the gyroscope reports a small
// non-zero rate because of sensor offset, temperature drift and
// quantization.  We estimate that offset as the average gyro reading
// over a calibration window (typically the first 2-6 s after power-up)
// and subtract it from every subsequent sample so that a stationary
// device integrates to a constant orientation instead of a slow drift.
//
// The estimator only counts samples that are *clearly* stationary —
// small motions during the calibration period would otherwise bias the
// estimate.  The same notion of "stationary" is used by the position
// integrator's zero-velocity update, so the two pieces share a single
// detector.
using System.Numerics;

namespace RokidDiyStation.Core.Fusion;

/// <summary>
/// Sliding-window gyro bias estimator.  Feed it every IMU sample (after
/// the StationaryDetector flags them as such) and read the running
/// average from <see cref="Bias"/>.  After the calibration window
/// expires the bias is frozen unless the caller resets the estimator.
/// </summary>
public sealed class GyroBiasEstimator
{
    private readonly int _windowSize;
    private readonly Vector3[] _window;
    private int _fill;
    private int _index;
    private Vector3 _sum;
    private bool _frozen;

    /// <summary>Running bias estimate (rad/s).  Zero until enough samples are collected.</summary>
    public Vector3 Bias { get; private set; } = Vector3.Zero;

    /// <summary>True once the calibration window has been populated and frozen.</summary>
    public bool IsCalibrated => _frozen;

    /// <summary>Number of stationary samples currently in the window.</summary>
    public int SampleCount => _fill;

    public GyroBiasEstimator(int windowSize = 1024)
    {
        if (windowSize < 4) windowSize = 4;
        _windowSize = windowSize;
        _window = new Vector3[windowSize];
    }

    /// <summary>Add one stationary gyro sample.  No-op when <see cref="IsCalibrated"/> is true.</summary>
    public void Add(Vector3 gyro)
    {
        if (_frozen) return;
        if (_fill == _windowSize)
        {
            _sum -= _window[_index];
        }
        _window[_index] = gyro;
        _sum += gyro;
        _index = (_index + 1) % _windowSize;
        if (_fill < _windowSize) _fill++;
        if (_fill == _windowSize)
        {
            Bias = _sum / _windowSize;
            _frozen = true;
        }
    }

    /// <summary>Discard the current estimate.  Useful when the user re-calibrates.</summary>
    public void Reset()
    {
        _fill = 0;
        _index = 0;
        _sum = Vector3.Zero;
        _frozen = false;
        Bias = Vector3.Zero;
    }
}
