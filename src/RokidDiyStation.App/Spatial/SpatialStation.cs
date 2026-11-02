// SPDX-License-Identifier: MIT
//
// SpatialStation: the piece that makes the whole thing a product.
//
// Four subsystems have to be married for the Rokid Max to show a
// floating Windows desktop in space:
//
//   1. the IMU stream  -> where the user is looking      (TrackingService)
//   2. the virtual display -> what the panel shows       (DisplayCapture)
//   3. the renderer    -> how it looks on the Micro-OLED (PanelRenderer)
//   4. the cursor feedback -> where the arrow is         (SpatialPointerFeedback)
//
// The renderer owns a dedicated thread and its own D3D device, and the
// capture must share that device (a WGC frame pool's textures live on
// the adapter of the device it was given).  So the capture can only be
// started once the renderer has a device, which is asynchronous.
//
// This class is the mediator.  It:
//   - finds the glasses' display and starts the renderer on it,
//   - makes sure the IddCx virtual monitor is attached where the
//     spatial panel expects it,
//   - waits for the renderer's device, then starts the MONITOR
//     capture,
//   - pumps the capture's frames and hands the resulting shader
//     resource view to the renderer,
//   - feeds the IMU samples into the anchor service the renderer
//     reads its camera from.
//
// There is NO input layer here, and that is the point.
//
// The virtual monitor is a REAL Windows display, so the operating
// system does all of the input work: hit-testing the mouse against
// the windows on it, activating them, ordering them, dragging them,
// and delivering clicks, wheel ticks and keystrokes.  There is no
// hook, no injection, no DevTools protocol.
//
// What remains of "input" is the cursor feedback loop, which is pure
// presentation: read the OS cursor, map its absolute coordinates into
// the panel's texture space, and draw the 3D quad.
//
// Everything here is failure-tolerant: the glasses can be unplugged,
// the virtual display can be detached, the display layout can change.
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
    private readonly DisplayCapture _capture = new();
    private readonly SpatialAnchorService _anchor;
    private readonly VirtualCursor _cursor = new();
    private SpatialPointerFeedback? _feedback;

    private DisplayGuard? _guard;
    private Thread? _pumpThread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private volatile bool _captureStarted;
    private SpatialState _state = SpatialState.Idle;
    private string _message = "Idle";
    private string? _preferredDisplayPath;

    /// <summary>Raised when the station's state changes.</summary>
    public event Action<SpatialState, string>? OnStatus;

    /// <summary>Raised for informational messages worth showing to the user.</summary>
    public event Action<string>? OnLog;

    /// <summary>Raised when the station hits an error worth reporting.</summary>
    public event Action<string>? OnError;

    /// <summary>Raised with the renderer's measured frame rate.</summary>
    public event Action<double>? OnFrameRate;

    /// <summary>The anchor service the renderer reads its camera from.</summary>
    public SpatialAnchorService Anchor => _anchor;

    /// <summary>The renderer, so the UI can ask about it.</summary>
    public PanelRenderer Renderer => _renderer;

    /// <summary>The 3D cursor this station drives.</summary>
    public VirtualCursor Cursor => _cursor;

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
        _renderer.Cursor = _cursor;

        // The capture pulls its frame HERE, on the render thread.
        //
        // The GPU copy from the captured surface into our own texture
        // is issued on the device's immediate context, and that
        // context is not thread-safe.  Doing it from the station's
        // pump thread interleaved with the renderer's draw calls on
        // the same context - and the panel came out completely black
        // while every status readout said everything was fine.
        _renderer.BeforeDraw = () =>
        {
            _capture.TryUpdate();
            _renderer.PanelTexture = _capture.Texture;
        };

        // The cursor feedback loop.  It reads the OS cursor, works out
        // whether it is over the virtual display, and maps its
        // absolute coordinates into the panel's texture space.
        //
        // This is the ONLY input-related code left in the product.
        _feedback = new SpatialPointerFeedback(_cursor, FindVirtualDisplay);
        _feedback.OnInsideChanged += inside =>
        {
            // Crossing onto the virtual display is the moment the
            // cursor becomes meaningful, so that is when the panel is
            // pinned: the user is looking at the spatial display, and
            // the panel should be where they are looking.
            if (inside) AnchorHere();
        };
    }

    /// <summary>True while the virtual monitor's capture is running.</summary>
    public bool IsCapturing => _captureStarted;

    /// <summary>
    /// The most recent captured frame, as a shader resource view.
    /// </summary>
    /// <remarks>
    /// The SRV is owned by the capture.  The renderer borrows it for
    /// the duration of a draw; nothing else should hold on to it.
    /// </remarks>
    public ID3D11ShaderResourceView? CaptureTexture => _capture.Texture;

    /// <summary>The captured desktop's size in pixels.</summary>
    public (int Width, int Height) CaptureSize => (_capture.Width, _capture.Height);

    /// <summary>
    /// The captured frame's own texture, for diagnostic tools that need
    /// to compare the SOURCE image against what the panel actually
    /// shows.
    /// </summary>
    public Vortice.Direct3D11.ID3D11Texture2D? CapturedTexture => _capture.CapturedTexture;

    /// <summary>True while the OS cursor is over the virtual display.</summary>
    public bool IsPointerInside => _feedback?.IsInside ?? false;

    // -----------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------

    /// <summary>
    /// Start the station: locate the glasses' display, open the render
    /// window on it, and begin capturing the virtual display.
    /// </summary>
    public void Start()
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
        _captureStarted = false;
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

    // -----------------------------------------------------------------
    // The virtual display
    // -----------------------------------------------------------------

    /// <summary>
    /// The virtual display, if it is attached.
    /// </summary>
    public static MonitorInfo? FindVirtualDisplay() => VirtualDisplayLocator.FindVirtual();

    /// <summary>
    /// Attach the virtual display where the spatial panel expects it.
    /// </summary>
    /// <remarks>
    /// The monitor goes to (-1920, 0), immediately to the LEFT of the
    /// primary display.  It must not overlap the glasses' display:
    /// Windows forbids overlapping monitor rectangles outright
    /// (DISP_CHANGE_BADPARAM), and our own WS_EX_TOPMOST opaque AR
    /// panel would occlude the capture.
    ///
    /// This needs no administrator rights: attaching and detaching are
    /// both plain ChangeDisplaySettingsEx calls.
    /// </remarks>
    /// <returns>True when the display is attached afterwards.</returns>
    public bool OpenSpatialDisplay()
    {
        if (!VirtualDisplayLocator.AttachDefault())
        {
            Log("The virtual display could not be attached. "
                + "Is the Virtual Display Driver installed?");
            SetState(SpatialState.Error, "The virtual display is not available.");
            return false;
        }

        // The attach takes a moment to be reflected by the shell.
        for (int i = 0; i < 40 && FindVirtualDisplay() is null; i++)
            Thread.Sleep(50);

        var monitor = FindVirtualDisplay();
        if (monitor is null)
        {
            Log("The virtual display was attached but did not appear.");
            SetState(SpatialState.Error, "The virtual display did not appear.");
            return false;
        }

        Log($"Spatial display open: {monitor.DeviceName} {monitor.Width}x{monitor.Height} "
            + $"at ({monitor.Bounds.Left},{monitor.Bounds.Top}).");
        return true;
    }

    /// <summary>
    /// Take the virtual display off the desktop.
    /// </summary>
    public bool CloseSpatialDisplay()
    {
        var ok = VirtualDisplayLocator.Detach();
        Log(ok ? "Spatial display closed." : "The virtual display could not be detached.");
        return ok;
    }

    /// <summary>
    /// Bring the OS cursor back to the physical desktop.
    /// </summary>
    /// <remarks>
    /// The virtual display lives at NEGATIVE coordinates, so when the
    /// user's mouse is on it the arrow has left the physical screen
    /// and the mouse feels lost.  This is the recovery action: one key
    /// press and the arrow is back on the laptop's screen.
    /// </remarks>
    /// <returns>True when the cursor was moved.</returns>
    public static bool SnapCursorHome()
    {
        var primary = DisplayLocator.Enumerate().FirstOrDefault(d => d.IsPrimary);
        if (primary is null) return false;

        var x = primary.Bounds.X + primary.Bounds.Width / 2;
        var y = primary.Bounds.Y + primary.Bounds.Height / 2;
        return NativeMethods.SetCursorPos(x, y);
    }

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

            var monitor = FindVirtualDisplay();
            if (monitor is null)
            {
                // The virtual display was detached - by the user, or by
                // Windows moving things around.  Stop the capture and
                // wait for it to come back.
                if (_captureStarted)
                {
                    _renderer.PanelTexture = null;
                    _capture.Stop();
                    _captureStarted = false;
                    Log("The virtual display went away.");
                }
                SetState(SpatialState.Searching, "Waiting for the spatial display");
                Thread.Sleep(100);
                continue;
            }

            if (!_captureStarted)
            {
                try
                {
                    _capture.Start(monitor.Handle, device);
                    _captureStarted = true;
                    Log($"Capturing the spatial display at {_capture.Width}x{_capture.Height}.");
                    SetState(SpatialState.Capturing, "Capturing");
                }
                catch (Exception ex)
                {
                    Log($"Capture failed: {ex.Message}");
                    OnError?.Invoke($"Capture failed: {ex.Message}");
                    Thread.Sleep(500);
                    continue;
                }
            }

            // The panel's SHAPE follows the content's shape, or the
            // content is stretched.  The virtual display is 1920x1080,
            // which is exactly the Rokid Max's per-eye viewport, so
            // this normally changes nothing - but if the user changes
            // the virtual display's resolution, the panel follows.
            var (cw, ch) = CaptureSize;
            if (cw > 0 && ch > 0 && _anchor.SetContentSize(cw, ch))
            {
                _anchor.AnchorHere();
                Log($"Panel shape follows the spatial display's aspect: {cw}x{ch}.");
            }

            // The cursor feedback: read the OS cursor, map it into the
            // panel's UV space, and let the renderer draw the 3D quad.
            //
            // This one CAN run on this thread: it is a plain
            // GetCursorInfo call and a float write, with no GPU
            // involvement at all.
            _feedback?.Update();

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

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
        _feedback?.Dispose();
        _renderer.Dispose();
        _cts?.Dispose();
    }
}
