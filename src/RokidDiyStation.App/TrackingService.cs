// SPDX-License-Identifier: MIT
//
// TrackingService: owns the HID device, the spatial anchor, the
// smoothing filter and the UDP emitter.  Exposes events for the UI
// (status, samples/s, last position) and a single Start/Stop pair.
//
// Lives in a background thread internally so the UI thread stays
// responsive; samples arrive at 437 Hz but the UI only needs ~5 Hz
// updates.
using System.IO;
using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.HidCapture;
using RokidDiyStation.Core.Output;
using RokidDiyStation.Core.Protocol;

namespace RokidDiyStation.App;

public enum TrackingState
{
    Stopped,
    Searching,
    Streaming,
    Error,
}

public sealed record TrackingStatus(
    TrackingState State,
    string Message,
    int SamplesPerSecond,
    bool GyroCalibrated,
    bool IsStationary,
    Vector3 Position,
    (double RollDeg, double PitchDeg, double YawDeg) Euler);

public sealed class TrackingService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private TrackingStatus _last = new(TrackingState.Stopped, "Idle", 0, false, false, Vector3.Zero, (0, 0, 0));
    private string? _lastError;
    private readonly OrientationSmoother _smoother;
    private RokidDiyStation.Core.Fusion.SpatialAnchor? _anchor;

    /// <summary>
    /// The spatial station, if one has been attached.
    /// </summary>
    /// <remarks>
    /// The tracking service owns the IMU stream, and the spatial
    /// station needs every sample in order to know where the user is
    /// looking.  Rather than have two things subscribe to the HID
    /// device, the station is attached to the tracking service and the
    /// samples are forwarded to it here.
    /// </remarks>
    private Spatial.SpatialStation? _station;

    public event Action<TrackingStatus>? StatusChanged;
    public event Action<string>? OnLog;

    /// <summary>Attach the spatial station so it receives IMU samples.</summary>
    public void AttachStation(Spatial.SpatialStation station) => _station = station;

    /// <summary>
    /// The live anchor, so the renderer can share it.
    /// </summary>
    public RokidDiyStation.Core.Fusion.SpatialAnchor? Anchor => Volatile.Read(ref _anchor);

    public TrackingStatus Last => _last;
    public AppSettings Settings => _settings;
    public string? LastError => _lastError;

    public TrackingService(AppSettings settings, SettingsService settingsService)
    {
        _settings = settings;
        _settingsService = settingsService;
        _smoother = new OrientationSmoother(settings.Smoothing);
        // The anchor is created up front, not when the device opens, so
        // that the renderer can share it from the first frame.  The
        // renderer and the tracker MUST use the same anchor: they are
        // two views of one world frame.
        _anchor = new SpatialAnchor(useMagnetometer: settings.UseMagnetometer);
    }

    /// <summary>Where diagnostic lines are mirrored to disk so the user
    /// can read them in Notepad even if the in-app log is unscrollable.</summary>
    public static readonly string DiagnosticLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RokidDiyStation", "diag.log");

    public void ApplySettings(AppSettings next)
    {
        // The settings are already persisted by SettingsService.Update.
        // The smoother's alpha can be changed at runtime without
        // restarting the stream.
        _smoother.Reset();
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_thread is { IsAlive: true }) return;
            _cts = new CancellationTokenSource();
            _thread = new Thread(Loop) { IsBackground = true, Name = "TrackingService" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _thread = null;
        }
    }

    /// <summary>
    /// "Pin the panel where I am looking right now."
    /// </summary>
    /// <remarks>
    /// This re-pins the panel at the current gaze WITHOUT moving the
    /// world origin and WITHOUT disturbing the fusion's calibration.
    /// It is the fast "put the panel back in front of me" action: the
    /// panel's world pose is replaced, but the head-pose reference that
    /// the fusion maintains is left alone, so the gyro bias estimate
    /// (which takes a couple of seconds to re-establish) survives.
    ///
    /// It deliberately does NOT rebase the anchor.  Rebasing rotates
    /// the anchor frame, and the panel's stored pose is expressed in
    /// that frame, so rebasing without re-pinning would make the panel
    /// lurch sideways.  <see cref="Recenter"/> does both, in the right
    /// order.
    /// </remarks>
    public void SetAnchorHere()
    {
        // The panel is owned by the spatial station; signal it through
        // a thread-safe request so the re-pin happens on the tracking
        // thread (which owns the anchor the panel is read from).
        _pendingAnchorReset = AnchorRequest.Set;
    }

    /// <summary>
    /// Throw away the fusion's state and forget the panel.
    /// </summary>
    /// <remarks>
    /// This is the "something is wrong, start over" action.  The gyro
    /// bias estimate and the position integrator are discarded, so the
    /// filter has to re-calibrate (about a second of stillness), and
    /// the panel is cleared because its world pose was expressed
    /// against a reference that no longer exists.
    /// </remarks>
    public void ResetAnchor()
    {
        _pendingAnchorReset = AnchorRequest.Reset;
    }

    /// <summary>
    /// The "I am looking straight ahead now" button.
    /// </summary>
    /// <remarks>
    /// This does two things: it moves the world origin to the current
    /// head pose, and it re-pins the spatial panel there.  Both have to
    /// happen together — re-pinning without moving the origin would
    /// leave the panel's stored pose inconsistent with the head pose
    /// the user re-anchored to, and moving the origin without
    /// re-pinning would rotate the anchor frame out from under the
    /// panel and make it lurch.
    ///
    /// This is the drift-recovery action.  A gyroscope has no absolute
    /// heading, so the panel inevitably creeps away from where the user
    /// is looking; rebasing the origin resets that reference, and
    /// re-pinning puts the panel back in front of them.
    ///
    /// The anchor mutation happens on the tracking thread (which owns
    /// the anchor); the panel re-pinning is handed to the spatial
    /// station, which owns the panel and does it on its pump thread.
    /// </remarks>
    public void Recenter()
    {
        _pendingAnchorReset = AnchorRequest.Recenter;
    }

    private enum AnchorRequest { None, Set, Reset, Recenter }
    private AnchorRequest _pendingAnchorReset;
    private readonly object _anchorReqLock = new();
    private AnchorRequest ConsumeAnchorRequest()
    {
        lock (_anchorReqLock)
        {
            var r = _pendingAnchorReset;
            _pendingAnchorReset = AnchorRequest.None;
            return r;
        }
    }

    private void Loop()
    {
        var token = _cts!.Token;
        Publish(new TrackingStatus(TrackingState.Searching, "Looking for Rokid device...", 0, false, false, Vector3.Zero, (0, 0, 0)));

        IHidCapture? device = null;
        try
        {
            device = HidSharpEnumerator.OpenFirst();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Publish(new TrackingStatus(TrackingState.Error, $"No device: {ex.Message}", 0, false, false, Vector3.Zero, (0, 0, 0)));
            return;
        }
        if (device is null)
        {
            Publish(new TrackingStatus(TrackingState.Error, "No Rokid Max found (VID=0x04D2 PID=0x162F).", 0, false, false, Vector3.Zero, (0, 0, 0)));
            return;
        }

        Publish(new TrackingStatus(TrackingState.Streaming, $"Streaming {device.ProductName} (0x{device.VendorId:X4}:0x{device.ProductId:X4})", 0, false, false, Vector3.Zero, (0, 0, 0)));

        // Reuse the shared anchor so the renderer keeps seeing the same
        // world frame across a device reconnect.  A reconnect means the
        // IMU stream restarted, so the pose has to be re-established
        // from scratch.
        var anchor = _anchor ??= new SpatialAnchor(useMagnetometer: _settings.UseMagnetometer);
        Volatile.Write(ref _anchor, anchor);
        anchor.Reset();
        var emitter = new FreePieEmitter(new FreePieEmitterOptions(
            TargetHost: _settings.UdpHost,
            TargetPort: _settings.UdpPort,
            MaxPacketsPerSecond: _settings.MaxPacketsPerSecond,
            Flags: FreePieFlags.Raw | FreePieFlags.Orient,
            IncludeRawPosition: true));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastTick = sw.Elapsed;
        var samplesThisSecond = 0;
        var lastUiTick = sw.Elapsed;
        var lastDebugTick = sw.Elapsed;
        // Accumulators for raw IMU magnitude AND per-axis sums so we
        // can dump once a second.  Used to diagnose whether Euler
        // noise is real head motion, sensor bias on a particular
        // axis, or filter instability.
        double accGyroMag = 0, accAccelMag = 0, accMagMag = 0;
        Vector3 accGyroSum = Vector3.Zero, accAccelSum = Vector3.Zero, accMagSum = Vector3.Zero;
        int accCount = 0;
        var lastSampleForDebug = new ImuSample(0, Vector3.Zero, Vector3.Zero, Vector3.Zero, false, 0);
        var wasCalibrated = false;
        var autoAnchored = false;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var req = ConsumeAnchorRequest();
                if (req == AnchorRequest.Set)
                {
                    // Re-pin the panel at the current gaze, but leave
                    // the anchor's frame and the fusion's calibration
                    // alone.
                    _station?.AnchorHere();
                }
                else if (req == AnchorRequest.Reset)
                {
                    // Throw the fusion's state away and forget the
                    // panel: the world origin is gone, so the panel's
                    // stored pose is meaningless.
                    anchor.Reset();
                    _station?.ForgetPanel();
                }
                else if (req == AnchorRequest.Recenter)
                {
                    // Move the world origin AND re-pin the panel, in
                    // that order: the panel's pose is expressed in the
                    // anchor frame, so the frame has to be rebased
                    // before the panel is pinned into it.  Doing it in
                    // the other order would leave the panel one frame
                    // stale — a visible lurch.
                    _station?.Recenter();
                }

                if (!device.TryRead(timeoutMs: 100, out var report)) continue;
                var result = PacketParser.Parse(report);
                if (!result.IsOk) continue;
                var sample = result.Value;
                // Accumulate raw magnitudes AND per-axis sums for the
                // 1 s diagnostic.  Per-axis data is critical to
                // distinguish a true motion from a single-axis bias.
                accGyroMag  += sample.Gyro.Length();
                accAccelMag += sample.Accel.Length();
                accMagMag   += sample.Mag.Length();
                accGyroSum  += sample.Gyro;
                accAccelSum += sample.Accel;
                accMagSum   += sample.Mag;
                accCount++;
                lastSampleForDebug = sample;
                anchor.Update(sample);
                _station?.FeedImu(sample);
                var (q, p) = _smoother.Update(anchor.HeadOrientation, anchor.HeadPosition);
                emitter.Send(q, p);
                samplesThisSecond++;
                // Detect the Calibrating -> Running transition so we
                // can auto-anchor once.  Auto-anchoring at this moment
                // (when the filter has just declared itself stable)
                // means "wherever the user is wearing the glasses and
                // looking forward" becomes the world origin — exactly
                // what they want for spatial-anchor-driven games and
                // recentring.
                if (!autoAnchored && anchor.IsGyroCalibrated)
                {
                    autoAnchored = true;
                    anchor.SetAnchorHere();
                    AppendLogSafe("Calibration complete — auto-anchored at current pose. Use 'Set Anchor' to re-anchor.");
                }

                var now = sw.Elapsed;
                if ((now - lastDebugTick).TotalSeconds >= 1.0)
                {
                    var avgGyro  = accCount > 0 ? accGyroMag  / accCount : 0;
                    var avgAccel = accCount > 0 ? accAccelMag / accCount : 0;
                    var avgMag   = accCount > 0 ? accMagMag   / accCount : 0;
                    var avgG = accCount > 0 ? accGyroSum  / accCount : Vector3.Zero;
                    var avgA = accCount > 0 ? accAccelSum / accCount : Vector3.Zero;
                    var avgM = accCount > 0 ? accMagSum   / accCount : Vector3.Zero;
                    var (dr, dp, dy) = SyntheticImuSource.ToEulerZYX(q);
                    AppendLogSafe(
                        $"DBG 1s: " +
                        $"gyro=({avgG.X:F4},{avgG.Y:F4},{avgG.Z:F4})|g|={avgGyro:F3} rad/s  " +
                        $"acc=({avgA.X:F2},{avgA.Y:F2},{avgA.Z:F2})|a|={avgAccel:F2} m/s²  " +
                        $"mag=({avgM.X:F1},{avgM.Y:F1},{avgM.Z:F1})  " +
                        // ToEulerZYX already returns DEGREES.  Multiplying
                        // by 180/pi a second time turned a 10-degree
                        // pitch into 1745 degrees and fired the gimbal
                        // -lock warning on every single sample.
                        $"eul=({dr:F1}, {dp:F1}, {dy:F1})  " +
                        $"cal={anchor.IsGyroCalibrated} stat={anchor.IsStationary}");
                    // If |pitch| is near ±90° we're in the gimbal-lock
                    // neighbourhood and the Euler decomposition is
                    // numerically unstable (small noise => huge
                    // angle jumps).  Surface that as a warning so the
                    // user knows to set an anchor at a more
                    // head-natural pitch (around 0..30°).
                    if (MathF.Abs(dp) > 80f)
                    {
                        AppendLogSafe($"WARN: pitch={dp:F1}° near gimbal lock — set anchor or use quaternion output.");
                    }
                    accGyroMag = accAccelMag = accMagMag = 0;
                    accGyroSum = accAccelSum = accMagSum = Vector3.Zero;
                    accCount = 0;
                    lastDebugTick = now;
                }

                // UI tick every 200ms so the status panel feels live.
                if ((now - lastUiTick).TotalMilliseconds >= 200)
                {
                    lastUiTick = now;
                    var sps = (int)(samplesThisSecond / (now - lastTick).TotalSeconds);
                    var (r, pi, ya) = SyntheticImuSource.ToEulerZYX(q);
                    Publish(new TrackingStatus(
                        TrackingState.Streaming,
                        $"Streaming {device.ProductName}",
                        sps,
                        anchor.IsGyroCalibrated,
                        anchor.IsStationary,
                        p,
                        // ToEulerZYX already returns degrees, and the
                        // status record is documented in degrees, so
                        // there is no conversion here.
                        (r, pi, ya)));
                }
                if ((now - lastTick).TotalSeconds >= 1.0)
                {
                    samplesThisSecond = 0;
                    lastTick = now;
                }
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Publish(new TrackingStatus(TrackingState.Error, $"Stream error: {ex.Message}", 0, false, false, Vector3.Zero, (0, 0, 0)));
        }
        finally
        {
            emitter.Dispose();
            device.Dispose();
            Publish(new TrackingStatus(TrackingState.Stopped, "Stopped", 0, false, false, Vector3.Zero, (0, 0, 0)));
        }
    }

    private void Publish(TrackingStatus s)
    {
        _last = s;
        try { StatusChanged?.Invoke(s); } catch { /* UI may be gone */ }
    }

    private void AppendLogSafe(string line)
    {
        try { OnLog?.Invoke(line); } catch { /* UI may be gone */ }
        // File mirroring is handled by MainWindow.AppendLog (which
        // appends every UI log line, DBG included).  Doing it here
        // too would double-write the same line.
    }

    public void Dispose()
    {
        Stop();
        try { _cts?.Dispose(); } catch { }
    }
}
