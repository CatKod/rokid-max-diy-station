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
    private readonly Action<string> _log;

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

    /// <summary>The window we are driving, or zero.</summary>
    public IntPtr TargetWindow => _window;

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

        var pages = await CdpDiscovery.ListPagesAsync(Port, token).ConfigureAwait(false);
        if (pages.Count == 0)
        {
            Log("No debuggable page found on port " + Port
                + ". Is the projected window a browser launched with --remote-debugging-port?");
            return false;
        }

        var target = MatchTarget(pages, title, _expectedUrl);
        if (target is null)
        {
            Log("No CDP page matches the projected window's title \"" + title + "\".");
            return false;
        }

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
    private static CdpTarget? MatchTarget(
        IReadOnlyList<CdpTarget> pages, string title, string url = "")
    {
        if (title.Length > 0)
        {
            foreach (var page in pages)
            {
                if (string.Equals(page.Title, title, StringComparison.OrdinalIgnoreCase))
                    return page;
            }
        }

        if (url.Length > 0)
        {
            foreach (var page in pages)
            {
                if (string.Equals(page.Url, url, StringComparison.OrdinalIgnoreCase))
                    return page;
            }
        }

        // Fall back to the first page whose URL is not a browser
        // internal page.  This is the right answer for the dedicated
        // --app instance, which has exactly one page.
        foreach (var page in pages)
        {
            if (page.Url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase)) continue;
            if (page.Url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase)) continue;
            return page;
        }

        return pages.Count > 0 ? pages[0] : null;
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
        try { _log?.Invoke(line); } catch { }
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
