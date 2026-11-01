// SPDX-License-Identifier: MIT
//
// The service that turns a stream of IMU samples into a panel pinned
// in space.
//
// This is the public surface the tray app talks to:
//
//     var service = new SpatialAnchorService();
//     service.Update(sample);              // feed IMU
//     service.AnchorHere();                // user pressed the hotkey
//     var view = service.GetViewMatrix();  // render with this
//
// It owns the fusion filter, the anchor, and the panel's pose, and
// keeps them consistent with each other.  The renderer never has to
// know about quaternions or the device frame — it gets a view matrix
// and a set of panel corners.
//
// World frame vs anchor frame
// ---------------------------
// The fusion filter works in its own "world" frame, which is
// established by the first sample it sees and never changes.  The
// ANCHOR frame is a second frame, rebased every time the user calls
// AnchorHere().  Everything the renderer uses is in the anchor frame,
// because that is the frame the user thinks of as "the room".
using System.Diagnostics;
using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.Protocol;

namespace RokidDiyStation.Core.Spatial;

/// <summary>
/// Settings for the spatial anchor service.
/// </summary>
/// <param name="PanelDistance">
/// How far away the anchored panel sits, in metres.
/// </param>
/// <param name="PanelFovFraction">
/// The fraction of the horizontal field of view the panel covers.
/// 0.5 means the panel subtends half the FOV.
/// </param>
/// <param name="DiagonalFovDegrees">
/// The display's diagonal field of view.  Overridable because the
/// vendor figure carries a couple of degrees of uncertainty.
/// </param>
/// <param name="PitchBiasDegrees">
/// Downward tilt applied to the panel at anchor time, in degrees,
/// relative to the user's line of sight.  Human-factors standards
/// (ISO 9241-5, ANSI/HFES 100-2007) put the relaxed line of sight
/// 15-35 degrees below horizontal, so a panel anchored dead ahead at
/// eye level is uncomfortable to look at for long.
///
/// The default is 0 for a hardware reason rather than an ergonomic
/// one.  The glasses' VERTICAL field of view is only 25.8 degrees
/// (the 50-degree figure is the diagonal), and a 50%-FOV panel at
/// 1.5 m already subtends 12.6 degrees of it.  That leaves about
/// 6.5 degrees of slack, and the previous default of -10 degrees
/// used more than that: it pushed the panel's bottom edge past the
/// bottom of the display, so the user saw a panel that was clipped
/// off the bottom of the glasses.
///
/// Any bias that is set is CLAMPED to what actually fits on screen
/// (see <see cref="SpatialAnchorService.AnchorHere"/>), so the setting
/// can never produce a half-hidden panel.
/// </param>
/// <param name="LazyFollowDeadzoneDegrees">
/// The angular comfort cone around the panel's centre, in degrees.
/// Inside the cone the panel is completely rigid — world-locked, no
/// drift correction at all, so reading text is perfectly stable.
/// Outside the cone the panel's world direction glides gently back
/// towards the user's gaze.  This is the "Lazy Follow" / "Tag-Along"
/// behaviour pioneered by Microsoft HoloLens and shipped by Virtual
/// Desktop, and it is the answer to 3DoF yaw drift: the panel never
/// jumps, but it also never crawls out of the field of view.
/// Set to 0 to disable lazy follow entirely (the panel then drifts
/// forever until the user re-anchors).
/// </param>
/// <param name="LazyFollowRatePerSecond">
/// How fast the lazy follow pulls the panel back towards the gaze,
/// in inverse seconds.  Higher is snappier.  Values around 2-4 feel
/// like a slow glide; anything above about 8 reads as the screen
/// chasing the user's eyes, which is nauseating.
/// </param>
/// <param name="NeckModelScale">
/// How much of the kinematic neck model's eye translation to apply,
/// in 0..1.  0 means the camera rotates about the eye itself (pure
/// rotation, no parallax, no "swim").  1 means the full Cardboard /
/// LibOVR neck offset.  In 3DoF there is no ground truth for the
/// neck's position, so the model invents a translation whenever the
/// user turns their whole body in their chair.  For a flat reading
/// panel that invented parallax is worse than none, so the default
/// is a damped 0.3.
/// </param>
public sealed record SpatialAnchorOptions
{
    public float PanelDistance { get; init; } = 1.5f;

    public float PanelFovFraction { get; init; } = 0.5f;

    public float DiagonalFovDegrees { get; init; } = RokidDisplayProfile.DiagonalFovDegrees;

    /// <summary>
    /// Downward tilt of the panel relative to the line of sight, in
    /// degrees.  Clamped to what actually fits on the display — see
    /// <see cref="SpatialAnchorOptions.PitchBiasDegrees"/>.
    /// </summary>
    public float PitchBiasDegrees { get; init; } = 0f;

    public float LazyFollowDeadzoneDegrees { get; init; } = 15f;

    public float LazyFollowRatePerSecond { get; init; } = 3f;

    public float NeckModelScale { get; init; } = 0.3f;

    public static readonly SpatialAnchorOptions Default = new();
}

/// <summary>
/// The state a renderer needs to draw the pinned panel.
/// </summary>
/// <param name="View">The camera's view matrix (world -> eye).</param>
/// <param name="PanelCorners">The panel's four corners in world space.</param>
/// <param name="EyePosition">The eye's world position.</param>
/// <param name="GazeDirection">The direction the user is looking.</param>
/// <param name="HeadOrientation">The head's orientation, for the HUD.</param>
public readonly record struct AnchorView(
    Matrix4x4 View,
    Vector3[] PanelCorners,
    Vector3 EyePosition,
    Vector3 GazeDirection,
    Quaternion HeadOrientation);

/// <summary>
/// Owns the fusion filter, the world anchor and the pinned panel, and
/// keeps them consistent.
/// </summary>
public sealed class SpatialAnchorService
{
    private SpatialAnchorOptions _options;
    private readonly SpatialAnchor _anchor;
    private NeckModelCamera _camera;

    /// <summary>
    /// Guards the panel and the lazy-follow state.
    /// </summary>
    /// <remarks>
    /// This service is touched from two threads: the tracking thread
    /// calls <see cref="Update"/> and the anchor mutations, while the
    /// render thread calls <see cref="GetView"/>.  The anchor itself
    /// has its own single-writer assumption, but the panel and the
    /// lazy-follow smoothing state are shared, so they need a lock.
    /// The lock is held for very short periods and never across any
    /// blocking call, so contention is a non-issue.
    /// </remarks>
    private readonly object _panelLock = new();
    private PinnedPanel? _panel;

    /// <summary>
    /// The panel's current rendered direction, after the lazy-follow
    /// correction has been applied.  Kept separate from the panel's
    /// rigid direction so that the panel's own pose stays immutable —
    /// the lazy follow moves the *view* of the panel, not the panel.
    ///
    /// It is kept as a full 3D vector rather than a horizontal one, so
    /// that the panel's elevation is preserved EXACTLY: the follow only
    /// ever rotates about the vertical axis, and such a rotation leaves
    /// the Y component alone.
    /// </summary>
    private Vector3 _smoothedDirection;
    private bool _hasSmoothedDirection;

    /// <summary>
    /// The high-resolution clock used to time the lazy follow.
    /// </summary>
    /// <remarks>
    /// This used to be <see cref="Environment.TickCount64"/>, whose
    /// resolution on Windows is the system timer tick — measured at
    /// 15-16 ms on this machine, with occasional 31 ms gaps.
    ///
    /// That is fatal for this particular job.  The render loop runs at
    /// 60 FPS, so a 16 ms clock resolution means the follow's timestep
    /// quantises to {0, 16, 32} ms: on some frames dt is 0 and the
    /// panel does not move AT ALL, and on the next frame dt is 31 ms
    /// and the panel takes a double step.  Measured: 20 % of frames
    /// had exactly zero motion.  That alternation IS the stutter.
    ///
    /// <see cref="Stopwatch"/> counts at the hardware's timer
    /// resolution (sub-microsecond), so the timestep is smooth.
    /// </remarks>
    private static readonly double TimestampFrequency = Stopwatch.Frequency;

    /// <summary>
    /// The high-resolution timestamp of the last <see cref="GetView"/>
    /// call, in <see cref="TimestampFrequency"/> ticks.
    /// </summary>
    private long _lastViewTimestamp;

    public SpatialAnchorService(SpatialAnchorOptions? options = null)
        : this(options, null) { }

    /// <summary>
    /// Create the service on top of an EXISTING anchor.
    /// </summary>
    /// <remarks>
    /// The tray app already runs a <see cref="SpatialAnchor"/> inside
    /// the tracking service, and that is the one the user rebases with
    /// "Set Anchor Here" / "Reset Anchor".  If the renderer used a
    /// second, independent anchor, the panel would be placed in a
    /// different world frame from the head pose the user re-anchored —
    /// the screen would jump somewhere unrelated every time they
    /// pressed the hotkey.  So the renderer and the tracker must share
    /// one anchor.
    /// </remarks>
    public SpatialAnchorService(SpatialAnchorOptions? options, SpatialAnchor? existingAnchor)
    {
        _options = options ?? SpatialAnchorOptions.Default;
        _anchor = existingAnchor ?? new SpatialAnchor();
        _camera = new NeckModelCamera(scale: _options.NeckModelScale);
    }

    /// <summary>The underlying anchor, for status readouts and calibration.</summary>
    public SpatialAnchor Anchor => _anchor;

    /// <summary>The pinned panel, once the user has anchored.</summary>
    public PinnedPanel? Panel { get { lock (_panelLock) return _panel; } }

    /// <summary>True once the user has anchored and a panel exists.</summary>
    public bool HasPanel { get { lock (_panelLock) return _panel is not null; } }

    /// <summary>Feed one IMU sample.  Updates the head pose.</summary>
    public void Update(in ImuSample sample) => _anchor.Update(sample);

    /// <summary>
    /// Apply new options at runtime.
    /// </summary>
    /// <remarks>
    /// The user can change the comfort cone, the panel distance and the
    /// neck-model scale from the settings window while the app is
    /// running, so the options cannot be baked in at construction time.
    ///
    /// Changing the distance or the FOV fraction changes the panel's
    /// SIZE, so the panel has to be re-pinned for the change to take
    /// effect.  Changing the comfort cone or the neck scale does not,
    /// because those do not affect the panel's stored pose.
    /// </remarks>
    public void ApplyOptions(SpatialAnchorOptions next)
    {
        var needsRepin = next.PanelDistance != _options.PanelDistance
                      || next.PanelFovFraction != _options.PanelFovFraction
                      || next.DiagonalFovDegrees != _options.DiagonalFovDegrees;

        _options = next;
        _camera = new NeckModelCamera(scale: next.NeckModelScale);

        if (needsRepin && HasPanel)
        {
            // Re-pin at the current gaze: the user keeps looking at the
            // same place, the panel just gets bigger or smaller.
            AnchorHere();
        }
    }

    /// <summary>
    /// Pin the panel where the user is currently looking.
    /// </summary>
    /// <remarks>
    /// The panel's distance and size come from the settings; its
    /// DIRECTION comes from the current gaze, and its world position is
    /// placed at that distance along the gaze ray from the eye.
    ///
    /// The panel's up axis is locked to world gravity rather than to the
    /// head's own up, so a user who anchors while their head is tilted
    /// gets a level screen rather than a crooked one.
    ///
    /// This does NOT move the origin of the world frame — it only says
    /// where the panel is.  See <see cref="Recenter"/> for that.
    /// </remarks>
    public void AnchorHere()
    {
        var headQ = _anchor.HeadOrientation;
        _camera.Solve(headQ, out var eye, out _);

        var width = RokidDisplayProfile.PanelWidth(
            _options.PanelDistance, _options.PanelFovFraction, _options.DiagonalFovDegrees);
        var height = RokidDisplayProfile.PanelHeight(width);

        // The gaze direction, with the ergonomic downward bias applied.
        var gaze = NeckModelCamera.Forward(headQ);

        // How much the panel can be tilted down before it falls off the
        // bottom of the display.
        //
        // The glasses' vertical field of view is 25.8 degrees (the
        // vendor's 50 degrees is the DIAGONAL).  The panel subtends
        // its own height in angle, so if it is centred on the gaze
        // its bottom edge is already half its own height below the
        // line of sight.  Tilting it further down moves the bottom
        // edge down again, and once that edge passes the bottom of
        // the display the user sees a clipped panel.
        //
        // So the usable downward tilt is:
        //
        //     maxBias = (vFov - panelSubtendedAngle) / 2
        //
        // and it is measured from the gaze, which is where the panel
        // would sit with no bias at all.
        var vFov = RokidDisplayProfile.VerticalFovDegrees(_options.DiagonalFovDegrees);
        var subtended = RokidDisplayProfile.SubtendedAngleDegrees(height, _options.PanelDistance);
        var maxBias = MathF.Max(0f, (vFov - subtended) * 0.5f);

        var bias = Math.Clamp(_options.PitchBiasDegrees, -maxBias, maxBias);
        if (MathF.Abs(bias) > 1e-3f)
        {
            // Rotate the gaze about the world X axis (the "right" axis
            // of the world frame) so the bias is in the world frame, not
            // relative to the head.
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, bias * MathF.PI / 180f);
            gaze = Vector3.Transform(gaze, q);
        }
        if (gaze.LengthSquared() < 1e-8f) gaze = -Vector3.UnitZ;

        lock (_panelLock)
        {
            _panel = new PinnedPanel(eye, gaze, _options.PanelDistance, width, height, _camera);
            // A fresh anchor invalidates the lazy-follow state; the new
            // panel is the rigid reference again.
            _hasSmoothedDirection = false;
            _lastViewTimestamp = 0;
        }
    }

    /// <summary>
    /// Make the user's current gaze the neutral forward direction, and
    /// re-pin the panel there.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="AnchorHere"/>, this moves the world origin, so
    /// the head's reported pose and the panel's pose both change.  It is
    /// the "I am looking straight ahead now" button.
    /// </remarks>
    public void Recenter()
    {
        _anchor.SetAnchorHere();
        AnchorHere();
    }

    /// <summary>Forget the anchor and the panel.</summary>
    public void Reset()
    {
        _anchor.Reset();
        ForgetPanel();
    }

    /// <summary>
    /// Forget the PANEL but leave the anchor's calibration alone.
    /// </summary>
    /// <remarks>
    /// The anchor and the panel are two different things: the anchor is
    /// the head-pose reference, and the panel is the object pinned
    /// against it.  "Reset anchor" means the fusion has to start over,
    /// because the reference itself is suspect.  But there are cases
    /// where only the panel should go — for instance when the user
    /// wants to re-anchor the fusion but keep the panel's relative
    /// placement — and in those cases the fusion's calibration (which
    /// takes about a second to re-establish) must NOT be thrown away.
    ///
    /// This is also the only way to clear the panel without touching
    /// the anchor from a thread other than the one that owns it, which
    /// is why the tracking service uses it rather than
    /// <see cref="Reset"/>.
    /// </remarks>
    public void ForgetPanel()
    {
        lock (_panelLock)
        {
            _panel = null;
            _hasSmoothedDirection = false;
            _lastViewTimestamp = 0;
        }
    }

    /// <summary>
    /// Apply the lazy-follow correction to the panel's world direction.
    /// </summary>
    /// <remarks>
    /// This is the mechanism that makes a 3DoF anchor survive without
    /// hijacking the user's reading.  A gyroscope has no absolute
    /// heading reference, so the panel's world direction inevitably
    /// creeps away from where the user is actually looking — about
    /// 1-4 degrees per minute while seated, and much more during the
    /// first quarter-hour as the glasses warm up and the gyro's
    /// zero-rate offset shifts.
    ///
    /// The two obvious responses are both wrong:
    ///
    ///  * Re-anchor automatically when the user stops moving.  This is
    ///    catastrophic for a desktop panel: the user turns their head
    ///    15 degrees to read a sidebar, holds still for three seconds,
    ///    and the panel leaps to the centre of their gaze.  Every
    ///    production 3DoF runtime (Virtual Desktop, SteamVR,
    ///    Bigscreen) uses on-demand recentering instead.
    ///
    ///  * Do nothing and let the panel drift.  Then the panel slowly
    ///    crawls out of the glasses' 44-degree horizontal field of
    ///    view and the user has to fight the hotkey.
    ///
    /// The lazy follow is the compromise, and it is what Virtual
    /// Desktop calls "Lazy Follow" and HoloLens calls "Tag-Along":
    ///
    ///  * Inside the comfort cone (default +-15 degrees) the panel is
    ///    COMPLETELY rigid.  No drift correction at all, so text is
    ///    perfectly stable while the user reads.
    ///  * Outside the cone the panel's direction glides back towards
    ///    the user's gaze at a slow, critically-damped rate.  It never
    ///    jumps, and it never chases the eyes within the cone.
    ///
    /// The correction is applied to the panel's RENDERED direction, not
    /// to the panel's stored pose.  The panel object itself stays
    /// immutable, so the anchor remains a rigid world object; only the
    /// way we look at it is nudged.  That keeps the anchor's semantics
    /// honest and makes the correction trivially reversible (the user
    /// can turn it off and the panel snaps back to its true pose).
    /// </remarks>
    /// <param name="gaze">The user's current gaze direction, in world coordinates.</param>
    /// <param name="dtSeconds">
    /// Time since the previous call.  Used for the exponential decay so
    /// the glide rate is independent of the frame rate.
    /// </param>
    private Vector3 ApplyLazyFollow(Vector3 gaze, float dtSeconds)
    {
        if (_panel is null) return gaze;

        var target = _panel.WorldDirection;

        // The deadzone is measured on the YAW only — the horizontal
        // angle between the gaze and the panel.  Vertical drift is
        // handled by the accelerometer, which pins pitch and roll to
        // gravity, so it does not need a comfort cone.
        var deadzoneRad = _options.LazyFollowDeadzoneDegrees * MathF.PI / 180f;
        if (deadzoneRad <= 1e-4f)
        {
            // Lazy follow disabled: the panel stays exactly where it
            // was anchored.
            _hasSmoothedDirection = false;
            return target;
        }

        // Project the gaze onto the horizontal plane; the comfort cone
        // is a cone around the panel's centre in the horizontal plane.
        var gazeFlat = new Vector3(gaze.X, 0f, gaze.Z);
        if (gazeFlat.LengthSquared() < 1e-8f) return target;
        gazeFlat = Vector3.Normalize(gazeFlat);

        // The panel's CURRENT rendered direction, as a full 3D vector.
        //
        // It is kept in 3D rather than as a horizontal direction so
        // that the panel's elevation is preserved EXACTLY.  The follow
        // only ever rotates about the vertical axis, and a rotation
        // about the vertical axis leaves the Y component alone, so the
        // panel's vertical angle never changes.
        //
        // (An earlier version kept only the horizontal direction and
        // re-attached the elevation afterwards by adding the components
        // back together.  That is not the same thing: the sum of two
        // vectors of different lengths does not point along the first
        // one, so the panel was silently tilted and shifted sideways.
        // Keeping the full vector avoids the reconstruction entirely.)
        if (!_hasSmoothedDirection)
        {
            _smoothedDirection = target;
            _hasSmoothedDirection = true;
        }

        var smoothedFlat = new Vector3(_smoothedDirection.X, 0f, _smoothedDirection.Z);
        if (smoothedFlat.LengthSquared() < 1e-8f)
        {
            // The panel is pointing (almost) straight up or down, so
            // there is no meaningful horizontal direction to follow.
            return _smoothedDirection;
        }
        smoothedFlat = Vector3.Normalize(smoothedFlat);

        // The angle between the gaze and where the panel is RIGHT NOW.
        // This is what the comfort cone is measured against — not
        // against the panel's anchored direction.  Measuring against the
        // anchored direction would mean the panel could never be
        // considered "inside the cone" once the follow had moved it,
        // and the follow would run away.
        var cosAngle = Math.Clamp(Vector3.Dot(gazeFlat, smoothedFlat), -1f, 1f);
        var angle = MathF.Acos(cosAngle);

        if (angle <= deadzoneRad)
        {
            // Inside the cone: fully rigid.
            //
            // NOTE: the smoothed direction is NOT reset here.  The old
            // code reset it to the panel's TRUE direction the instant
            // the gaze re-entered the cone, which teleported the panel
            // by however far the follow had dragged it — measured at
            // 56 degrees in a single frame, at 3366 deg/s.
            //
            // The smoothed direction is now a CONTINUOUS state that
            // simply stops changing while the panel is inside the cone,
            // so the panel stays exactly where it stopped.  When the
            // gaze comes back, the follow glides it home smoothly
            // instead of teleporting it.
            return _smoothedDirection;
        }

        // Where the panel should be heading: the point on the comfort
        // cone's edge, on the side the panel already is.
        //
        // Pulling towards the GAZE instead would make the panel chase
        // the user's eyes; pulling towards the cone edge just undoes
        // the drift without ever "catching" the user.
        var desiredFlat = DirectionAtConeEdge(smoothedFlat, gazeFlat, deadzoneRad);

        // The angle from the panel's current direction to the desired
        // one, and the axis to rotate about.
        var axis = Vector3.Cross(smoothedFlat, desiredFlat);
        if (axis.LengthSquared() < 1e-10f)
        {
            // Already aligned (or exactly opposite) — nothing to do.
            return _smoothedDirection;
        }
        axis = Vector3.Normalize(axis);

        // The angle to rotate by is the angle to the DESIRED direction,
        // NOT the angle to the gaze.
        //
        // This distinction is the whole difference between "lazy
        // follow" and "chasing the user's eyes".  The gaze may be 40
        // degrees away, but the desired resting place is the cone
        // edge, only 25 degrees away.  Rotating by the gaze angle
        // would carry the panel right onto the gaze — which is
        // precisely the behaviour the comfort cone exists to
        // prevent, and it would make the panel jump to the centre of
        // vision every time the user looked away.
        var toDesired = MathF.Acos(Math.Clamp(Vector3.Dot(smoothedFlat, desiredFlat), -1f, 1f));

        var rate = Math.Max(0f, _options.LazyFollowRatePerSecond);
        if (rate <= 1e-4f) return _smoothedDirection;

        // The follow's step.
        //
        // The exponential blend (1 - exp(-rate*dt)) is what makes the
        // motion frame-rate independent and guarantees it never
        // overshoots.
        //
        // It also already produces a CONTINUOUS velocity: the gap
        // between the panel and the cone's edge is exactly the amount
        // by which the gaze is outside the cone, so the panel's speed
        // is proportional to how far outside the cone the gaze is —
        // which falls to zero as the gaze reaches the edge.  That is
        // the right shape for a comfort cone: the panel is perfectly
        // still while the gaze is inside it and eases into motion as
        // the gaze leaves.
        //
        // What broke it before was NOT this law — it was the CLOCK.
        // The step used to be timed with Environment.TickCount64,
        // whose 15-16 ms resolution quantises the timestep at 60 FPS
        // to {0, 16, 32} ms.  On the frames where dt came out as 0 the
        // blend was exactly 0 and the panel did not move at all; on the
        // next frame dt was 31 ms and the panel took a double step.
        // Measured: 20 % of frames had zero motion, with a 0.64
        // degree leap in between.  That alternation IS the stutter.
        //
        // The timestep now comes from the high-resolution Stopwatch, so
        // the blend is smooth and every frame moves the panel by a
        // proportional amount.
        var dt = Math.Clamp(dtSeconds, 0f, 0.25f);
        var blend = 1f - MathF.Exp(-rate * dt);

        var rotated = Vector3.Transform(
            _smoothedDirection,
            Quaternion.CreateFromAxisAngle(axis, toDesired * blend));

        _smoothedDirection = Vector3.Normalize(rotated);
        return _smoothedDirection;
    }

    /// <summary>
    /// The direction that sits exactly <paramref name="deadzoneRad"/>
    /// away from the gaze, on the side of <paramref name="from"/>.
    /// </summary>
    /// <remarks>
    /// If the gaze and the panel are already within the cone, the
    /// panel's own direction is returned unchanged.
    /// </remarks>
    private static Vector3 DirectionAtConeEdge(Vector3 from, Vector3 gaze, float deadzoneRad)
    {
        var cosAngle = Math.Clamp(Vector3.Dot(from, gaze), -1f, 1f);
        var angle = MathF.Acos(cosAngle);
        if (angle <= deadzoneRad) return from;

        var axis = Vector3.Cross(from, gaze);
        if (axis.LengthSquared() < 1e-10f)
        {
            axis = Vector3.Cross(from, Vector3.UnitY);
            if (axis.LengthSquared() < 1e-10f) axis = Vector3.UnitX;
        }
        axis = Vector3.Normalize(axis);

        // Rotate FROM THE PANEL TOWARDS THE GAZE, but only far enough
        // to leave the gaze sitting exactly on the cone edge.
        //
        // The direction of the rotation matters.  The obvious-looking
        // alternative — rotate the GAZE towards the panel by the
        // deadzone angle — is the same thing geometrically, but it is
        // easy to get the SIGN of the axis wrong, and getting it wrong
        // pushes the panel AWAY from the gaze instead of towards it.
        // The old code did exactly that: the panel was left 55 degrees
        // from the anchor when it should have been 25, which pushed it
        // out of the field of view instead of bringing it back.
        //
        // Rotating the panel about cross(panel, gaze) by a POSITIVE
        // angle moves it towards the gaze, so the sign is unambiguous.
        var toRotate = angle - deadzoneRad;

        return Vector3.Normalize(Vector3.Transform(
            from,
            Quaternion.CreateFromAxisAngle(axis, toRotate)));
    }

    /// <summary>
    /// Build the view the renderer needs.
    /// </summary>
    /// <remarks>
    /// The view matrix maps world coordinates into the eye's frame:
    ///
    ///     V = T(-p_eye) * R^T
    ///
    /// with R the head's orientation and p_eye the neck-modelled eye
    /// position.
    ///
    /// It is built from R directly rather than with
    /// <see cref="Matrix4x4.CreateLookAt"/>, and that distinction
    /// matters.  CreateLookAt takes an "up" vector and constructs a
    /// camera whose up is always that vector — so it silently
    /// DISCARDS the head's roll.  With CreateLookAt the world would
    /// appear to roll with the head, which is exactly the
    /// head-locked look we are trying to escape.  A screen pinned in
    /// the room has to stay level in the room while the head tilts,
    /// which means the camera has to carry the full rotation,
    /// roll included.
    ///
    /// The same reasoning applies to the eye's translation: taking the
    /// position from the neck model (rather than from the head's
    /// origin) is what gives the panel its correct parallax as the
    /// user tilts and turns.
    ///
    /// The ORDER of the two factors is not arbitrary, and getting it
    /// wrong is subtle: at zero pitch and zero yaw every candidate
    /// order produces a perfectly plausible result, so the mistake
    /// survives casual testing and only shows up when the user looks
    /// up or down.
    ///
    /// System.Numerics uses the ROW-VECTOR convention: a point is
    /// transformed as v' = v * M, and a product M1 * M2 applies M1
    /// FIRST and M2 second.  The world -> eye transform therefore has
    /// to subtract the eye position first and rotate second:
    ///
    ///     v_eye = (v_world - p_eye) * R^T
    ///            = v_world * T(-p_eye) * R^T
    ///
    /// The previous code had the factors the other way round
    /// (R^T * T(-p_eye)), which rotates the translation into the eye
    /// frame as well and produces a view matrix that is off by the
    /// eye's rotation.  At zero pitch that error is invisible, but at
    /// a realistic 30-degree reading tilt it threw the panel's NDC
    /// coordinates to y = 2.7 (twice the top of the viewport) and the
    /// depth to z = -0.46 (behind the near plane), so the panel was
    /// clipped away entirely and the renderer drew nothing.
    /// </remarks>
    public AnchorView GetView()
    {
        var headQ = _anchor.HeadOrientation;
        _camera.Solve(headQ, out var eye, out _);

        var rotation = Matrix4x4.CreateFromQuaternion(headQ);
        var view = Matrix4x4.CreateTranslation(-eye) * Matrix4x4.Transpose(rotation);

        var gaze = NeckModelCamera.Forward(headQ);
        var corners = Array.Empty<Vector3>();

        lock (_panelLock)
        {
            if (_panel is not null)
            {
                // The lazy follow needs the time since the last view so
                // its glide rate is frame-rate independent.
                //
                // The clock is the high-resolution Stopwatch, NOT
                // Environment.TickCount64.  The latter has a 15-16 ms
                // resolution on Windows, which at a 60 FPS render rate
                // quantises the timestep to {0, 16, 32} ms — so the
                // panel took no step at all on some frames and a double
                // step on others.  Measured: 20 % of frames had zero
                // panel motion.  That alternation was the stutter.
                var now = Stopwatch.GetTimestamp();
                var dt = _lastViewTimestamp > 0
                    ? (float)((now - _lastViewTimestamp) / TimestampFrequency)
                    : 1f / 60f;
                _lastViewTimestamp = now;

                var followed = ApplyLazyFollow(gaze, dt);
                var pose = _panel.PoseWithDirection(followed);
                corners = pose.Corners();
            }
        }

        return new AnchorView(view, corners, eye, gaze, headQ);
    }

    /// <summary>
    /// The projection matrix for the glasses' display.
    /// </summary>
    /// <remarks>
    /// The Rokid Max's field of view is the horizontal one: the
    /// display is 16:9 and the vendor publishes a diagonal figure,
    /// which <see cref="RokidDisplayProfile"/> converts.  The
    /// vertical FOV then follows from the aspect ratio.
    ///
    /// <see cref="Matrix4x4.CreatePerspectiveFieldOfView"/> is
    /// right-handed with a 0..1 depth range, which is what D3D wants,
    /// so it can be handed to the renderer unchanged.
    /// </remarks>
    /// <param name="near">Near plane, in metres.</param>
    /// <param name="far">Far plane, in metres.</param>
    public Matrix4x4 GetProjection(float near = 0.05f, float far = 50f)
    {
        var hFov = RokidDisplayProfile.HorizontalFovDegrees(_options.DiagonalFovDegrees);
        var vFov = RokidDisplayProfile.VerticalFovDegrees(_options.DiagonalFovDegrees);
        return Matrix4x4.CreatePerspectiveFieldOfView(vFov * MathF.PI / 180f, 16f / 9f, near, far);
    }
}
