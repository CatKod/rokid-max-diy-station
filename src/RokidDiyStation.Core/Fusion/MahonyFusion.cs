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
/// <summary>
/// Tunable parameters for the Mahony filter.  Defaults are conservative
/// values that work well on the Rokid Max at 437 Hz.
///
/// NOTE: this type is deliberately NOT a positional record struct.  A
/// positional <c>readonly record struct</c> zero-initialises on
/// <c>default</c> / <c>new()</c>, and those paths BYPASS the primary
/// constructor's parameter defaults — which silently produced a filter
/// with no accelerometer correction at all.  See <see cref="Validate"/>.
///
/// The default values and the overall gating architecture follow
/// Sebastian Madgwick's x-io <c>Fusion</c> library
/// (<c>Fusion/FusionAhrs.c</c>), which is the reference implementation
/// for this class of filter and the one every production 3DoF runtime
/// is descended from.  See .research/g1/findings.md for the full
/// derivation, the primary-source citations and the reasoning behind
/// each parameter.
/// </summary>
public readonly record struct MahonyTuning
{
    /// <summary>
    /// Proportional gain for the accelerometer's gravity correction, in
    /// inverse seconds.
    ///
    /// The error <c>e = â × v̂</c> is dimensionless (the sine of the
    /// tilt error), so the correction <c>Kp · e</c> has units of rad/s
    /// and Kp has units of s⁻¹.  The tilt error then decays as
    /// <c>θ(t) = θ₀·e^(−Kp·t)</c>, so the time constant is
    /// <c>τ = 1/Kp</c> and the equivalent corner frequency is
    /// <c>fc = Kp / 2π</c>.
    ///
    /// At the default of 0.40 s⁻¹ that gives τ = 2.5 s and
    /// fc = 0.064 Hz: motion transients above ~0.1 Hz are integrated by
    /// the gyro alone, and gravity anchors only the slow drift below
    /// ~0.06 Hz.  That is the right shape for a virtual screen — fast
    /// head turns must feel instantaneous, and the horizon must not
    /// wobble.
    /// </summary>
    public float KpAccel { get; init; } = 0.40f;

    /// <summary>
    /// Integral gain for the accelerometer's gravity correction, in
    /// inverse seconds squared.
    ///
    /// Deliberately ZERO.  An accelerometer-based integrator cannot tell
    /// a real sensor bias from a sustained linear acceleration, so it
    /// winds up during walking or a car ride and then drives the
    /// virtual horizon off level for tens of seconds afterwards.  Gyro
    /// bias is handled separately and correctly, by
    /// <see cref="GyroBiasEstimator"/>, which only accumulates samples
    /// while the device is detected to be stationary.
    /// </summary>
    public float KiAccel { get; init; } = 0.0f;

    /// <summary>Proportional gain for magnetometer correction.</summary>
    public float KpMag { get; init; } = 1.0f;

    /// <summary>Integral gain for magnetometer correction.</summary>
    public float KiMag { get; init; } = 0.0f;

    /// <summary>
    /// Standard gravity, in m/s².  Used only to detect free-fall and
    /// by the stationary detector, NOT as a rejection gate — see
    /// <see cref="RejectionAngleDegrees"/> for why the magnitude gate
    /// was removed.
    /// </summary>
    public float GravityMagnitude { get; init; } = 9.80665f;

    /// <summary>
    /// The angular rejection threshold while the device is STILL, in
    /// degrees.
    ///
    /// This is Madgwick's <c>accelerationRejection</c>, and it is the
    /// only rejection gate the filter uses.  The magnitude gate and the
    /// gyro-rate gate that used to be here were both wrong for this
    /// hardware:
    ///
    ///   * The magnitude gate (|‖a‖ − g| &gt; 0.4 m/s², i.e. 4 % of
    ///     gravity) trips on the centripetal acceleration of an
    ///     ordinary head turn.  The glasses sit ~9 cm in front of the
    ///     cervical pivot, so a 200°/s turn produces
    ///     a_c = ω²·r ≈ 1.1 m/s² — nearly three times the threshold.
    ///     Madgwick's own reference implementation has NO magnitude gate
    ///     at all, precisely because it produces these false rejections.
    ///
    ///   * The gyro-rate gate (|ω| &gt; 0.8 rad/s = 46°/s) is below the
    ///     rate of an ordinary saccade (150–300°/s), so it switched the
    ///     accelerometer off during every normal head turn.
    ///
    /// The threshold is TIGHT while the device is still, because a
    /// still device whose accelerometer disagrees with the estimate is
    /// experiencing linear acceleration — a lean in the chair, a car
    /// taking a gentle curve — and the estimate is the one that is
    /// right.  The user's head may have rolled 20°; the glasses are
    /// still on their face; the world has not tilted.  Following the
    /// accelerometer here would tilt the virtual horizon, which is
    /// exactly the artefact this whole filter exists to prevent.
    /// </summary>
    public float RejectionAngleDegrees { get; init; } = 5.0f;

    /// <summary>
    /// The angular rejection threshold while the device is rotating
    /// FAST, in degrees.
    ///
    /// During a fast head turn the glasses experience a real
    /// centripetal acceleration: the head pivots about the cervical
    /// spine, and the glasses sit ~9 cm in front of that pivot, so a
    /// 200°/s turn generates a_c = ω²·r ≈ 1.1 m/s² (0.11 g) and tilts
    /// the apparent gravity by about 6.4°.  A 250°/s turn reaches
    /// ~9.9°.  The threshold therefore has to be wide enough to admit
    /// that much apparent tilt, or the filter loses its gravity
    /// reference during every ordinary head movement.
    ///
    /// The threshold is interpolated between
    /// <see cref="RejectionAngleDegrees"/> and this value according to
    /// the gyro rate, so it is tight when the head is still and wide
    /// only while the head is genuinely turning.
    /// </summary>
    public float MaxRejectionAngleDegrees { get; init; } = 15.0f;

    /// <summary>
    /// The gyro rate (rad/s) at which the rejection threshold has
    /// reached <see cref="MaxRejectionAngleDegrees"/>.
    ///
    /// 2.0 rad/s is 115°/s — a brisk head turn, well above the rate at
    /// which the centripetal term becomes significant.
    /// </summary>
    public float GyroRateForLooseRejection { get; init; } = 2.0f;

    /// <summary>
    /// How fast the filter is allowed to slew towards the
    /// accelerometer's gravity direction when the two disagree by a
    /// SMALL amount, in degrees per second.
    ///
    /// This is the knob that decides how the filter resolves the
    /// fundamental ambiguity of a 3DoF IMU: a still device whose
    /// accelerometer disagrees with the estimate might be showing
    /// linear acceleration (a lean, a car taking a gentle curve) or
    /// might be showing that the estimate has drifted.
    ///
    /// The product requirement settles it in favour of the horizon: a
    /// virtual screen whose level tilts every time the user shifts in
    /// their chair is far worse than a tilt that drifts by a fraction of
    /// a degree per minute.  So the slew is deliberately slow - fast
    /// enough to cancel the filter's own measured drift of about
    /// 0.3 deg/min within a minute, slow enough that a three-second
    /// lean moves the horizon by less than a degree.
    /// </summary>
    public float GravityRealignRateDegreesPerSecond { get; init; } = 0.2f;

    /// <summary>
    /// The residual (degrees) beyond which the filter's own estimate is
    /// assumed to be STALE rather than merely wrong, and the recovery
    /// latch is allowed to fire.
    ///
    /// A small persistent residual (a few degrees) means the
    /// accelerometer is probably lying — a sustained lean — and the
    /// filter should keep ignoring it, because following it would tilt
    /// the virtual horizon.  A large one means the filter's estimate is
    /// probably out of date (the glasses were picked up and put down in
    /// a new posture), and the measurement should be allowed to pull the
    /// estimate back.
    /// </summary>
    public float StaleEstimateAngleDegrees { get; init; } = 25.0f;

    /// <summary>
    /// How far the accelerometer's magnitude may be from gravity, in
    /// m/s^2, for the reading to be considered consistent with the
    /// device being at rest.
    ///
    /// This is NOT a rejection gate on the correction — the old code
    /// used it that way and it broke the resting-tilt case, because
    /// the glasses sit back on the nose at 20-30 degrees and the
    /// wearer breathes, jostles and shifts.  Instead it selects WHICH
    /// TIER applies:
    ///
    ///   * Inside the tolerance, the reading could be gravity, so the
    ///     filter may use it (at full weight in tier A, at a crawl in
    ///     tier B, at full weight in tier C).
    ///   * Outside it, there is provably real linear acceleration, so
    ///     the accelerometer's direction is NOT the direction of
    ///     gravity and the filter falls back to the gyro alone.
    ///
    /// The value is deliberately loose.  A slow head turn produces
    /// about 1.1 m/s^2 of centripetal acceleration at the lens, and a
    /// car taking a gentle curve produces a steady fraction of a g,
    /// so anything tighter would reject readings that are perfectly
    /// good.
    /// </summary>
    public float MagnitudeTolerance { get; init; } = 0.5f;

    /// <summary>
    /// How much tighter the agreement threshold becomes once a
    /// disagreement has been seen, in degrees.
    ///
    /// This is a Schmitt trigger on the agreement test.  Without it
    /// the filter can creep up to the gate threshold from outside,
    /// cross it by a hair, and then commit fully to a reading that
    /// was never trustworthy — the horizon tilts all the way over to a
    /// false gravity direction while the filter nominally believes it
    /// is in agreement.
    ///
    /// The hysteresis is small, so it does not delay a genuine
    /// re-lock; it only prevents a marginal, marginal reading from
    /// taking over.
    /// </summary>
    public float AgreementHysteresisDegrees { get; init; } = 2.0f;

    /// <summary>
    /// How long the accelerometer may stay distrusted before the filter
    /// forces a full-weight re-lock, in seconds.
    ///
    /// Madgwick's <c>rejectionTimeout</c>.  On expiry the filter
    /// restores FULL proportional weight (not a token fraction) and
    /// holds it until the residual agrees again.  A partial weight is
    /// what the old code used, and it was fatal: because the angular
    /// gate stays closed while the residual is large, a partial weight
    /// can never shrink the residual, so the filter settled into a
    /// permanent low-trust equilibrium and never re-locked.  That is
    /// exactly the failure the timeout exists to prevent.
    /// </summary>
    public float RejectionTimeoutSeconds { get; init; } = 4.0f;

    /// <summary>
    /// The maximum angular rate, in rad/s, at which the gravity
    /// correction may be applied during NORMAL operation — a slew-rate
    /// clamp on the feedback.
    ///
    /// This is the last line of defence against a lurching horizon.  Even
    /// if a bad accelerometer sample slips past the gate, it can move
    /// the horizon at most a few degrees per second, so a half-second
    /// glance can never tilt the virtual screen by more than a degree or
    /// two.  Monado's 3DoF filter (<c>m_imu_3dof.c</c>) uses the same
    /// idea.
    ///
    /// The value is chosen to sit just above the filter's own natural
    /// bound — with the default 15° gate and Kp = 0.4, the natural rate
    /// is Kp·sin(15°) ≈ 0.10 rad/s — so the clamp is a safety net for
    /// pathological residuals rather than something that shapes normal
    /// behaviour.
    /// </summary>
    public float MaxCorrectionRateRadPerSecond { get; init; } = 0.12f;

    /// <summary>
    /// The maximum angular rate, in rad/s, at which the gravity
    /// correction may be applied while RECOVERING from a stale
    /// estimate.
    ///
    /// This is looser than <see cref="MaxCorrectionRateRadPerSecond"/>
    /// on purpose.  During recovery the filter has already decided that
    /// its own estimate is out of date, so the measurement is the
    /// trustworthy one and the correction needs to be able to pull the
    /// estimate back briskly.  It is still bounded, so the re-lock is a
    /// smooth glide rather than a jump.
    /// </summary>
    public float MaxRecoveryCorrectionRateRadPerSecond { get; init; } = 0.25f;

    /// <summary>
    /// The gyro rate below which the device counts as stationary, in
    /// rad/s.  Below this, the filter trusts the accelerometer more
    /// readily, because a still device's accelerometer really is
    /// measuring gravity and nothing else.
    /// </summary>
    public float StationaryGyroThreshold { get; init; } = 0.015f;

    /// <summary>
    /// How long the device must be stationary before the filter resets
    /// its rejection state and re-locks immediately, in seconds.
    ///
    /// This is the "rapid re-lock" path.  When the user sits down after
    /// walking, the glasses become still and the accelerometer becomes
    /// trustworthy again immediately — there is no reason to wait out
    /// the full <see cref="RejectionTimeoutSeconds"/> in that case.
    /// </summary>
    public float StationaryTimeSeconds { get; init; } = 0.4f;

    /// <summary>
    /// The tuning used when none is supplied.
    /// </summary>
    public static readonly MahonyTuning Default = new();

    /// <summary>
    /// Create a tuning with the documented default values.
    /// </summary>
    /// <remarks>
    /// This constructor is REQUIRED, and it is the whole point of the
    /// type's shape.  A struct with field initializers must declare an
    /// explicitly declared constructor, and only then does <c>new
    /// MahonyTuning()</c> run the initializers.  Without it, <c>new
    /// MahonyTuning()</c> — and therefore <c>MahonyTuning.Default</c> —
    /// would zero every field, which is exactly the bug this type's
    /// shape exists to prevent.
    ///
    /// Note that <c>default(MahonyTuning)</c> STILL zero-initialises; no
    /// constructor can change that.  That path is caught by
    /// <see cref="Validate"/>.
    /// </remarks>
    public MahonyTuning()
    {
    }

    /// <summary>
    /// Throw if the tuning is not usable.
    /// </summary>
    /// <remarks>
    /// This guard exists because of a bug that shipped:  <see cref="MahonyTuning"/>
    /// used to be a positional <c>readonly record struct</c> with a primary
    /// constructor, and both <c>Default = new()</c> and the
    /// <c>MahonyTuning tuning = default</c> parameter default resolved to the
    /// struct's ZERO-initialising path, not to the primary constructor's
    /// parameter defaults.  Every filter therefore ran with
    /// <c>KpAccel = 0</c> — that is, with the accelerometer gravity
    /// correction entirely switched off — and with
    /// <c>GravityMagnitude = 0</c>, which made the rejection gates divide by
    /// zero and pin the trust factor at 0.1 forever.
    ///
    /// The filter then degenerated into open-loop gyro integration, which is
    /// precisely the configuration that produced the measured ~0.3°/min of
    /// roll drift on real Rokid Max hardware, and it is why an earlier
    /// attempt to fix that drift by zeroing the accelerometer correction
    /// "worked" — the correction was already dead.
    ///
    /// A zero-valued tuning is never a legitimate configuration, so this
    /// throws rather than silently producing a broken filter.
    /// </remarks>
    /// <exception cref="ArgumentException">The tuning is not usable.</exception>
    public void Validate()
    {
        if (KpAccel <= 0f)
        {
            throw new ArgumentException(
                $"MahonyTuning.KpAccel must be positive (got {KpAccel}). " +
                "A zero gain disables the accelerometer gravity correction and turns " +
                "the filter into open-loop gyro integration, which drifts.",
                nameof(KpAccel));
        }
        if (GravityMagnitude <= 0f)
        {
            throw new ArgumentException(
                $"MahonyTuning.GravityMagnitude must be positive (got {GravityMagnitude}).",
                nameof(GravityMagnitude));
        }
        if (MagnitudeTolerance <= 0f)
        {
            throw new ArgumentException(
                $"MahonyTuning.MagnitudeTolerance must be positive (got {MagnitudeTolerance}).",
                nameof(MagnitudeTolerance));
        }
        if (AgreementHysteresisDegrees < 0f)
        {
            throw new ArgumentException(
                $"MahonyTuning.AgreementHysteresisDegrees must not be negative " +
                $"(got {AgreementHysteresisDegrees}).",
                nameof(AgreementHysteresisDegrees));
        }
        if (RejectionAngleDegrees <= 0f || RejectionAngleDegrees >= 90f)
        {
            throw new ArgumentException(
                $"MahonyTuning.RejectionAngleDegrees must be in (0, 90) (got {RejectionAngleDegrees}).",
                nameof(RejectionAngleDegrees));
        }
        if (MaxRejectionAngleDegrees < RejectionAngleDegrees || MaxRejectionAngleDegrees >= 90f)
        {
            throw new ArgumentException(
                $"MahonyTuning.MaxRejectionAngleDegrees must be in [RejectionAngleDegrees, 90) " +
                $"(got {MaxRejectionAngleDegrees}, rejection angle {RejectionAngleDegrees}).",
                nameof(MaxRejectionAngleDegrees));
        }
        if (StaleEstimateAngleDegrees <= MaxRejectionAngleDegrees || StaleEstimateAngleDegrees >= 180f)
        {
            throw new ArgumentException(
                $"MahonyTuning.StaleEstimateAngleDegrees must be in (MaxRejectionAngleDegrees, 180) " +
                $"(got {StaleEstimateAngleDegrees}, max rejection {MaxRejectionAngleDegrees}).",
                nameof(StaleEstimateAngleDegrees));
        }
        if (GravityRealignRateDegreesPerSecond <= 0f)
        {
            throw new ArgumentException(
                $"MahonyTuning.GravityRealignRateDegreesPerSecond must be positive " +
                $"(got {GravityRealignRateDegreesPerSecond}).",
                nameof(GravityRealignRateDegreesPerSecond));
        }
        if (GyroRateForLooseRejection <= 0f)
        {
            throw new ArgumentException(
                $"MahonyTuning.GyroRateForLooseRejection must be positive (got {GyroRateForLooseRejection}).",
                nameof(GyroRateForLooseRejection));
        }
        if (RejectionTimeoutSeconds <= 0f)
        {
            throw new ArgumentException(
                $"MahonyTuning.RejectionTimeoutSeconds must be positive (got {RejectionTimeoutSeconds}).",
                nameof(RejectionTimeoutSeconds));
        }
        if (MaxCorrectionRateRadPerSecond <= 0f)
        {
            throw new ArgumentException(
                $"MahonyTuning.MaxCorrectionRateRadPerSecond must be positive (got {MaxCorrectionRateRadPerSecond}).",
                nameof(MaxCorrectionRateRadPerSecond));
        }
        if (StationaryGyroThreshold <= 0f)
        {
            throw new ArgumentException(
                $"MahonyTuning.StationaryGyroThreshold must be positive (got {StationaryGyroThreshold}).",
                nameof(StationaryGyroThreshold));
        }
    }
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
    // Skip mag correction for the first N samples so a noisy initial
    // reading can't yank the heading.  ~0.5s at 437 Hz.
    private const int MagWarmupSamples = 200;

    // The recovery state.
    //
    //   _disagreementSeconds  how long the accelerometer and the
    //                         filter's estimate have disagreed.  This
    //                         is what separates a transient (a knock, a
    //                         head snap) from a sustained problem (the
    //                         glasses were put down in a new posture).
    //   _recoveryLatched      set when a large, sustained disagreement
    //                         is judged to mean the estimate is stale.
    //                         It holds the full correction until the
    //                         residual comes back inside the gate.
    private float _disagreementSeconds;
    private bool _inDisagreement;
    private bool _recoveryLatched;

    // The stationary detector's state.
    private float _stationaryTimer;
    private bool _stationary;

    /// <summary>
    /// True while the accelerometer correction is being suppressed by the
    /// rejection gate.  Exposed for diagnostics.
    /// </summary>
    public bool IsAccelRejected => LastAccelTrust <= 0f;

    /// <summary>
    /// True while the recovery latch is holding the accelerometer at full
    /// weight after a sustained rejection.  Exposed for diagnostics.
    /// </summary>
    public bool IsRecovering => _recoveryLatched;

    /// <summary>
    /// The trust factor the filter applied to the accelerometer on the
    /// last sample, after all the rejection gates and the recovery
    /// timeout.  Exposed for diagnostics.
    /// </summary>
    public float LastAccelTrust { get; private set; } = 1f;

    /// <summary>
    /// The angle between the accelerometer's measured direction and the
    /// filter's estimate of gravity, in degrees, on the last sample.
    /// </summary>
    /// <remarks>
    /// This is the raw quantity the rejection gate works on.  It is
    /// the single most useful number for diagnosing a bad anchor: if
    /// it is small the filter is in agreement with the sensor, and if
    /// it is large the sensor is being distrusted.
    /// </remarks>
    public float LastResidualDegrees { get; private set; }

    /// <summary>
    /// How long the accelerometer and the estimate have been
    /// disagreeing, in seconds.
    /// </summary>
    public float DisagreementSeconds => _disagreementSeconds;

    /// <summary>
    /// True when the filter is in tier B: the two disagree, the device
    /// is at rest, and the filter is slewing the horizon back very
    /// slowly rather than snapping to the sensor.
    /// </summary>
    public bool IsSlowRealigning => LastAccelTrust > 0f && LastAccelTrust < 1f;

    /// <param name="tuning">
    /// Filter tuning.  Defaults to <see cref="MahonyTuning.Default"/>.
    /// </param>
    /// <param name="useMagnetometer">
    /// Whether to apply the magnetometer's heading correction.
    ///
    /// OFF by default, and it should stay off for this product.  The
    /// Rokid Max's magnetometer reads about 108 µT indoors — well
    /// outside Earth's 25–65 µT field — because the metal chassis
    /// dominates the reading.  Indoors the heading correction is then
    /// worse than no correction at all, and it fights the gyro in a way
    /// the user sees as the screen swinging around.  Every production
    /// 3DoF runtime (Virtual Desktop, SteamVR, Bigscreen) likewise
    /// resolves heading by hand (one-key recenter), not by compass.
    /// </param>
    public MahonyFusion(MahonyTuning? tuning = null, bool useMagnetometer = false)
    {
        _tuning = tuning ?? MahonyTuning.Default;
        _tuning.Validate();
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
        _disagreementSeconds = 0f;
        _inDisagreement = false;
        _recoveryLatched = false;
        _stationaryTimer = 0f;
        _stationary = false;
        LastAccelTrust = 1f;
    }

    /// <summary>
    /// Feed one IMU sample.  Returns the status of the update.
    /// </summary>
    /// <param name="accelTrust">External override for the accelerometer
    /// correction weight, 0..1.  Pass 1 (the default) to let the filter
    /// decide for itself; the filter scales the correction down only
    /// when the accelerometer is measurably disturbed by linear
    /// acceleration.  Do NOT set this to 0 while the device is merely
    /// stationary: at a constant resting tilt the accelerometer error
    /// term a × v̂ is exactly zero, so the correction contributes
    /// nothing and switching it off turns the filter into open-loop gyro
    /// integration — which is what makes pitch and roll drift away from
    /// gravity.  Measured on real Rokid Max hardware: with the
    /// correction disabled while stationary, roll drifted a linear
    /// ~0.3°/min; with it enabled, roll stays put.</param>
    public FusionResult Update(in ImuSample sample, float accelTrust = 1f)
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

        var gyro = sample.Gyro;
        if (!IsFinite(gyro)) return FusionResult.Rejected;

        // 3. GYRO PREDICTION.
        //
        //    Propagate the orientation forward by the CURRENT gyro sample
        //    BEFORE evaluating the gravity residual.  This matters more
        //    than it looks: the residual has to be compared against the
        //    orientation the filter is about to have, not the one it had
        //    before this sample.  Comparing against the lagged estimate
        //    makes the filter see a residual during every fast head turn
        //    — the filter's own integration lag shows up as an apparent
        //    tilt error, the gate trips, and the filter loses its gravity
        //    reference exactly when it is moving fastest.
        //
        //    The prediction is the plain gyro integration with NO
        //    correction; the correction is applied afterwards.
        var predicted = Integrate(_q, gyro, dt);

        // 4. Error terms.
        //
        //    The quaternion q is DEVICE -> WORLD: the gyro integration
        //    q' = 0.5 * q ⊗ ω_body is the body-frame kinematic
        //    equation for that convention.  The accelerometer reading
        //    is in the device (body) frame.  So the quantity the filter
        //    needs to compare against is the world up vector expressed
        //    in the BODY frame, which is R^T * up — NOT R * up.
        //
        //    Getting this wrong is subtle and expensive: the two
        //    expressions are both unit vectors and both look
        //    reasonable, but only one of them is an attracting
        //    equilibrium.  Measured on the real device, the wrong form
        //    left a steady residual that no gain tuning could remove.
        //
        //    The sign follows from the same algebra.  The correction is
        //    applied as a body-frame rotation rate (q' = 0.5 * q ⊗
        //    (ω + err)), so with q_corr = q ⊗ D we get
        //
        //        R_corr^T up = D^T * R^T up = D^T * estUp
        //
        //    and we want R_corr^T up == aHat, i.e. D must rotate aHat
        //    onto estUp.  The axis of "rotate a onto b" for small
        //    angles is cross(a, b), so D = cross(aHat, estUp).
        //
        //    Hence: err = cross(aHat, estUp).
        var estUp = RotateByQuatInverse(Vector3.UnitY, predicted);
        var accelErr = Vector3.Cross(aHat, estUp);

        // 5. The angular rejection gate.
        //
        //    This is Madgwick's `accelerationRejection`, and it is the
        //    ONLY direction gate the filter uses.  The magnitude gate and
        //    the gyro-rate gate that used to be here were both wrong for
        //    this hardware:
        //
        //      * The magnitude gate (|a| - g| > 0.4 m/s^2, i.e. 4 % of
        //        gravity) trips on the centripetal acceleration of an
        //        ordinary head turn.  The glasses sit ~9 cm in front of
        //        the cervical pivot, so a 200 deg/s turn produces
        //        a_c = omega^2 * r = 1.1 m/s^2, nearly three times the
        //        threshold.  Madgwick's own reference implementation has
        //        NO magnitude gate at all, precisely because it produces
        //        these false rejections.
        //
        //      * The gyro-rate gate (|omega| > 0.8 rad/s = 46 deg/s) is
        //        below the rate of an ordinary saccade (150-300 deg/s),
        //        so it switched the accelerometer off during every
        //        normal head turn.
        //
        //    The angular gate catches what both of those missed - a
        //    linear acceleration whose magnitude stays near 1 g but
        //    whose direction is wrong - and it catches it correctly,
        //    because a direction that is wrong is a direction that
        //    disagrees with gravity.
        //
        //    The threshold is NOT a constant: it is interpolated by the
        //    gyro rate.  A still device gets a tight threshold, so a
        //    lean in the chair cannot tilt the virtual horizon; a device
        //    turning fast gets a wide one, so the real centripetal tilt
        //    of a head turn is not mistaken for corruption.
        var cosAngle = Math.Clamp(Vector3.Dot(aHat, estUp), -1f, 1f);
        var residualDeg = MathF.Acos(cosAngle) * 180f / MathF.PI;
        LastResidualDegrees = residualDeg;

        var gyroRate = gyro.Length();
        var rejectionAngle = _tuning.RejectionAngleDegrees;
        if (_tuning.GyroRateForLooseRejection > 1e-3f && gyroRate > 0f)
        {
            var t = Math.Clamp(gyroRate / _tuning.GyroRateForLooseRejection, 0f, 1f);
            rejectionAngle = _tuning.RejectionAngleDegrees
                           + (_tuning.MaxRejectionAngleDegrees - _tuning.RejectionAngleDegrees) * t;
        }

        // THE MAGNITUDE CHECK.
        //
        // This is NOT the old hard "magnitude gate".  It is not a gate
        // on the correction, and it is deliberately much looser than
        // the old one (0.5 m/s^2 instead of 0.4).  It answers a
        // completely different question: "is the reading consistent
        // with the device being at rest?"
        //
        // A device at rest reads exactly 1 g.  A device being
        // translated reads 1 g PLUS the linear acceleration, and the
        // extra component is what tilts the apparent gravity.  So a
        // magnitude that is well away from 1 g is proof that there IS
        // real linear acceleration, and the accelerometer's DIRECTION
        // is therefore not the direction of gravity.
        //
        // It cannot be used to reject the correction outright - the
        // old code did that, and it broke the resting-tilt case,
        // because the glasses sit back on the nose at 20-30 degrees
        // and the wearer breathes, jostles and shifts.  Instead it
        // only decides WHICH TIER applies.
        var magOk = MathF.Abs(accelNorm - _tuning.GravityMagnitude)
                    <= _tuning.MagnitudeTolerance;

        // THE SCHMITT TRIGGER ON AGREEMENT.
        //
        // The threshold for "the accelerometer agrees with the filter"
        // is tighter once a disagreement has been seen.  Without that
        // hysteresis the filter can creep up to the threshold from
        // outside, cross it by a hair, and then commit fully to a
        // reading that was never trustworthy in the first place - the
        // horizon would tilt all the way over to a false gravity
        // direction while the filter was nominally "in agreement".
        var acceptAngle = rejectionAngle - _tuning.AgreementHysteresisDegrees;
        var withinGate = _inDisagreement
            ? residualDeg <= acceptAngle
            : residualDeg <= rejectionAngle;

        // 6. The stationary detector.
        //
        //    A still device's accelerometer is measuring gravity and
        //    nothing else - UNLESS the device is also being translated,
        //    which is what the magnitude check below screens for.
        UpdateStationaryState(gyro, accelNorm, dt);

        // 7. The trust tier.
        //
        //    There are three tiers, and choosing between them is the
        //    whole game.  The ambiguity they resolve is fundamental: a
        //    still device whose accelerometer disagrees with the filter's
        //    estimate can mean EITHER that the estimate has drifted OR
        //    that the accelerometer is picking up linear acceleration.
        //    Nothing in a 3DoF IMU can tell those two apart directly.
        //
        //    So the filter asks a different question: HOW BIG is the
        //    disagreement, and HOW LONG has it persisted?
        //
        //    Tier A - AGREEMENT.  The residual is inside the gate.  The
        //    accelerometer is telling us the same thing the filter
        //    already believes, so apply the full proportional
        //    correction.  This is ordinary operation, and it is what
        //    pins the virtual horizon to the real world.
        //
        //    Tier B - DISAGREEMENT, SMALL.  The residual is outside the
        //    gate but below the staleness threshold.  This is the
        //    ambiguous case, and the product requirement decides it: a
        //    virtual screen whose horizon tilts every time the user
        //    shifts in their chair is the single most visible failure a
        //    3DoF anchor can produce.  So the filter does NOT follow the
        //    accelerometer here.  It applies a very slow constant-rate
        //    slew instead, which is fast enough to cancel the filter's
        //    own drift (measured at ~0.3 deg/min) within a minute, and
        //    slow enough that a sustained lean moves the horizon by only
        //    a fraction of a degree per second.
        //
        //    Tier C - DISAGREEMENT, LARGE AND PERSISTENT.  The residual
        //    is large enough that the estimate is more likely to be
        //    stale than merely wrong, and it has persisted for longer
        //    than the rejection timeout.  This is the case where the
        //    glasses were picked up and set down in a new posture.  The
        //    full correction is restored, exactly as Madgwick does.
        //
        //    The critical detail in Tier C is that the correction is
        //    FULL, not partial.  A partial weight can never resolve the
        //    disagreement: the gate stays closed while the residual is
        //    large, so a partial weight cannot shrink the residual, so
        //    the gate never re-opens.  The old code clamped the weight
        //    to 10 % on timeout, which settled the filter into a
        //    permanent 10 %-strength equilibrium that NEVER re-locked
        //    onto gravity - the exact failure the timeout was introduced
        //    to prevent.
        var totalErr = Vector3.Zero;
        LastAccelTrust = 0f;

        if (accelTrust > 0f)
        {
            if (withinGate && magOk)
            {
                // Tier A: AGREEMENT.  The accelerometer and the filter
                // agree, and the magnitude says the reading is
                // consistent with being at rest.  Apply the full
                // proportional correction - this is what pins the
                // virtual horizon to the real world.
                LastAccelTrust = 1f;
                totalErr += accelErr * _tuning.KpAccel;

                if (_tuning.KiAccel > 0f)
                {
                    _integralAccel += accelErr * _tuning.KiAccel * dt;
                    totalErr += _integralAccel;
                }

                // Agreement: the disagreement state is cleared.
                _disagreementSeconds = 0f;
                _inDisagreement = false;
                _recoveryLatched = false;
            }
            else
            {
                // DISAGREEMENT (or an implausible magnitude).
                _disagreementSeconds += dt;
                _inDisagreement = true;

                // How far off is the estimate, and for how long?
                var stale = residualDeg >= _tuning.StaleEstimateAngleDegrees;
                var sustained = _disagreementSeconds >= _tuning.RejectionTimeoutSeconds;

                if (_recoveryLatched)
                {
                    // Already latched: keep pulling the estimate back
                    // until the residual agrees again.
                    LastAccelTrust = 1f;
                    totalErr += accelErr * _tuning.KpAccel;
                }
                else if (stale && sustained && magOk)
                {
                    // Tier C: the estimate is stale.  Restore the full
                    // correction and LATCH it, so it stays in effect
                    // until the residual comes back inside the gate.
                    //
                    // The magnitude must still be plausible here.  A
                    // device that is being translated for 4 s is not
                    // "stale", it is MOVING, and locking onto its
                    // apparent gravity would tilt the horizon by the
                    // full amount of the linear acceleration.  Only a
                    // device that is genuinely at rest (1 g, still)
                    // can have a stale estimate.
                    _recoveryLatched = true;
                    LastAccelTrust = 1f;
                    totalErr += accelErr * _tuning.KpAccel;
                }
                else if (_stationary && magOk)
                {
                    // Tier B: a still device whose reading is plausible
                    // but disagrees with the estimate.  The
                    // accelerometer is probably measuring gravity and
                    // the filter's estimate has drifted, so slew towards
                    // it - but at a crawl.
                    //
                    // The slew is a constant ANGULAR RATE rather than a
                    // proportional gain, so it never overshoots and its
                    // speed does not depend on how wrong the estimate is.
                    LastAccelTrust = 0.5f;
                    totalErr += SlowRealign(accelErr, residualDeg);
                }
                else
                {
                    // Moving, or the magnitude says there is real linear
                    // acceleration.  Ignore the accelerometer entirely:
                    // the gyro is the better reference over a short
                    // window.
                    _integralAccel = Vector3.Zero;
                }
            }
        }
        else
        {
            // An explicit caller override of 0 is absolute: the caller
            // has said "no accelerometer, ever".
            _disagreementSeconds = 0f;
            _inDisagreement = false;
            _recoveryLatched = false;
            _integralAccel = Vector3.Zero;
        }

        // SLEW-RATE CLAMP.  The correction is an angular rate, and
        // capping it guarantees that no single bad sample - or even a
        // sustained bad period - can move the virtual horizon faster
        // than a couple of degrees per second.  Monado's 3DoF filter
        // uses the same idea.
        //
        // The limit is looser while RECOVERING: the filter has already
        // decided its own estimate is stale, so the measurement is the
        // trustworthy one and the correction needs to be able to pull
        // the estimate back briskly.
        var maxRate = _recoveryLatched
            ? _tuning.MaxRecoveryCorrectionRateRadPerSecond
            : _tuning.MaxCorrectionRateRadPerSecond;

        var correctionRate = totalErr.Length();
        if (correctionRate > maxRate)
        {
            totalErr *= maxRate / correctionRate;
        }

        // The magnetometer path.  Off by default indoors (see the
        // constructor), but kept so the option remains available.
        if (_useMagnetometer
            && _sampleCount >= MagWarmupSamples
            && IsFinite(sample.Mag)
            && sample.Mag.LengthSquared() > 1e-3f)
        {
            // Rotate the magnetometer reading into the world frame so
            // the heading reference is in the horizontal plane only.
            var mWorld = Vector3.Transform(sample.Mag, _q);
            var mH = new Vector3(mWorld.X, 0, mWorld.Z);
            if (mH.LengthSquared() > 1e-3f)
            {
                mH = Vector3.Normalize(mH);
                var mHat = Vector3.Transform(mH, Quaternion.Inverse(_q));

                // The magnetometer's horizontal direction, in the body
                // frame, is the same kind of quantity: the world-frame
                // north direction rotated into the device frame.
                var estNorth = RotateByQuatInverse(Vector3.UnitX, _q);
                // Project both into the horizontal plane.
                var measuredNorth = Vector3.Cross(Vector3.UnitY, Vector3.Cross(mHat, Vector3.UnitY));
                if (measuredNorth.LengthSquared() > 1e-6f)
                {
                    measuredNorth = Vector3.Normalize(measuredNorth);
                    var magCorrection = Vector3.Cross(mHat, estNorth);
                    totalErr += magCorrection * _tuning.KpMag;
                    if (_tuning.KiMag > 0f)
                    {
                        _integralMag += magCorrection * _tuning.KiMag * dt;
                        totalErr += _integralMag;
                    }
                }
            }
        }

        // 9. Apply the correction to the gyro and integrate.
        var corrected = gyro + totalErr;
        _q = Integrate(_q, corrected, dt);

        _sampleCount++;
        return _sampleCount < MinSamplesForStableEstimate ? FusionResult.Warmup : FusionResult.Ok;
    }

    /// <summary>
    /// Integrate a quaternion by a body-frame angular rate.
    /// </summary>
    /// <remarks>
    /// The quaternion is device-&gt;world, so the body-frame kinematic
    /// equation is <c>q_dot = 0.5 * q (x) omega_body</c>.  The Hamilton
    /// product q (x) omega for q = (w, x, y, z) and omega = (0, gx, gy, gz)
    /// is:
    ///
    ///   w' = -x*gx - y*gy - z*gz
    ///   x' =  w*gx + y*gz - z*gy
    ///   y' =  w*gy - x*gz + z*gx
    ///   z' =  w*gz + x*gy - y*gx
    ///
    /// Two mistakes lived here for a long time, and together they made
    /// the filter look like it needed gain tuning when it actually
    /// needed arithmetic:
    ///
    ///  1. The components were handed to System.Numerics' Quaternion
    ///     constructor in (w, x, y, z) order, but that constructor takes
    ///     (X, Y, Z, W).  Every component landed one slot off, so the
    ///     filter integrated a rotation about a different axis than the
    ///     gyro reported.
    ///  2. The sign of the w*gy term in y' was flipped.
    ///
    /// Symptom: a rotation about device Y (yaw — the axis the Rokid Max
    /// user turns their head on) integrated to -66 deg instead of +90
    /// deg, while rotations about X and Z came out correct.  Because yaw
    /// is the axis with no gravity reference, the error was invisible in
    /// the tilt and fatal in the heading.
    ///
    /// The result is normalised, so the quaternion stays a unit
    /// quaternion even over a long session.
    /// </remarks>
    private static Quaternion Integrate(Quaternion q, Vector3 omega, float dt)
    {
        var gx = omega.X * 0.5f * dt;
        var gy = omega.Y * 0.5f * dt;
        var gz = omega.Z * 0.5f * dt;

        var dq = new Quaternion(
            q.W * gx + q.Y * gz - q.Z * gy,
            q.W * gy - q.X * gz + q.Z * gx,
            q.W * gz + q.X * gy - q.Y * gx,
            -q.X * gx - q.Y * gy - q.Z * gz);

        return Quaternion.Normalize(q + dq);
    }

    /// <summary>
    /// Track whether the device has been still long enough to trust the
    /// accelerometer outright.
    /// </summary>
    private void UpdateStationaryState(Vector3 gyro, float accelNorm, float dt)
    {
        var still = gyro.Length() < _tuning.StationaryGyroThreshold
                 && MathF.Abs(accelNorm - _tuning.GravityMagnitude) < _tuning.MagnitudeTolerance;

        if (!still)
        {
            _stationaryTimer = 0f;
            _stationary = false;
            return;
        }

        _stationaryTimer += dt;
        if (_stationaryTimer >= _tuning.StationaryTimeSeconds)
        {
            _stationary = true;
        }
    }

    /// <summary>
    /// A very slow, constant-rate slew towards the accelerometer's
    /// gravity direction.
    /// </summary>
    /// <remarks>
    /// This is the "tier B" correction: the accelerometer and the
    /// filter's estimate disagree by a small amount, and the product
    /// requirement is that the virtual horizon must NOT tilt to follow
    /// it.  But the estimate still has to be pulled back eventually,
    /// because the filter's own tilt drift is real (measured at about
    /// 0.3 deg/min on the Rokid Max) and nothing else corrects it.
    ///
    /// So the correction is a constant angular RATE rather than a
    /// proportional gain.  That has two useful properties:
    ///
    ///   * It never overshoots, because the rate does not grow with the
    ///     error - a proportional gain would accelerate into the target.
    ///   * Its speed is independent of the size of the disagreement, so
    ///     a huge accidental error does not produce a fast lurch.
    ///
    /// The default rate of 0.2 deg/s cancels the filter's drift in well
    /// under a minute, while moving the horizon by only 0.6 deg over a
    /// three-second lean - comfortably below the threshold at which the
    /// movement becomes visible.
    /// </remarks>
    private Vector3 SlowRealign(Vector3 accelErr, float residualDeg)
    {
        var axisLen = accelErr.Length();
        if (axisLen < 1e-6f) return Vector3.Zero;

        var rateRadPerSecond = _tuning.GravityRealignRateDegreesPerSecond * MathF.PI / 180f;
        var residualRad = residualDeg * MathF.PI / 180f;

        // Never ask for more rotation than is needed to close the gap,
        // so the slew converges exactly rather than sailing past.
        var rate = MathF.Min(rateRadPerSecond, residualRad);

        return accelErr / axisLen * rate;
    }

    private void TrySeedFromAccel(in ImuSample sample)
    {
        var a = sample.Accel;
        if (!IsFinite(a)) return;
        var n = a.Length();
        if (n < 1e-3f) return;
        a /= n;
        // The accelerometer reads the direction of world "up" expressed
        // in the DEVICE frame (the reaction to gravity).  We need the
        // rotation that maps the device frame onto the world frame, so
        // we look for the quaternion q with
        //
        //     R^T * up == a     <=>    q rotates a onto up
        //
        // i.e. the axis is cross(a, up) — the measured vector first.
        var axis = Vector3.Cross(a, Vector3.UnitY);
        var axisLen = axis.Length();
        if (axisLen < 1e-6f)
        {
            // a is parallel to up: either identity or 180 deg about X.
            if (a.Y < 0)
            {
                _q = new Quaternion(1, 0, 0, 0); // 180 deg about X
            }
            return;
        }
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(a, Vector3.UnitY), -1f, 1f));
        _q = Quaternion.CreateFromAxisAngle(axis / axisLen, angle);
    }

    private static Vector3 RotateByQuat(Vector3 v, Quaternion q)
    {
        // v' = q * v * q*
        var t = 2f * Vector3.Cross(new Vector3(q.X, q.Y, q.Z), v);
        return v + q.W * t + Vector3.Cross(new Vector3(q.X, q.Y, q.Z), t);
    }

    /// <summary>
    /// Rotate a vector by the INVERSE of q:  v' = q^-1 * v * q.
    /// </summary>
    /// <remarks>
    /// Because q is device-&gt;world, this is the operation that takes a
    /// world-frame direction (like "up") into the device frame — which
    /// is the frame the accelerometer reading lives in.
    /// </remarks>
    private static Vector3 RotateByQuatInverse(Vector3 v, Quaternion q)
    {
        var qi = Quaternion.Inverse(q);
        var t = 2f * Vector3.Cross(new Vector3(qi.X, qi.Y, qi.Z), v);
        return v + qi.W * t + Vector3.Cross(new Vector3(qi.X, qi.Y, qi.Z), t);
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
