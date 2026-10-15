// SPDX-License-Identifier: MIT
//
// Lightweight exponential smoothing for orientation and position.
//
// One-Euro filter would be nicer for fast head motion, but a simple
// EMA is more than enough for the "make the output less jittery" use
// case and avoids the magic cut-off frequency.  Coefficient 0 = no
// smoothing (raw output), 1 = frozen.
using System.Numerics;

namespace RokidDiyStation.App;

public sealed class OrientationSmoother
{
    private readonly double _alpha;
    private Quaternion _q = Quaternion.Identity;
    private Vector3 _pos;
    private bool _primed;

    public OrientationSmoother(double alpha)
    {
        _alpha = Math.Clamp(alpha, 0, 0.999);
    }

    public Quaternion Orientation => _q;
    public Vector3 Position => _pos;

    public void Reset()
    {
        _q = Quaternion.Identity;
        _pos = Vector3.Zero;
        _primed = false;
    }

    public (Quaternion Orientation, Vector3 Position) Update(Quaternion q, Vector3 p)
    {
        if (!_primed)
        {
            _q = q;
            _pos = p;
            _primed = true;
            return (_q, _pos);
        }
        // Quaternion SLERP for orientation, lerp for position.
        _q = Quaternion.Slerp(_q, q, (float)(1.0 - _alpha));
        _pos = Vector3.Lerp(_pos, p, (float)(1.0 - _alpha));
        return (_q, _pos);
    }
}
