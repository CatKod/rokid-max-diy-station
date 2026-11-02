// SPDX-License-Identifier: MIT
//
// The bridge between the virtual 3D cursor and the projected app.
//
// This is the piece that makes the product's promise true: the user
// moves a PHYSICAL mouse, a 3D cursor appears on the panel in the
// glasses, and clicking it lands on the app that is being projected.
//
// Why the click cannot go through Win32
// ------------------------------------
// The panel window is WS_EX_TOPMOST, opaque, and covers the entire
// Rokid display.  Windows routes mouse events by SPATIAL HIT-TESTING
// at the pointer's coordinates, walking the Z-order from the top - not
// by focus.  So a click at the panel's coordinates lands on our panel
// and can never reach the browser behind it.  PostMessage does not
// help either: Chromium checks the real GetKeyState() and
// GetCursorPos() and discards synthetic events.
//
// For a browser the answer is the Chrome DevTools Protocol:
// Input.dispatchMouseEvent builds a blink::WebMouseEvent and forwards
// it straight to the renderer over Mojo IPC.  The browser does not
// need to be foreground, does not need focus, and the desktop cursor
// never moves.  That is exactly the property this product needs.
//
// The chain, end to end
// ---------------------
//
//   physical mouse
//     -> PointerInput (poll GetCursorPos, differential mapping)
//     -> VirtualCursor (u, v) in panel texture coordinates
//     -> this coordinator
//     -> BrowserInputRouter (u, v -> CSS pixels)
//     -> CDP Input.dispatchMouseEvent
//     -> the browser's renderer
//
// The three coordinate spaces
// ---------------------------
// The CURSOR lives in the captured image's coordinates, because that is
// what the panel shows.  The BROWSER lives in CSS pixels, because that
// is what the page's event handlers see.  The mapping between them is
// the browser's own geometry: the window's frame rect (what WGC
// captures), the window's client rect (where the page starts), and the
// page's own viewport size.
//
// The click DELIVERY point is the panel's own (u, v).  It is the only
// place where "where the user is pointing in space" and "where the
// cursor is drawn" can disagree, and keeping one number for both is
// what makes the cursor and the click agree.
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RokidDiyStation.App.Spatial;

namespace RokidDiyStation.App.Input;

/// <summary>
/// Routes the virtual cursor's interactions to the projected application.
/// </summary>
/// <remarks>
/// The coordinator owns a single worker thread.  Every CDP call is a
/// WebSocket round trip, and the render thread must never block on
/// one - a stalled WebSocket would stall the whole panel.
///
/// Hover events are COALESCED rather than queued: the mouse moves far
/// faster than the panel renders, and a queue of pending hover
/// positions would only deliver stale positions late.  Click and
/// wheel events are QUEUED, because dropping a click is not an option.
/// </remarks>
public sealed class SpatialInputCoordinator : IDisposable
{
    private readonly Func<(int Width, int Height)> _captureSizeProvider;

    private readonly BrowserInputRouter _router = new();
    private readonly CancellationTokenSource _cts = new();

    // Clicks and wheel ticks: every one of them must be delivered.
    private readonly ConcurrentQueue<PendingAction> _actions = new();

    // Hovers: only the newest one matters.
    private readonly object _hoverLock = new();
    private float _hoverU;
    private float _hoverV;
    private bool _hoverPending;

    private readonly SemaphoreSlim _signal = new(0);
    private Thread? _worker;
    private volatile bool _running;

    private IntPtr _window;
    private string _expectedTitle = "";
    private string _expectedUrl = "";
    private TaskCompletionSource<bool>? _attachTcs;

    /// <summary>Raised for log lines worth showing to the user.</summary>
    public Action<string>? OnLog;

    /// <summary>
    /// Create the coordinator.
    /// </summary>
    /// <param name="captureSizeProvider">
    /// Supplies the captured window's size, which is the coordinate
    /// space the cursor lives in.
    /// </param>
    public SpatialInputCoordinator(Func<(int Width, int Height)> captureSizeProvider)
    {
        _captureSizeProvider = captureSizeProvider;
    }

    /// <summary>True while a browser is connected and reachable.</summary>
    public bool IsInteractive => _router.IsConnected;

    /// <summary>
    /// The DevTools port to look for the browser on.
    /// </summary>
    /// <remarks>
    /// Chrome's default is 9222, and that is what the browser we
    /// launch ourselves uses.  The port is a property rather than a
    /// constant so that a test - or a user running a second browser
    /// for a different purpose - can point the coordinator somewhere
    /// else.
    /// </remarks>
    public int Port { get; set; } = CdpDiscovery.DefaultPort;

    /// <summary>
    /// Every DevTools port worth searching.
    /// </summary>
    /// <remarks>
    /// The browser we launch ourselves listens on the default port,
    /// and the Electron applications we launch listen on their own
    /// dedicated port - a launched application must not collide with
    /// the browser the user already has open.
    ///
    /// So the attach searches BOTH: the projected window is whichever
    /// one it is, and the search finds it either way.
    /// </remarks>
    public IReadOnlyList<int> Ports { get; set; } = new[] { CdpDiscovery.DefaultPort, AppLauncher.Port };

    /// <summary>The window we are driving, or zero.</summary>
    public IntPtr TargetWindow => _window;

    /// <summary>
    /// The viewport's rectangle inside the captured image.
    /// </summary>
    /// <remarks>
    /// Exposed so a probe can check the coordinate mapping against a
    /// known ground truth: the page's own layout gives a target a
    /// position in CSS pixels, and this says where that lands in the
    /// captured image.
    /// </remarks>
    public ViewportRect Viewport => _router.Viewport;

    /// <summary>
    /// Raised for every key event that goes over the wire.
    /// </summary>
    /// <remarks>
    /// A keystroke that arrives twice is invisible from the outside -
    /// the application just shows two characters - so seeing exactly
    /// what was dispatched is what makes the bug findable.
    /// </remarks>
    public event Action<string>? OnKeyDispatched
    {
        add => _router.OnKeyDispatched += value;
        remove => _router.OnKeyDispatched -= value;
    }

    /// <summary>
    /// Raised for every mouse event that goes over the wire.
    /// </summary>
    /// <remarks>
    /// The same diagnostic purpose as
    /// <see cref="OnKeyDispatched"/>.  A click that never arrives looks
    /// exactly like a click that was never sent, and only the trace
    /// tells the two apart.
    /// </remarks>
    public event Action<string>? OnMouseDispatched
    {
        add => _router.OnMouseDispatched += value;
        remove => _router.OnMouseDispatched -= value;
    }

    /// <summary>
    /// Evaluate an expression in the projected application.
    /// </summary>
    /// <remarks>
    /// This runs over the SAME connection the input goes over, so it
    /// has to be queued like the input is: a second concurrent client
    /// on the same target would either be refused or would steal the
    /// replies belonging to the mouse commands.
    ///
    /// It exists for the diagnostics - "what does the application think
    /// its own layout is?" - which is the only way to get GROUND
    /// TRUTH about where a target is, rather than guessing.  A probe
    /// uses it to ask the application where its own search box is, and
    /// then aims the virtual cursor at exactly that.
    /// </remarks>
    /// <param name="expression">The JavaScript to evaluate.</param>
    /// <returns>The result, or null when it could not be evaluated.</returns>
    public Task<string?> EvaluateAsync(string expression)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _actions.Enqueue(new PendingAction(async ct =>
        {
            try
            {
                tcs.TrySetResult(await _router.EvaluateAsync(expression, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(null);
                Log("Evaluate failed: " + ex.Message);
            }
        }));
        _signal.Release();

        return tcs.Task;
    }

    /// <summary>
    /// Map a texture coordinate to the page's CSS pixels.
    /// </summary>
    /// <param name="u">Horizontal texture coordinate, 0 at the viewer's left.</param>
    /// <param name="v">Vertical texture coordinate, 0 at the viewer's top.</param>
    /// <param name="x">The CSS x coordinate.</param>
    /// <param name="y">The CSS y coordinate.</param>
    /// <returns>True when the mapping is available.</returns>
    /// <remarks>
    /// The device pixel ratio comes from the ROUTER, not from the
    /// geometry.  The router is the component that read the ratio from
    /// the page and built the viewport with it, so asking it is the
    /// only way to be sure the two sides of the conversion agree.
    /// Reading it from the geometry instead silently used 1.0 when
    /// the geometry was unknown, which inflated every coordinate by the
    /// ratio on a scaled display.
    /// </remarks>
    public bool ToCss(float u, float v, out double x, out double y)
        => _router.Viewport.ToCss(u, v, _router.DevicePixelRatio, out x, out y);

    /// <summary>
    /// Map a CSS pixel back into a texture coordinate.
    /// </summary>
    /// <param name="cssX">The CSS x coordinate.</param>
    /// <param name="cssY">The CSS y coordinate.</param>
    /// <param name="u">Horizontal texture coordinate, 0 at the viewer's left.</param>
    /// <param name="v">Vertical texture coordinate, 0 at the viewer's top.</param>
    /// <returns>True when the mapping is available.</returns>
    public bool ToContent(double cssX, double cssY, out float u, out float v)
        => _router.Viewport.ToContent(cssX, cssY, _router.DevicePixelRatio, out u, out v);

    // -----------------------------------------------------------------
    // Attaching
    // -----------------------------------------------------------------

    /// <summary>
    /// Attach to the browser that owns the given window.
    /// </summary>
    /// <remarks>
    /// The CDP target list does not say which OS window a page belongs
    /// to, so the two are matched by TITLE: in --app mode the window's
    /// title is the page's title, and the DevTools endpoint reports the
    /// same string.  If the title cannot be matched, the URL is tried
    /// as a fallback.
    /// </remarks>
    public async Task<bool> AttachAsync(
        IntPtr hwnd, string expectedTitle = "", string expectedUrl = "", CancellationToken token = default)
    {
        // A zero handle is allowed: the page can be identified by its
        // title alone, and the viewport mapping then falls back to the
        // page's own size.
        if (hwnd == IntPtr.Zero && expectedTitle.Length == 0 && expectedUrl.Length == 0)
            return false;

        _window = hwnd;
        _expectedTitle = expectedTitle;
        _expectedUrl = expectedUrl;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _attachTcs = tcs;

        _actions.Enqueue(new PendingAction(async ct =>
        {
            var ok = await DoAttachAsync(ct).ConfigureAwait(false);
            tcs.TrySetResult(ok);
        }));

        _signal.Release();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token);
        var finished = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, linked.Token))
            .ConfigureAwait(false);
        if (finished != tcs.Task) return false;
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<bool> DoAttachAsync(CancellationToken token)
    {
        // A zero window handle means "there is no window to inspect" -
        // the caller only knows the page by its title.  That is the
        // case in the tests, and it is harmless in production, where
        // the handle is always real.
        var title = _window == IntPtr.Zero ? "" : NativeMethods.GetTitleOf(_window);
        if (title.Length == 0) title = _expectedTitle;

        // The page may live on any of the ports we know about: the
        // default one for the browser, the dedicated one for the
        // applications we launch.
        //
        // The search is SCORED, not "first port wins": the user's own
        // browser may be on the default port with a single tab open,
        // and that tab must never be captured by mistake just because
        // its port came first.
        var candidates = new List<(CdpTarget Target, int Port, int Score)>();
        var searched = new List<int>();

        // The port that was set explicitly is always searched - it is
        // the one the caller knows about.  The known ports are tried
        // after it.
        var ports = new List<int> { Port };
        foreach (var port in Ports)
        {
            if (!ports.Contains(port)) ports.Add(port);
        }

        foreach (var port in ports)
        {
            if (searched.Contains(port)) continue;
            searched.Add(port);

            var pages = await CdpDiscovery.ListPagesAsync(port, token).ConfigureAwait(false);
            if (pages.Count == 0) continue;

            foreach (var page in pages)
            {
                var score = ScoreTarget(page, title, _expectedUrl);

                // A port with exactly ONE real page is that page's
                // endpoint - the Electron application we launched has
                // one workbench document.  It scores below an exact
                // title match, so the user's own single tab in their
                // browser can never outrank a real match.
                if (score == 0 && IsSoleCandidate(pages, page))
                    score = 50;

                if (score > 0) candidates.Add((page, port, score));
            }
        }

        if (candidates.Count == 0)
        {
            Log("No debuggable page matched the projected window on any known port ("
                + string.Join(", ", searched) + "). Is the projected window an app started "
                + "with --remote-debugging-port?");
            return false;
        }

        // The best match wins; a tie is broken by the exact title, and
        // then by the dedicated launch port - an application we started
        // is more likely to be the intended target than a tab in the
        // user's own browser.
        var best = candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Port == AppLauncher.Port)
            .First();

        var target = best.Target;
        Port = best.Port;

        if (!await _router.ConnectAsync(target.WebSocketDebuggerUrl, token).ConfigureAwait(false))
        {
            Log("Could not open the DevTools WebSocket for \"" + target.Title + "\".");
            return false;
        }

        if (!await RefreshGeometryAsync(token).ConfigureAwait(false))
            Log("Connected to \"" + target.Title + "\", but the page's metrics are not readable yet.");

        await _router.EnsureActiveAsync(token).ConfigureAwait(false);

        Log("Interactive: driving \"" + target.Title + "\" over the DevTools protocol.");
        return true;
    }

    /// <summary>
    /// Find the CDP page that belongs to the projected window.
    /// </summary>
    /// <remarks>
    /// The title is the strongest signal - in --app mode the window's
    /// title IS the page's title - but a page that has not finished
    /// loading has an EMPTY title, and then the title is useless.
    /// The URL is the fallback: the projected window was opened from
    /// a URL, and the CDP target reports the same one.
    /// </remarks>
    /// <summary>
    /// How well a page matches the projected window.
    /// </summary>
    /// <remarks>
    /// The score is a RANK, not a match/no-match flag, because the
    /// coordinator searches several ports at once and has to pick
    /// between candidates.  Zero means "not a candidate at all".
    ///
    /// The ranking matters most in the one dangerous case: the user's
    /// own browser is open on the default port with one tab, and an
    /// application we launched is on the dedicated port.  An exact
    /// title match has to beat the "there is only one page here"
    /// fallback, or the user's tab gets hijacked.
    /// </remarks>
    private static int ScoreTarget(CdpTarget page, string title, string url)
    {
        // Internal pages are never a target.
        if (page.Url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase)) return 0;
        if (page.Url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase)) return 0;
        if (page.Url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) return 0;
        if (page.Type != "page") return 0;

        if (title.Length > 0)
        {
            if (string.Equals(page.Title, title, StringComparison.OrdinalIgnoreCase))
                return 100;

            // A substring match, because an application's window title
            // and its page title are rarely identical: VS Code's
            // window says "notes.md - Visual Studio Code" while the
            // page it renders says "Visual Studio Code".
            if (page.Title.Length > 0
                && (page.Title.Contains(title, StringComparison.OrdinalIgnoreCase)
                    || title.Contains(page.Title, StringComparison.OrdinalIgnoreCase)))
                return 70;
        }

        if (url.Length > 0
            && string.Equals(page.Url, url, StringComparison.OrdinalIgnoreCase))
            return 90;

        // The last resort: a port that has exactly ONE candidate page
        // IS that page's endpoint.  This is the case for an Electron
        // application we launched - it has one workbench document.
        return 0;
    }

    /// <summary>
    /// Whether a page is the only real candidate on its port.
    /// </summary>
    /// <remarks>
    /// A port with exactly one candidate page IS that page's
    /// endpoint.  This is the case for an Electron application we
    /// launched - it has one workbench document.
    ///
    /// It is deliberately NOT "the first page on the port".  A browser
    /// the user already has open can have a dozen tabs, and picking
    /// the first of them would drive the WRONG window - which is
    /// worse than driving nothing at all.
    /// </remarks>
    private static bool IsSoleCandidate(
        IReadOnlyList<CdpTarget> pages, CdpTarget candidate)
    {
        CdpTarget? sole = null;
        var count = 0;

        foreach (var page in pages)
        {
            if (page.Type != "page") continue;
            if (page.Url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase)) continue;
            if (page.Url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase)) continue;
            if (page.Url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(page.WebSocketDebuggerUrl)) continue;
            sole = page;
            count++;
        }

        return count == 1 && ReferenceEquals(sole, candidate);
    }

    // -----------------------------------------------------------------
    // Input
    // -----------------------------------------------------------------

    /// <summary>
    /// Tell the coordinator the cursor moved.
    /// </summary>
    public void CursorMoved(float u, float v)
    {
        lock (_hoverLock)
        {
            _hoverU = u;
            _hoverV = v;
            _hoverPending = true;
        }
        _signal.Release();
    }

    /// <summary>
    /// Deliver a click or a wheel tick.
    /// </summary>
    public void Deliver(PointerEvent e, float u, float v)
    {
        _actions.Enqueue(new PendingAction(async ct =>
        {
            var modifiers = CdpClient.ModifierMask(e.Alt, e.Control, false, e.Shift);

            if (e.WheelDelta != 0)
            {
                // Win32 reports 120 per detent; CDP expects pixels, and
                // Chromium's own convention is a third of a line (about
                // 100 px) per detent.
                var pixels = e.WheelDelta / 120.0 * 100.0;
                await _router.ScrollAsync((int)Math.Round(pixels), modifiers, ct).ConfigureAwait(false);
                return;
            }

            await _router.ButtonAsync(e.Button, e.IsDown, u, v, modifiers, 1, ct).ConfigureAwait(false);
        }));
        _signal.Release();
    }

    /// <summary>
    /// Deliver a keystroke to the projected application.
    /// </summary>
    /// <remarks>
    /// Keystrokes are QUEUED, not coalesced.  The order of a key
    /// sequence is the message: "a", "b", "c" has to arrive as
    /// a-b-c, and a press has to arrive before its release.  The
    /// worker thread is the only thing that talks to the browser, so
    /// queueing here is what preserves the order.
    /// </remarks>
    /// <param name="key">The keystroke, already resolved.</param>
    /// <param name="alt">Whether Alt is held.</param>
    /// <param name="control">Whether Ctrl is held.</param>
    /// <param name="shift">Whether Shift is held.</param>
    public void DeliverKey(ResolvedKey key, bool alt, bool control, bool shift)
    {
        _actions.Enqueue(new PendingAction(async ct =>
        {
            var modifiers = CdpClient.ModifierMask(alt, control, false, shift);
            await _router.KeyAsync(key, modifiers, ct).ConfigureAwait(false);
        }));
        _signal.Release();
    }

    /// <summary>
    /// Detach from the browser and stop the worker.
    /// </summary>
    public void Detach()
    {
        _actions.Clear();
        _actions.Enqueue(new PendingAction(_ =>
        {
            _router.Disconnect();
            return Task.CompletedTask;
        }));
        _signal.Release();
    }

    // -----------------------------------------------------------------
    // The worker
    // -----------------------------------------------------------------

    /// <summary>Start the worker thread.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SpatialInput",
        };
        _worker.Start();
    }

    private async void WorkerLoop()
    {
        var token = _cts.Token;

        while (!token.IsCancellationRequested && _running)
        {
            try
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Drain the queue.  Bounded, so a flood of wheel ticks
            // cannot starve the hover.
            for (var i = 0; i < 64 && !token.IsCancellationRequested; i++)
            {
                if (!_actions.TryDequeue(out var action)) break;
                try
                {
                    await action.Run(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log("Input dispatch failed: " + ex.Message);
                }
            }

            // Then the newest hover position, if any.
            float u;
            float v;
            lock (_hoverLock)
            {
                if (!_hoverPending) continue;
                _hoverPending = false;
                u = _hoverU;
                v = _hoverV;
            }

            try
            {
                await _router.HoverAsync(u, v, 0, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("Hover dispatch failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Re-read the window's geometry and the page's metrics.
    /// </summary>
    /// <remarks>
    /// This has to be repeated, not done once: the user can resize the
    /// browser window, and the mapping from the cursor's texture
    /// coordinates to the page's CSS pixels changes with it.
    ///
    /// The FRAME rect is the question with two answers.  DWM gives
    /// modern windows an invisible resize border, so GetWindowRect is
    /// about 8 pixels wider on each side than the visible frame, and
    /// the visible frame is what WGC captures.  We therefore try both
    /// and take whichever matches the capture's size.
    /// </remarks>
    private async Task<bool> RefreshGeometryAsync(CancellationToken token)
    {
        var (captureWidth, captureHeight) = _captureSizeProvider();
        if (captureWidth <= 0 || captureHeight <= 0) return false;

        // Without a window handle there is no geometry to read, so the
        // page's own viewport is used instead.  That is correct for a
        // browser in --app mode, where the client area IS the
        // viewport.
        if (_window == IntPtr.Zero)
            return await _router.RefreshViewportAsync(token).ConfigureAwait(false);

        if (!NativeMethods.TryGetExtendedFrameBounds(_window, out var frame))
        {
            if (!NativeMethods.GetWindowRect(_window, out frame)) return false;
        }

        if (!NativeMethods.GetWindowRect(_window, out var window)) window = frame;

        // The client's top-left in virtual-desktop coordinates.
        var origin = new NativeMethods.POINT { x = 0, y = 0 };
        if (!NativeMethods.ClientToScreen(_window, ref origin)) return false;
        if (!NativeMethods.GetClientRect(_window, out var client)) return false;

        _router.Geometry = new BrowserWindowGeometry
        {
            Handle = _window,
            CaptureWidth = captureWidth,
            CaptureHeight = captureHeight,
            WindowRect = window,
            FrameRect = frame,
            ClientRect = new NativeMethods.RECT
            {
                left = origin.x,
                top = origin.y,
                right = origin.x + client.Width,
                bottom = origin.y + client.Height,
            },
        };

        return await _router.RefreshMetricsAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Periodically refresh the geometry while attached.
    /// </summary>
    public async Task WatchGeometryAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_window == IntPtr.Zero) continue;
            try
            {
                await RefreshGeometryAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log("Geometry refresh failed: " + ex.Message);
            }
        }
    }

    private void Log(string line)
    {
        try { OnLog?.Invoke(line); } catch { }
    }

    private sealed record PendingAction(Func<CancellationToken, Task> Run);

    public void Dispose()
    {
        _running = false;
        _cts.Cancel();
        _signal.Release();
        try { _worker?.Join(TimeSpan.FromSeconds(2)); } catch { }
        _router.Dispose();
        _cts.Dispose();
        _signal.Dispose();
    }
}
