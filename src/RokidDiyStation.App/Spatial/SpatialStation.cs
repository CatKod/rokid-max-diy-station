// SPDX-License-Identifier: MIT
//
// SpatialStation: the piece that makes the whole thing a product.
//
// Three subsystems have to be married for the Rokid Max to show a
// window floating in space:
//
//   1. the IMU stream  -> where the user is looking      (TrackingService)
//   2. the capture     -> what the panel shows           (WindowCapture)
//   3. the renderer    -> how it looks on the Micro-OLED (PanelRenderer)
//
// The renderer owns a dedicated thread and its own D3D device, and the
// capture must share that device (a WGC frame pool's textures live on
// the adapter of the device it was given).  So the capture can only be
// started once the renderer has a device, which is asynchronous.
//
// This class is the mediator.  It:
//   - finds the glasses' display and starts the renderer on it,
//   - waits for the renderer's device, then starts the capture,
//   - pumps the capture's frames and hands the resulting shader
//     resource view to the renderer,
//   - feeds the IMU samples into the anchor service the renderer
//     reads its camera from.
//
// Everything here is failure-tolerant: the glasses can be unplugged,
// the captured window can be closed, the display layout can change.
// None of that should take the tray app down.
using System.IO;
using System.Numerics;
using RokidDiyStation.Core.Spatial;
using Vortice.Direct3D11;

namespace RokidDiyStation.App.Spatial;

/// <summary>What the spatial station is currently doing.</summary>
public enum SpatialState
{
    Idle,
    Searching,
    Capturing,
    Error,
}

public sealed class SpatialStation : IDisposable
{
    private readonly object _lock = new();
    private readonly PanelRenderer _renderer = new();
    private readonly WindowCapture _capture = new();
    private readonly SpatialAnchorService _anchor;
    private DisplayGuard? _guard;

    private Thread? _pumpThread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private SpatialState _state = SpatialState.Idle;
    private string _message = "Idle";
    private string? _preferredDisplayPath;

    /// <summary>Raised when the station's state changes.</summary>
    public event Action<SpatialState, string>? OnStatus;

    /// <summary>Raised for informational messages worth showing to the user.</summary>
    public event Action<string>? OnLog;

    /// <summary>The anchor service the renderer reads its camera from.</summary>
    public SpatialAnchorService Anchor => _anchor;

    /// <summary>The renderer, so the UI can ask about it.</summary>
    public PanelRenderer Renderer => _renderer;

    /// <summary>Current state.</summary>
    public SpatialState State => _state;

    /// <summary>Human-readable description of the current state.</summary>
    public string Message => _message;

    public SpatialStation(
        SpatialAnchorOptions? options = null,
        Core.Fusion.SpatialAnchor? sharedAnchor = null)
    {
        _anchor = new SpatialAnchorService(options, sharedAnchor);
        _renderer.OnError += msg => Log($"Renderer: {msg}");
        _renderer.OnFrameRate += fps => OnFrameRate?.Invoke(fps);
        _capture.OnSourceClosed += () => Log("The captured window was closed.");
    }

    /// <summary>Raised with the renderer's measured frame rate.</summary>
    public event Action<double>? OnFrameRate;

    /// <summary>Raised when the station hits an error worth reporting.</summary>
    public event Action<string>? OnError;

    /// <summary>True while a capture is running.</summary>
    public bool IsCapturing => _capture.IsActive;

    /// <summary>
    /// The most recent captured frame, as a shader resource view.
    /// </summary>
    /// <remarks>
    /// The SRV is owned by the capture.  The renderer borrows it for
    /// the duration of a draw; nothing else should hold on to it.
    /// </remarks>
    public ID3D11ShaderResourceView? CaptureTexture => _capture.Texture;

    /// <summary>The captured window's size in pixels.</summary>
    public (int Width, int Height) CaptureSize => _capture.Size;

    // -----------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------

    /// <summary>
    /// Start the station: locate the glasses' display, open the render
    /// window on it, and begin capturing.
    /// </summary>
    /// <param name="sourceWindow">The window to capture and pin.  Zero means none yet.</param>
    public void Start(IntPtr sourceWindow = default)
    {
        lock (_lock)
        {
            if (_running) return;

            var display = DisplayLocator.Select(_preferredDisplayPath);
            if (display is null)
            {
                SetState(SpatialState.Error, "No display found — is the Rokid Max plugged in?");
                return;
            }

            Log($"Display: {display}");

            _cts = new CancellationTokenSource();
            _running = true;
            _renderer.Start(_anchor, display);

            // The display guard keeps the panel window on the glasses'
            // display.  Without it, unplugging the USB-C cable makes
            // DWM relocate our borderless 1920x1080 topmost window
            // onto the primary display, where it covers the user's
            // entire screen and cannot be dismissed.
            _guard ??= new DisplayGuard(_renderer, display);
            _guard.OnLog += line => Log(line);
            PanelRenderer.DisplayChanged += _guard.Check;

            _pumpThread = new Thread(() => PumpLoop(_cts!.Token))
            {
                IsBackground = true,
                Name = "SpatialStation",
            };
            _pumpThread.Start();

            if (sourceWindow != default)
                QueueCapture(sourceWindow);
        }
    }

    /// <summary>Stop everything and tear down the GPU resources.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            _cts?.Cancel();
            _cts = null;
        }

        // Drop the renderer's borrowed reference to the capture's
        // texture before the capture disposes it — see StopCapture for
        // why the order matters.
        _renderer.PanelTexture = null;
        _capture.Stop();
        _renderer.Stop();
        SetState(SpatialState.Idle, "Stopped");
    }

    /// <summary>
    /// Point the station at a different display (e.g. the user moved
    /// the glasses to another port and the layout changed).
    /// </summary>
    public void SetPreferredDisplay(string? devicePath)
    {
        _preferredDisplayPath = devicePath;
        var display = DisplayLocator.Select(devicePath);
        if (display is not null) _renderer.Reposition(display);
    }
    /// <summary>
    /// Capture a window and show it on the panel.
    /// </summary>
    /// <remarks>
    /// The capture can only start once the renderer has created its
    /// D3D device, so this is a request, not an immediate action.  The
    /// pump thread picks it up as soon as the device is ready.
    /// </remarks>
    public void QueueCapture(IntPtr hwnd)
    {
        if (hwnd == default) return;
        lock (_lock)
        {
            _pendingCapture = hwnd;
            // Switching source: tear the old capture down so the pump
            // starts a fresh one for the new window.
            if (_capture.IsActive && _capture.SourceWindow != hwnd)
            {
                // Drop the renderer's borrowed reference first — see
                // StopCapture for why the order matters.
                _renderer.PanelTexture = null;
                _capture.Stop();
            }
            if (!_running) Start(hwnd);
        }
    }

    private IntPtr _pendingCapture = default;

    /// <summary>Stop capturing, leaving the panel blank.</summary>
    public void StopCapture()
    {
        lock (_lock)
        {
            _pendingCapture = default;
            _stopRequested = true;
        }
        // Drop the renderer's reference to the capture's texture BEFORE
        // stopping the capture.
        //
        // The order matters: the capture owns the shader resource view,
        // and the renderer is holding a borrowed pointer to it.  If the
        // capture is torn down first, the renderer can draw with a
        // disposed COM object, which throws a NullReferenceException
        // from deep inside D3D and takes the render thread down with
        // it.
        _renderer.PanelTexture = null;
        _capture.Stop();
        SetState(SpatialState.Searching, "Not capturing");
    }

    /// <summary>
    /// Set when the user explicitly stopped the capture, so the pump
    /// does not immediately restart it.
    /// </summary>
    private bool _stopRequested;

    // -----------------------------------------------------------------
    // The pump
    // -----------------------------------------------------------------

    private void PumpLoop(CancellationToken token)
    {
        SetState(SpatialState.Searching, "Waiting for the render device…");

        while (!token.IsCancellationRequested && _running)
        {
            // The renderer creates its device on its own thread; until
            // it exists we cannot create the WGC frame pool.
            var device = _renderer.Device;
            if (device is null)
            {
                Thread.Sleep(50);
                continue;
            }

            // Start the capture if one was requested and we have not
            // started it yet.
            IntPtr wanted;
            lock (_lock) wanted = _pendingCapture;
            if (wanted != default && !_capture.IsActive)
            {
                try
                {
                    _capture.Start(wanted, device);
                    lock (_lock) _stopRequested = false;
                    Log($"Capturing window 0x{wanted.ToInt64():X} at {_capture.Size.Width}x{_capture.Size.Height}.");
                    SetState(SpatialState.Capturing, "Capturing");
                }
                catch (Exception ex)
                {
                    Log($"Capture failed: {ex.Message}");
                    SetState(SpatialState.Error, $"Capture failed: {ex.Message}");
                    lock (_lock) _pendingCapture = default;
                    Thread.Sleep(500);
                    continue;
                }
            }
            else if (wanted == default && _capture.IsActive)
            {
                bool stopped;
                lock (_lock) stopped = _stopRequested;
                if (stopped)
                {
                    // Drop the renderer's borrowed reference first.
                    _renderer.PanelTexture = null;
                    _capture.Stop();
                    SetState(SpatialState.Searching, "Not capturing");
                }
            }

            // Pull the newest captured frame and hand it to the
            // renderer.  The SRV is owned by the capture; the renderer
            // only borrows it for the duration of the draw.
            if (_capture.IsActive)
            {
                _capture.TryUpdate();
                _renderer.PanelTexture = _capture.Texture;
            }

            Thread.Sleep(2);
        }
    }

    // -----------------------------------------------------------------
    // IMU feed
    // -----------------------------------------------------------------

    /// <summary>
    /// Feed one IMU sample into the anchor.  Called from the tracking
    /// thread at ~437 Hz.
    /// </summary>
    public void FeedImu(in RokidDiyStation.Core.Protocol.ImuSample sample) => _anchor.Update(sample);

    /// <summary>Pin the panel where the user is currently looking.</summary>
    public void AnchorHere() => _anchor.AnchorHere();

    /// <summary>
    /// Forget the pinned panel, leaving the head-pose reference alone.
    /// </summary>
    /// <remarks>
    /// Used by the "reset" action: the panel's world pose was
    /// expressed against a reference that is about to be discarded, so
    /// the panel has to go with it.  The fusion's calibration is NOT
    /// touched — it is still good, and rebuilding it costs the user a
    /// second of stillness.
    /// </remarks>
    public void ForgetPanel() => _anchor.ForgetPanel();

    /// <summary>
    /// Apply new anchor options (comfort cone, panel distance, neck
    /// model scale) at runtime.
    /// </summary>
    public void ApplyAnchorOptions(Core.Spatial.SpatialAnchorOptions options)
        => _anchor.ApplyOptions(options);

    /// <summary>Make the current gaze the neutral forward direction.</summary>
    public void Recenter() => _anchor.Recenter();

    /// <summary>Forget the anchor and the panel.</summary>
    public void ResetAnchor() => _anchor.Reset();
    // -----------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------

    private void SetState(SpatialState state, string message)
    {
        _state = state;
        _message = message;
        try { OnStatus?.Invoke(state, message); } catch { }
    }

    private void Log(string line)
    {
        try { OnLog?.Invoke(line); } catch { }
    }

    private void ReportError(string line)
    {
        Log(line);
        try { OnError?.Invoke(line); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
        _renderer.Dispose();
        _cts?.Dispose();
    }
}
