// SPDX-License-Identifier: MIT
//
// The state of the virtual 3D cursor.
//
// This is the piece of policy that the renderer and the input layer
// share: where the cursor is on the panel, and how it responds to the
// physical mouse.
//
// The mapping policy is "re-seeded differential":
//
//  * When the physical pointer ENTERS the glasses' display, the cursor
//    is placed at the point the mouse is actually pointing at (found
//    by ray-casting the mouse pixel onto the panel plane).
//  * From then on, the cursor only ever moves by the DELTA of the
//    mouse.  Turning the head does not move it at all, so the cursor
//    stays glued to the same word on the page.
//
// The differential part is what makes the cursor world-locked.  An
// absolute mapping would re-derive the cursor's position from the
// mouse pixel every frame, and since the mouse pixel is head-locked
// while the panel is world-locked, the cursor would slide across the
// content every time the user turned their head.
//
// The gain is expressed in the CAPTURED WINDOW's pixels, so one mouse
// count moves the cursor by one content pixel - the same feel as an
// ordinary monitor.
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
/// Where the virtual cursor is on the panel, and how it moves.
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
    /// True while the physical pointer is over the glasses' display.
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
    /// This is the re-seed, used when the pointer enters the glasses'
    /// display.  The coordinates are clamped, because the user may well
    /// enter through the black border around the panel, in which case
    /// the ray misses and the nearest edge is the right answer.
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
    /// Mark the cursor inactive - the pointer has left the glasses'
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
    /// Move the cursor by a mouse delta.
    /// </summary>
    /// <param name="deltaX">Mouse movement to the right, in source pixels.</param>
    /// <param name="deltaY">Mouse movement downwards, in source pixels.</param>
    /// <param name="contentWidth">The captured window's width in pixels.</param>
    /// <param name="contentHeight">The captured window's height in pixels.</param>
    /// <returns>True when the cursor actually moved.</returns>
    public bool Move(float deltaX, float deltaY, int contentWidth, int contentHeight)
    {
        if (deltaX == 0f && deltaY == 0f) return false;

        var w = PanelPointer.ContentWidth(contentWidth);
        var h = PanelPointer.ContentHeight(contentHeight);

        lock (_lock)
        {
            if (!_active) return false;

            var newU = Math.Clamp(_u + deltaX / w, 0f, 1f);
            var newV = Math.Clamp(_v + deltaY / h, 0f, 1f);

            if (newU == _u && newV == _v)
            {
                // Already at the edge.  Do not accumulate the overflow:
                // reversing direction must move the cursor immediately,
                // with no "wind-up" rubber-banding.
                return false;
            }

            _u = newU;
            _v = newV;
            _dirty = true;
            return true;
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
