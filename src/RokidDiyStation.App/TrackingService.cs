// SPDX-License-Identifier: MIT
//
// TrackingService: owns the HID device, the spatial anchor, the
// smoothing filter and the UDP emitter.  Exposes events for the UI
// (status, samples/s, last position) and a single Start/Stop pair.
//
// Lives in a background thread internally so the UI thread stays
// responsive; samples arrive at 437 Hz but the UI only needs ~5 Hz
// updates.
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

    public event Action<TrackingStatus>? StatusChanged;

    public TrackingStatus Last => _last;
    public AppSettings Settings => _settings;
    public string? LastError => _lastError;

    public TrackingService(AppSettings settings, SettingsService settingsService)
    {
        _settings = settings;
        _settingsService = settingsService;
        _smoother = new OrientationSmoother(settings.Smoothing);
    }

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

    public void SetAnchorHere()
    {
        // The anchor is owned by the tracking loop; signal it through
        // a thread-safe property.
        _pendingAnchorReset = AnchorRequest.Set;
    }

    public void ResetAnchor()
    {
        _pendingAnchorReset = AnchorRequest.Reset;
    }

    private enum AnchorRequest { None, Set, Reset }
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

        var anchor = new SpatialAnchor(useMagnetometer: _settings.UseMagnetometer);
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

        try
        {
            while (!token.IsCancellationRequested)
            {
                var req = ConsumeAnchorRequest();
                if (req == AnchorRequest.Set) anchor.SetAnchorHere();
                else if (req == AnchorRequest.Reset) anchor.Reset();

                if (!device.TryRead(timeoutMs: 100, out var report)) continue;
                var result = PacketParser.Parse(report);
                if (!result.IsOk) continue;
                anchor.Update(result.Value);
                var (q, p) = _smoother.Update(anchor.HeadOrientation, anchor.HeadPosition);
                emitter.Send(q, p);
                samplesThisSecond++;

                var now = sw.Elapsed;
                if ((now - lastTick).TotalSeconds >= 1.0)
                {
                    var sps = samplesThisSecond;
                    samplesThisSecond = 0;
                    lastTick = now;
                    if ((now - lastUiTick).TotalMilliseconds >= 200)
                    {
                        lastUiTick = now;
                        var (r, pi, ya) = SyntheticImuSource.ToEulerZYX(q);
                        Publish(new TrackingStatus(
                            TrackingState.Streaming,
                            $"Streaming {device.ProductName}",
                            sps,
                            anchor.IsGyroCalibrated,
                            anchor.IsStationary,
                            p,
                            (r * 180 / Math.PI, pi * 180 / Math.PI, ya * 180 / Math.PI)));
                    }
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

    public void Dispose()
    {
        Stop();
        try { _cts?.Dispose(); } catch { }
    }
}
