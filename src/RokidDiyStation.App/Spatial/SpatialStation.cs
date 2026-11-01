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
using RokidDiyStation.App.Input;
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
    private VirtualCursor? _cursor;
    private PointerInput? _pointerInput;
    private KeyboardInput? _keyboardInput;
    private SpatialInputCoordinator? _input;

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

    /// <summary>
    /// Raised when the user clicks, scrolls or presses a key while the
    /// pointer is over the glasses' display.
    /// </summary>
    /// <remarks>
    /// This is the hook the input layer uses to forward the interaction
    /// to the captured application - CDP for a browser, an app-native
    /// channel for everything else.
    /// </remarks>
    public event Action<PointerEvent>? OnPointerEvent;

    /// <summary>
    /// Raised when the physical pointer enters or leaves the glasses'
    /// display.
    /// </summary>
    public event Action<bool>? OnPointerInsideChanged;

    /// <summary>
    /// Raised for each keystroke, once the pointer is over the glasses.
    /// </summary>
    /// <remarks>
    /// The keystroke is already resolved into the DevTools protocol's
    /// vocabulary - the DOM key name, the DOM code name and the
    /// character the user's layout produced.
    /// </remarks>
    public event Action<ResolvedKey>? OnKey;

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

        // The virtual cursor and the mouse tracker.  The tracker reads
        // the panel's pose from the anchor service, so the cursor is
        // always resolved against the panel as it is RIGHT NOW - which
        // is what keeps it glued to the content when the head turns.
        _cursor = new VirtualCursor();
        _pointerInput = new PointerInput(
            _cursor,
            () => _anchor.HasPanel ? _anchor.GetView().PanelPose : null,
            () => _anchor.GetView().View,
            () => _anchor.GetProjection());
        _pointerInput.OnPointerEvent += e =>
        {
            OnPointerEvent?.Invoke(e);
            ForwardPointerEvent(e);
        };
        _pointerInput.OnPointerInsideChanged += inside =>
        {
            // The keyboard is captured exactly when the pointer is over
            // the glasses.  That is the user's way of saying "I am
            // working in the glasses now" - and it keeps their real
            // typing working everywhere else.
            if (_keyboardInput is not null) _keyboardInput.IsCapturing = inside;

            OnPointerInsideChanged?.Invoke(inside);
        };
        _pointerInput.OnLog += line => Log(line);
        _renderer.Cursor = _cursor;

        // The keyboard.  It is captured only while the pointer is over
        // the glasses' display, which is the user's signal that they
        // are working in the glasses.  Outside that, the hook only
        // observes and every keystroke still goes to whatever the
        // user was typing into.
        _keyboardInput = new KeyboardInput();
        _keyboardInput.OnKey += key =>
        {
            OnKey?.Invoke(key);
            ForwardKey(key);
        };
        _keyboardInput.OnLog += line => Log(line);

        // A keystroke is only taken away from the focused application
        // when it is actually going to be delivered over the protocol.
        // Swallowing it without a destination would make the user's
        // typing go nowhere.
        _keyboardInput.HasDeliveryTarget = () => _input?.IsInteractive ?? false;

        // The input coordinator: the bridge from the virtual cursor to
        // the projected application.  It owns a worker thread, because
        // every CDP call is a WebSocket round trip and the render
        // thread must never block on one.
        _input = new SpatialInputCoordinator(() => CaptureSize);
        _input.OnLog += line => Log(line);
        _input.Start();

        // The window procedure is static, so it reaches the tracker
        // through this static hook.
        PanelRenderer.CurrentPointerInput = _pointerInput;
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

    /// <summary>
    /// The bridge from the virtual cursor to the projected app.
    /// </summary>
    /// <remarks>
    /// Exposed so a probe can check the coordinate mapping end to end
    /// against the page's own ground truth.
    /// </remarks>
    public SpatialInputCoordinator Input => _input!;

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

            // The pointer tracker needs to know which part of the
            // virtual desktop belongs to the glasses, so it can tell
            // "the pointer is over the panel" from "the pointer is over
            // the XP-Pen".
            if (_pointerInput is not null)
                _pointerInput.DisplayBounds = display.Bounds;

            _cts = new CancellationTokenSource();
            _running = true;
            _renderer.Start(_anchor, display);

            // The low-level mouse hook.  It is installed on its own
            // thread with its own message pump, because the hook's
            // callback is invoked on the installing thread.
            _pointerInput?.StartHook();

            // The keyboard hook, on its own thread for the same reason.
            _keyboardInput?.StartHook();

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

            // The geometry watcher keeps the coordinate mapping
            // correct.  The browser window can be moved and resized
            // while it is projected, and the mapping from the
            // cursor's texture coordinate to the page's CSS pixels
            // changes with it - so it has to be re-read periodically,
            // not once at attach time.
            if (_input is not null && _cts is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _input.WatchGeometryAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // The station stopped.  Nothing to do.
                    }
                    catch (Exception ex)
                    {
                        Log("Geometry watcher stopped: " + ex.Message);
                    }
                });
            }

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
                    AttachInput(wanted);
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
                    _input?.Detach();
                }
            }

            // Pull the newest captured frame and hand it to the
            // renderer.  The SRV is owned by the capture; the renderer
            // only borrows it for the duration of the draw.
            if (_capture.IsActive)
            {
                _capture.TryUpdate();
                _renderer.PanelTexture = _capture.Texture;

                // The differential mapping's gain is expressed in the
                // captured window's pixels, so the tracker needs to
                // know the content's size.
                if (_pointerInput is not null)
                {
                    var (w, h) = _capture.Size;
                    if (w > 0 && h > 0) _pointerInput.ContentSize = (w, h);
                }

                // The panel's SHAPE follows the content's shape, or the
                // content is stretched.  The capture's size is only
                // known once it has produced a frame, so the panel is
                // re-pinned at that point - before that it was pinned
                // with the default 16:9 shape.
                //
                // Only the first frame of a capture can change the
                // aspect, so the re-pin happens once, not every frame.
                var (cw, ch) = _capture.Size;
                if (cw > 0 && ch > 0 && _anchor.SetContentSize(cw, ch))
                {
                    _anchor.AnchorHere();
                    Log($"Panel shape follows the content's aspect: {cw}x{ch}.");
                }
            }

            // Tell the input layer where the cursor is, so a hover
            // reaches the projected app.  Only when it has actually
            // moved.
            ForwardCursorPosition();

            Thread.Sleep(2);
        }
    }

    // -----------------------------------------------------------------
    // Input plumbing
    // -----------------------------------------------------------------

    /// <summary>
    /// Try to attach the input coordinator to the captured window.
    /// </summary>
    /// <remarks>
    /// The attach is best-effort: a window that is not a debuggable
    /// browser is still perfectly projectable, it simply is not
    /// interactive.  Failing here must never stop the capture.
    /// </remarks>
    private void AttachInput(IntPtr hwnd)
    {
        var input = _input;
        if (input is null) return;

        var title = NativeMethods.GetTitleOf(hwnd);
        var className = NativeMethods.GetClassNameOf(hwnd);
        Log($"Attaching input to \"{title}\" ({className}).");

        // The attach is asynchronous - it needs the HTTP endpoint and
        // then a WebSocket - so it runs on a worker rather than
        // blocking the pump thread.
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await input.AttachAsync(hwnd, title).ConfigureAwait(false);
                if (!ok)
                {
                    Log("The projected window is not interactive: no DevTools endpoint "
                        + "matched it. Launch the browser with --remote-debugging-port=9222 "
                        + "to enable direct interaction.");
                }
            }
            catch (Exception ex)
            {
                Log($"Input attach failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Forward the cursor's movement to the input coordinator.
    /// </summary>
    /// <remarks>
    /// Called from the render thread, once per frame, when the cursor
    /// has actually moved.  The coordinator coalesces the positions,
    /// so a fast mouse does not build up a backlog of stale hovers.
    /// </remarks>
    private void ForwardCursorPosition()
    {
        var cursor = _cursor;
        var input = _input;
        if (cursor is null || input is null) return;
        if (!cursor.IsActive) return;
        if (!cursor.IsDirty) return;

        var (u, v) = cursor.Position;
        input.CursorMoved(u, v);
    }

    /// <summary>
    /// Forward a click or wheel tick to the projected application.
    /// </summary>
    private void ForwardPointerEvent(PointerEvent e)
    {
        var input = _input;
        var cursor = _cursor;
        if (input is null || cursor is null) return;
        if (!cursor.IsActive) return;

        var (u, v) = cursor.Position;
        input.Deliver(e, u, v);
    }

    /// <summary>
    /// Forward a keystroke to the projected application.
    /// </summary>
    /// <remarks>
    /// The modifier state is read from the OS at the moment the key
    /// goes down, because the hook's virtual key code for a modifier
    /// is the modifier itself - the state of the OTHER modifiers is
    /// what has to go into the bitmask.
    /// </remarks>
    private void ForwardKey(ResolvedKey key)
    {
        var input = _input;
        if (input is null) return;

        // A key is only delivered when there is something to deliver
        // it TO.  Without this, the queue would fill with keystrokes
        // for a window that is not interactive.
        if (!input.IsInteractive) return;

        input.DeliverKey(
            key,
            PointerInput.IsAltDown(),
            PointerInput.IsControlDown(),
            PointerInput.IsShiftDown());
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
        _input?.Dispose();
        _pointerInput?.Dispose();
        _keyboardInput?.Dispose();
        _capture.Dispose();
        _renderer.Dispose();
        _cts?.Dispose();
    }
}
