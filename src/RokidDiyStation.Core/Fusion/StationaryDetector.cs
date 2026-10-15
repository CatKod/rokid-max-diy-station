// SPDX-License-Identifier: MIT
//
// Stationary detector for the Rokid Max IMU.
//
// A sample is "stationary" when both:
//   * |gyro| < GyroThreshold   (angular rate close to zero)
//   * |accel - G| < AccelThreshold  (linear acceleration close to 1g
//                                    in the device frame, i.e. the
//                                    only acceleration is gravity)
//
// We require the condition to hold for a minimum duration so a brief
// pause mid-motion doesn't trigger a false positive.  This is a
// textbook zero-velocity update (ZUPT) condition used by every
// pedestrian inertial navigation system.
using System.Numerics;

namespace RokidDiyStation.Core.Fusion;

public sealed class StationaryDetectorOptions
{
    /// <summary>Max angular rate (rad/s) considered "not rotating".</summary>
    public float GyroThreshold { get; init; } = 0.04f;

    /// <summary>Max deviation of |accel| from 1g (m/s²) considered "not translating".</summary>
    public float AccelThreshold { get; init; } = 0.6f;

    /// <summary>How long the condition must hold before we declare stationary.</summary>
    public TimeSpan MinDuration { get; init; } = TimeSpan.FromMilliseconds(150);
}

public enum StationaryState
{
    Moving = 0,
    /// <summary>The condition is currently being observed; not yet stable.</summary>
    Settling,
    Stationary,
}

public sealed class StationaryDetector
{
    private readonly StationaryDetectorOptions _opts;
    private ulong _settlingStartNs;
    private StationaryState _state;
    public const float GravityMagnitude = 9.80665f;

    public StationaryDetector(StationaryDetectorOptions? options = null)
    {
        _opts = options ?? new StationaryDetectorOptions();
        _state = StationaryState.Moving;
        _settlingStartNs = 0;
    }

    public StationaryState State => _state;

    public void Reset()
    {
        _state = StationaryState.Moving;
        _settlingStartNs = 0;
    }

    /// <summary>Feed one sample.  Returns the new state.</summary>
    public StationaryState Update(Vector3 gyro, Vector3 accel, ulong timestampNs)
    {
        var gyroQuiet = gyro.LengthSquared() < _opts.GyroThreshold * _opts.GyroThreshold;
        var accelDelta = accel.Length() - GravityMagnitude;
        var accelQuiet = MathF.Abs(accelDelta) < _opts.AccelThreshold;
        var quiet = gyroQuiet && accelQuiet;
        switch (_state)
        {
            case StationaryState.Moving:
                if (quiet)
                {
                    _settlingStartNs = timestampNs;
                    _state = StationaryState.Settling;
                }
                break;
            case StationaryState.Settling:
                if (!quiet)
                {
                    _state = StationaryState.Moving;
                    break;
                }
                if (timestampNs > _settlingStartNs
                    && (timestampNs - _settlingStartNs) >= (ulong)_opts.MinDuration.TotalMilliseconds * 1_000_000UL)
                {
                    _state = StationaryState.Stationary;
                }
                break;
            case StationaryState.Stationary:
                if (!quiet)
                {
                    _state = StationaryState.Moving;
                }
                break;
        }
        return _state;
    }
}
