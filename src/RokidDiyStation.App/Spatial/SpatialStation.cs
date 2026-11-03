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

    /// <summary>
    /// The anchor this station was given, when it shares one with the
    /// tracking service.
    /// </summary>
    /// <remarks>
    /// The tracking service owns the IMU stream and feeds the shared
    /// anchor itself, so the station must not feed it a second time.
    /// This field records WHO owns the feeding; see
    /// <see cref="FeedImu"/>.
    /// </remarks>
    private readonly Core.Fusion.SpatialAnchor? _sharedAnchor;

    private DisplayGuard? _guard;
    private Thread? _pumpThread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private volatile bool _captureStarted;
    private SpatialState _state = SpatialState.Idle;
    private string _message = "Idle";
    private string? _preferredDisplayPath;

    /// <summary>
    /// The soft barrier that keeps the cursor off the glasses' display.
    /// </summary>
    /// <remarks>
    /// It is created once and installed/uninstalled as the spatial
    /// display opens and closes.  The hook is GLOBAL - it sees every
    /// mouse move in the system - so it must not be left installed
    /// while the spatial display is closed.
    /// </remarks>
    private readonly MouseBarrier _mouseBarrier = new();

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
        _sharedAnchor = sharedAnchor;
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

        // Crossing onto the virtual display does NOT re-pin the panel.
        //
        // It used to.  The reasoning was "the cursor is meaningful over
        // the spatial display, so that is where the panel belongs" -
        // and the effect was that every time the user moved the mouse
        // from one display to another, the panel TELEPORTED to wherever
        // they happened to be looking.  The user's report was exactly
        // that: "when I move from another screen to this one, it seems
        // to recentre itself".
        //
        // The reason it is wrong is that the mouse and the head are
        // completely independent.  The user moves the mouse to the
        // virtual display to click something in it, which says nothing
        // about where they are LOOKING, and nothing about where they
        // want the panel to be.  Worse, the panel's world pose is
        // supposed to be a property of the ROOM: a screen hanging on a
        // wall does not jump to face you every time you walk past it.
        //
        // So the panel is pinned ONCE, deliberately - when the spatial
        // display is opened, or when the user asks for it - and then it
        // stays where it is.  The cursor entering the display only
        // activates the 3D cursor; it does not move the panel.
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
    /// This needs no administrator rights: the attach and the detach
    /// are both plain display-configuration calls.
    /// </remarks>
    /// <returns>True when the display is attached afterwards.</returns>
    public bool OpenSpatialDisplay()
    {
        // The CCD attach is the whole mechanism - the adapter is
        // always attached to the machine, so there is nothing to
        // "publish" first.
        //
        // THE LEGACY ATTACH IS NOT USED AS A FALLBACK.
        //
        // It used to be.  The legacy attach is
        // ChangeDisplaySettingsEx, and an IddCx INDIRECT display does
        // not support the legacy mode-setting path at all: the call
        // answers DISP_CHANGE_FAILED (-1) for every mode and every
        // field combination.  So it could never work here anyway, and
        // keeping it only risked the one-way door.
        if (!VirtualDisplayController.Open())
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

        // The glasses' own display is a projection canvas, not a work
        // surface - the user sees the virtual display in the glasses,
        // not the glasses' screen.  So the cursor is kept off it.
        InstallMouseBarrier();

        return true;
    }

    /// <summary>
    /// Take the virtual display off the desktop.
    /// </summary>
    /// <remarks>
    /// The virtual display is fully REMOVED from the desktop, not
    /// merely detached: the rectangle does not appear in Display
    /// Settings, there is nothing to accidentally drag a window onto,
    /// and nothing can get lost behind the glasses.
    ///
    /// This is the behaviour the user asked for - "when I am not using
    /// the spatial display, disable it" - because a leftover virtual
    /// monitor is a trap: a window dragged onto it goes behind the
    /// glasses and is very hard to get back.
    ///
    /// It is also REVERSIBLE: the adapter stays attached to the
    /// machine, so opening it again brings it straight back.
    /// </remarks>
    public bool CloseSpatialDisplay()
    {
        // The barrier goes first, so the cursor is free while the
        // display is being torn down.
        UninstallMouseBarrier();

        var ok = VirtualDisplayController.Close();
        if (!ok)
        {
            // There is deliberately NO legacy fallback here.
            //
            // The legacy detach takes the monitor off the desktop by
            // setting a zero-width mode, and that is a ONE-WAY DOOR:
            // the adapter's StateFlags drop from 0x1 to 0x0,
            // EnumDisplaySettings fails for that adapter from then on,
            // and every later re-attach attempt answers
            // DISP_CHANGE_FAILED (-1).  The virtual display would then
            // never come back.
            //
            // A leftover virtual monitor is a cosmetic problem.  A
            // virtual monitor that can never be reopened is a broken
            // product, so the display stays.
            Log("The virtual display could not be detached. "
                + "The legacy fallback is not used because it would make the "
                + "display impossible to reopen.");
        }

        Log(ok ? "Spatial display closed." : "The virtual display could not be detached.");
        return ok;
    }

    // -----------------------------------------------------------------
    // The mouse barrier
    // -----------------------------------------------------------------

    /// <summary>
    /// Keep the cursor out of the glasses' display rectangle.
    /// </summary>
    /// <remarks>
    /// The glasses' display is identified by the same fingerprint the
    /// renderer uses, so the barrier and the panel always agree on
    /// which screen is the Rokid Max.
    ///
    /// The barrier is SOFT (see <see cref="MouseBarrier"/>): the
    /// cursor is pushed back but not hard-clipped, because ClipCursor
    /// breaks the XP-Pen Artist's absolute digitiser.
    /// </remarks>
    private void InstallMouseBarrier()
    {
        var glasses = DisplayLocator.Select(_preferredDisplayPath);
        if (glasses is null)
        {
            Log("The mouse barrier was not installed: the glasses' display was not found.");
            return;
        }

        // The barrier is only meaningful when the glasses' display is
        // NOT where the virtual display is.  When they overlap, the
        // user IS working on that screen and blocking it would be
        // wrong.
        var spatial = FindVirtualDisplay();
        if (spatial is not null && spatial.Bounds.IntersectsWith(glasses.Bounds))
        {
            Log("The mouse barrier was not installed: the glasses' display overlaps the spatial display.");
            return;
        }

        var bounds = glasses.Bounds;
        var forbidden = new NativeMethods.RECT
        {
            left = bounds.Left,
            top = bounds.Top,
            right = bounds.Right,
            bottom = bounds.Bottom,
        };

        // The glasses are to the RIGHT of the working displays in the
        // usual layout (the laptop at 0, the tablet at 1920, the
        // glasses at 3840), so the cursor is pushed back to the left
        // edge.  When the glasses are on the other side, it is pushed
        // to the right.
        var forbiddenIsToTheRight = glasses.Bounds.Left >= 0;

        if (_mouseBarrier.Install(forbidden, forbiddenIsToTheRight))
            Log($"The cursor is kept off the Rokid Max's display ({bounds.X},{bounds.Y}).");
        else
            Log("The mouse barrier could not be installed.");
    }

    /// <summary>Let the cursor go everywhere again.</summary>
    private void UninstallMouseBarrier()
    {
        if (_mouseBarrier.IsActive)
        {
            _mouseBarrier.Uninstall();
            Log("The cursor can go back to the Rokid Max's display.");
        }
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
            //
            // NOTE: the panel is NOT re-pinned here.  It used to be,
            // which meant that changing the virtual display's
            // resolution teleported the panel to wherever the user
            // happened to be looking.  SetContentSize now resizes the
            // panel in place, and this call only reports the change.
            var (cw, ch) = CaptureSize;
            if (cw > 0 && ch > 0 && _anchor.SetContentSize(cw, ch))
            {
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
    /// <remarks>
    /// This is a NO-OP when the station shares its anchor with the
    /// tracking service, which it always does in the product.
    ///
    /// It used to push every sample into the anchor a SECOND time:
    /// the tracking service already called <c>anchor.Update(sample)</c>
    /// on the very same anchor object, and then handed the same sample
    /// to the station, which called <c>_anchor.Update(sample)</c> again.
    /// The anchor's fusion filter therefore saw each sample twice, with
    /// identical timestamps.
    ///
    /// That is not harmless.  The Mahony filter computes its timestep
    /// as the difference between this sample's timestamp and the
    /// previous one; with the sample repeated, that difference is
    /// exactly ZERO, and the filter REJECTS the sample (see
    /// MahonyFusion.Update: "if (dtNs <= 0) return FusionResult.Rejected").
    /// So every second sample was silently thrown away, the gyro bias
    /// estimate was fed every sample but integrated at half rate, and
    /// the position integrator saw every other sample's worth of
    /// acceleration.
    ///
    /// The visible symptom was a slow, unexplained wander of the
    /// anchored panel - which users read as "the panel drifts and
    /// recentres itself".
    /// </remarks>
    public void FeedImu(in RokidDiyStation.Core.Protocol.ImuSample sample)
    {
        // Only feed the anchor ourselves when we own it.  When the
        // anchor is SHARED with the tracking service, the tracker is
        // already feeding it and we must not feed it again.
        if (_sharedAnchor is not null && ReferenceEquals(_anchor.Anchor, _sharedAnchor))
            return;

        _anchor.Update(sample);
    }

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

    /// <summary>
    /// Set the size of the virtual 3D cursor.
    /// </summary>
    /// <remarks>
    /// Applied live: the user can see the arrow grow or shrink while
    /// the app is running, which is the only practical way to choose a
    /// size that looks right on their own face.
    /// </remarks>
    /// <param name="scale">The scale factor; clamped into range.</param>
    public void ApplyCursorScale(float scale) => _renderer.CursorScale = scale;

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
