// SPDX-License-Identifier: MIT
//
// The state of the virtual 3D cursor.
//
// This is the piece of policy that the renderer and the input layer
// share: where the cursor is on the panel.
//
// The mapping policy is "absolute"
// -------------------------------
// The cursor is placed at the point the OS cursor is at, mapped
// through the panel's texture coordinates:
//
//     u = (cursorX - monitorLeft)  / monitorWidth
//     v = (cursorY - monitorTop)   / monitorHeight
//
// The reason it is absolute - rather than the "re-seeded differential"
// model this class used to implement - is the virtual display.
//
// Under the differential model, the mouse moved over the glasses'
// REAL display, whose pixel grid has nothing to do with the captured
// content's.  So the cursor's position had to be re-derived from a
// ray-cast against the panel's plane, and the only thing that made it
// world-locked was that the cursor then moved by the mouse's DELTA.
//
// Now the mouse moves over the VIRTUAL DISPLAY, whose pixel grid IS
// the captured texture's.  So the mapping is a plain affine
// transform: one mouse pixel moves the cursor by exactly one content
// pixel, with no gain, no ray-cast and no drift.
//
// The signs (see PanelPointer.ToUv for where they come from):
//
//     u = 0 is the viewer's LEFT,  u = 1 the RIGHT.
//     v = 0 is the viewer's TOP,   v = 1 the BOTTOM.
//
// So moving the mouse RIGHT increases u, and moving it DOWN increases
// v.  Both are the "obvious" directions, but the derivation runs
// through a right-handed world frame in which the panel's "right" axis
// points towards the viewer's LEFT, so it is worth writing down.
namespace RokidDiyStation.Core.Spatial;

/// <summary>
/// Where the virtual cursor is on the panel.
/// </summary>
public sealed class VirtualCursor
{
    private readonly object _lock = new();

    private float _u = 0.5f;
    private float _v = 0.5f;
    private bool _active;
    private bool _dirty = true;

    /// <summary>
    /// The cursor's horizontal texture coordinate.  0 is the viewer's
    /// left, 1 the right.
    /// </summary>
    public float U { get { lock (_lock) return _u; } }

    /// <summary>
    /// The cursor's vertical texture coordinate.  0 is the viewer's
    /// top, 1 the bottom.
    /// </summary>
    public float V { get { lock (_lock) return _v; } }

    /// <summary>
    /// True while the cursor is over the virtual display.
    /// </summary>
    public bool IsActive { get { lock (_lock) return _active; } }

    /// <summary>
    /// True when the cursor has moved since this was last read.
    /// </summary>
    /// <remarks>
    /// The renderer only needs to rebuild the cursor quad when the
    /// position actually changed, so this avoids a pointless buffer
    /// upload on every frame.
    /// </remarks>
    public bool IsDirty
    {
        get { lock (_lock) { var d = _dirty; _dirty = false; return d; } }
    }

    /// <summary>
    /// Place the cursor at a specific point on the panel.
    /// </summary>
    /// <remarks>
    /// The coordinates are clamped, because a rounding error at the
    /// very edge of the virtual display could otherwise put the cursor
    /// a hair outside the panel.
    /// </remarks>
    public void Seed(float u, float v)
    {
        lock (_lock)
        {
            _u = Math.Clamp(u, 0f, 1f);
            _v = Math.Clamp(v, 0f, 1f);
            _active = true;
            _dirty = true;
        }
    }

    /// <summary>
    /// Mark the cursor inactive - the pointer has left the virtual
    /// display.
    /// </summary>
    public void Deactivate()
    {
        lock (_lock)
        {
            if (!_active) return;
            _active = false;
            _dirty = true;
        }
    }

    /// <summary>
    /// The cursor's current position, as a texture coordinate pair.
    /// </summary>
    public (float U, float V) Position
    {
        get { lock (_lock) return (_u, _v); }
    }
}
