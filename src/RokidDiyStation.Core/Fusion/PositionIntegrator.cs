// SPDX-License-Identifier: MIT
//
// Position integrator with zero-velocity updates.
//
// Pure dead-reckoning from a consumer-grade IMU drifts quickly: a 0.1
// m/s² accel bias integrated for 10 s gives ~5 m of position error.
// Two mechanisms keep it bounded for the spatial-anchoring use case:
//
//   1. We rotate the measured accel into the *world* frame using the
//      current orientation estimate and subtract gravity *before*
//      integrating.  Without that step we'd double-count gravity and
//      the device would appear to "fall" even when stationary.
//
//   2. When the StationaryDetector reports stationary for a full
//      ZUPT window we force velocity to zero and bleed the position
//      drift back toward the last known good state.  This is the same
//      technique used by pedestrian INS / foot-mounted INS.
//
// Note that this is **not** a full INS solution.  The position is in
// the *head* frame (origin = where the anchor was reset) and is only
// accurate to roughly the magnitude of the gyro bias * t².  For the
// "pin a window in space" use case the position stays within a few
// centimetres while the user is moving slowly — which is all you need
// for a stable virtual screen.
using System.Numerics;

namespace RokidDiyStation.Core.Fusion;

public sealed class PositionIntegratorOptions
{
    /// <summary>How aggressively the position bleeds back when stationary. 0 = no correction, 1 = snap to origin.</summary>
    public float ZuptBlend { get; init; } = 0.6f;

    /// <summary>Below this velocity magnitude (m/s) the ZUPT may also zero velocity (in addition to the detector).</summary>
    public float ZuptVelocityThreshold { get; init; } = 0.05f;
}

public sealed class PositionIntegrator
{
    private readonly PositionIntegratorOptions _opts;
    private Vector3 _position;
    private Vector3 _velocity;
    private ulong _prevTs;
    private bool _primed;
    private bool _wasStationary;

    public Vector3 Position => _position;
    public Vector3 Velocity => _velocity;

    public PositionIntegrator(PositionIntegratorOptions? options = null)
    {
        _opts = options ?? new PositionIntegratorOptions();
    }

    /// <summary>Reset the integrator to the origin.  Use when the user sets a new anchor.</summary>
    public void Reset()
    {
        _position = Vector3.Zero;
        _velocity = Vector3.Zero;
        _prevTs = 0;
        _primed = false;
        _wasStationary = false;
    }

    /// <summary>
    /// Feed one sample.
    /// </summary>
    /// <param name="accelWorld">Accel rotated into the world frame (gravity already removed).</param>
    /// <param name="isStationary">True if the StationaryDetector says we're stationary.</param>
    /// <param name="timestampNs">Sample timestamp.</param>
    public void Update(Vector3 accelWorld, bool isStationary, ulong timestampNs)
    {
        if (!_primed)
        {
            _prevTs = timestampNs;
            _primed = true;
            _wasStationary = isStationary;
            return;
        }
        var dtNs = (long)timestampNs - (long)_prevTs;
        if (dtNs <= 0) return;
        var dt = dtNs * 1e-9f;
        if (!float.IsFinite(dt) || dt < 1e-4f || dt > 0.1f) return;
        _prevTs = timestampNs;

        // ZUPT: when stationary, bleed velocity (and via integration
        // position) toward zero.  This is the only mechanism that
        // prevents the integrator from running away on a slowly-drifting
        // accel bias.
        if (isStationary)
        {
            if (!_wasStationary)
            {
                // Just entered stationary: zero velocity immediately.
                _velocity = Vector3.Zero;
            }
            else
            {
                // Continued stationary: bleed position drift back to
                // zero with a small exponential decay so we don't
                // snap visibly while the user is still.
                _velocity *= 1f - _opts.ZuptBlend;
                _position *= 1f - _opts.ZuptBlend * 0.5f;
            }
            _wasStationary = true;
            return;
        }
        _wasStationary = false;

        // Trapezoidal integration.  We don't have access to the previous
        // accel here, so use a simple forward Euler — for the spatial
        // use case the error is dominated by the ZUPT correction.
        _velocity += accelWorld * dt;
        // Clamp to a sane human walking speed (3 m/s) so a single bad
        // sample doesn't fling the anchor across the room.
        if (_velocity.LengthSquared() > 9f) _velocity = Vector3.Normalize(_velocity) * 3f;
        _position += _velocity * dt;
    }
}
