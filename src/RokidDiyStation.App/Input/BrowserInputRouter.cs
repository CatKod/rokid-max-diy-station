// SPDX-License-Identifier: MIT
//
// Driving a browser that we projected onto the panel.
//
// The whole point of the virtual cursor is that the user can ACT on
// what they see in the glasses - click a link, press a button, scroll
// a page - without ever touching the desk.  This class is the path
// from "the user clicked at panel coordinate (u, v)" to "the browser
// received a mouse event at CSS pixel (x, y)".
//
// The chain
// ---------
//
//   pointer delta  ->  VirtualCursor (u, v)  ->  CSS (x, y)  ->  CDP
//
// The middle step is the interesting one.  Three coordinate spaces
// have to be lined up:
//
//   * The CAPTURE is the whole browser window, frame included.  WGC
//     captures the window's rectangle, so it includes the invisible
//     resize border and any caption bar.
//
//   * The VIEWPORT is the page's content area.  In --app mode there
//     is no tab strip and no address bar, so the viewport is the
//     window's client area minus the scrollbar.
//
//   * CSS pixels are the page's own coordinate space, which is the
//     viewport scaled by 1/devicePixelRatio.
//
// The bridge between them is the browser itself: `window.screenX`,
// `window.outerWidth` and friends say where the window is on the
// desktop and how big it is, and the Win32 client rect says where
// the client area sits inside the window.  With both, the viewport's
// rectangle inside the captured image is exact - no guessing, no
// "assume there is a 30-pixel title bar".
//
// Why CDP and not SendInput
// -------------------------
// The panel window is WS_EX_TOPMOST and opaque and covers the whole
// glasses' display.  Win32 routes mouse events by SPATIAL HIT-TESTING
// at the cursor's coordinates, so a SendInput click lands on our
// panel and can never reach the browser behind it.  CDP bypasses the
// Win32 input queue entirely: the event goes straight to Blink over
// Mojo IPC, and the browser does not need to be foreground.
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using RokidDiyStation.App.Spatial;

namespace RokidDiyStation.App.Input;

/// <summary>
/// Where the browser's viewport sits inside the captured image.
/// </summary>
/// <param name="Left">The viewport's left edge, in captured pixels.</param>
/// <param name="Top">The viewport's top edge, in captured pixels.</param>
/// <param name="Width">The viewport's width, in captured pixels.</param>
/// <param name="Height">The viewport's height, in captured pixels.</param>
public readonly record struct ViewportRect(int Left, int Top, int Width, int Height)
{
    /// <summary>
    /// Map a texture coordinate into CSS pixels.
    /// </summary>
    /// <param name="u">Horizontal texture coordinate, 0 at the viewer's left.</param>
    /// <param name="v">Vertical texture coordinate, 0 at the viewer's top.</param>
    /// <param name="devicePixelRatio">The page's device pixel ratio.</param>
    /// <param name="x">The CSS x coordinate.</param>
    /// <param name="y">The CSS y coordinate.</param>
    public bool ToCss(float u, float v, double devicePixelRatio, out double x, out double y)
    {
        if (Width <= 0 || Height <= 0)
        {
            x = 0;
            y = 0;
            return false;
        }

        var px = Left + u * Width;
        var py = Top + v * Height;
        x = px / devicePixelRatio;
        y = py / devicePixelRatio;
        return true;
    }

    /// <summary>
    /// Map a CSS pixel back into a texture coordinate.
    /// </summary>
    /// <param name="cssX">The CSS x coordinate.</param>
    /// <param name="cssY">The CSS y coordinate.</param>
    /// <param name="devicePixelRatio">The page's device pixel ratio.</param>
    /// <param name="u">Horizontal texture coordinate, 0 at the viewer's left.</param>
    /// <param name="v">Vertical texture coordinate, 0 at the viewer's top.</param>
    /// <returns>True when the mapping is available.</returns>
    /// <remarks>
    /// This is the INVERSE of <see cref="ToCss"/>, and it is what a
    /// probe needs to answer the question "if I want the cursor to be
    /// over this particular thing on the page, where does the mouse
    /// have to point?".
    ///
    /// The round trip ToCss(ToContent(x)) == x is what makes the
    /// virtual cursor trustworthy: the click is delivered at the CSS
    /// coordinate, and the arrow is drawn at the texture coordinate,
    /// so the two must be inverses of each other or the cursor lies.
    /// </remarks>
    public bool ToContent(double cssX, double cssY, double devicePixelRatio, out float u, out float v)
    {
        if (Width <= 0 || Height <= 0)
        {
            u = 0;
            v = 0;
            return false;
        }

        var px = cssX * devicePixelRatio - Left;
        var py = cssY * devicePixelRatio - Top;
        u = (float)(px / Width);
        v = (float)(py / Height);
        return true;
    }
}

/// <summary>
/// The browser-window geometry the viewport mapping needs.
/// </summary>
public sealed class BrowserWindowGeometry
{
    /// <summary>The browser window's handle, for the Win32 client rect.</summary>
    public IntPtr Handle { get; init; }

    /// <summary>The captured image's size, in pixels.</summary>
    public int CaptureWidth { get; init; }

    /// <summary>The captured image's height, in pixels.</summary>
    public int CaptureHeight { get; init; }

    /// <summary>The page's device pixel ratio.</summary>
    /// <remarks>
    /// Settable, because the ratio is read from the page and can change
    /// when the browser is dragged between displays with different
    /// scaling.
    /// </remarks>
    public double DevicePixelRatio { get; set; } = 1.0;

    /// <summary>
    /// The browser window's rectangle in virtual-desktop pixels.
    /// </summary>
    /// <remarks>
    /// This is GetWindowRect, which INCLUDES the invisible resize
    /// border DWM gives modern windows.
    /// </remarks>
    public NativeMethods.RECT WindowRect { get; set; }

    /// <summary>
    /// The window's visible frame, in virtual-desktop pixels.
    /// </summary>
    /// <remarks>
    /// DWMWA_EXTENDED_FRAME_BOUNDS - the rectangle the user can see.
    /// On Windows 11 this is a few pixels smaller than
    /// <see cref="WindowRect"/> on each side.
    /// </remarks>
    public NativeMethods.RECT FrameRect { get; set; }

    /// <summary>
    /// The browser window's client area in virtual-desktop pixels.
    /// </summary>
    public NativeMethods.RECT ClientRect { get; set; }

    /// <summary>
    /// Compute the viewport's rectangle inside the captured image.
    /// </summary>
    /// <remarks>
    /// The capture covers the window's whole rectangle, so the client
    /// area's offset within the window - measured against the WINDOW
    /// rect, in the same virtual-desktop coordinates - is also its
    /// offset within the capture.
    ///
    /// The width and height come from the page itself
    /// (<c>window.innerWidth</c>), not from the client rect: the
    /// client rect includes the scrollbar, and the scrollbar is not
    /// part of the page.
    /// </remarks>
    public ViewportRect ComputeViewport(int innerWidthCss, int innerHeightCss)
    {
        var captureWidth = Math.Max(1, CaptureWidth);
        var captureHeight = Math.Max(1, CaptureHeight);

        // Which rectangle does the capture correspond to?  DWM's
        // invisible resize border means the two candidates differ by a
        // few pixels, and the only reliable answer is the one whose
        // size matches the captured image.
        var frame = FrameRect.Width > 0 && FrameRect.Height > 0 ? FrameRect : WindowRect;
        var window = WindowRect.Width > 0 && WindowRect.Height > 0 ? WindowRect : frame;

        var chosen = Math.Abs(frame.Width - captureWidth) <= Math.Abs(window.Width - captureWidth)
            ? frame
            : window;

        // The scale between the window's own coordinates and the
        // captured image's pixels.  They are normally identical (the
        // capture is at the window's own DPI), but a window moved
        // between displays with different scaling would differ.
        var windowWidth = Math.Max(1, chosen.Width);
        var windowHeight = Math.Max(1, chosen.Height);
        var scaleX = captureWidth / (float)windowWidth;
        var scaleY = captureHeight / (float)windowHeight;

        var left = (ClientRect.left - chosen.left) * scaleX;
        var top = (ClientRect.top - chosen.top) * scaleY;

        // The page's CSS viewport, converted into captured pixels.
        var width = (float)(innerWidthCss * DevicePixelRatio) * scaleX;
        var height = (float)(innerHeightCss * DevicePixelRatio) * scaleY;

        return new ViewportRect(
            (int)MathF.Round(left),
            (int)MathF.Round(top),
            (int)MathF.Round(width),
            (int)MathF.Round(height));
    }
}

/// <summary>
/// Forwards the virtual cursor's interactions to a Chromium browser
/// over the DevTools protocol.
/// </summary>
public sealed class BrowserInputRouter : IDisposable
{
    private readonly CdpClient _client = new();
    private readonly SemaphoreSlim _dispatchLock = new(1, 1);

    private double _lastX;
    private double _lastY;
    private bool _havePosition;
    private bool _leftDown;
    private bool _rightDown;
    private bool _middleDown;

    /// <summary>
    /// The page's device pixel ratio, as read from the page itself.
    /// </summary>
    /// <remarks>
    /// The router OWNS this value, and every conversion uses it.  That
    /// is not tidiness - it is correctness.
    ///
    /// The viewport rectangle is measured in DEVICE pixels (it has to
    /// be, because it describes a region of the captured image), while
    /// CDP's mouse coordinates are CSS pixels.  So the conversion
    /// divides by the device pixel ratio.
    ///
    /// If the ratio used to BUILD the viewport and the ratio used to
    /// CONVERT it could differ, every click would be off by that
    /// factor.  They used to: the viewport was built from the ratio
    /// read from the page, but the conversion read the ratio from
    /// <see cref="Geometry"/>, which is null on the path where the
    /// window's geometry is unknown - so the conversion silently used
    /// 1.0 while the viewport had been built with 1.25.  On a
    /// 125%-scaled display that put every click 25% of the screen away
    /// from where the user aimed.
    ///
    /// One owner, one value, no possibility of disagreement.
    /// </remarks>
    private double _devicePixelRatio = 1.0;

    /// <summary>The page's device pixel ratio, as the router last read it.</summary>
    public double DevicePixelRatio => _devicePixelRatio;

    /// <summary>The geometry the viewport mapping needs.</summary>
    public BrowserWindowGeometry? Geometry { get; set; }

    /// <summary>True while the CDP connection is usable.</summary>
    public bool IsConnected => _client.IsConnected;

    /// <summary>The last CDP error, for diagnostics.</summary>
    public string? LastError => _client.LastError;

    /// <summary>
    /// Raised for every key event that goes over the wire.
    /// </summary>
    /// <remarks>
    /// The double-typing bug - a keystroke that arrives twice - is
    /// invisible from the outside, because the application simply shows
    /// two characters.  Seeing the exact shape of what was dispatched
    /// is what makes it findable.
    /// </remarks>
    public event Action<string>? OnKeyDispatched
    {
        add => _client.OnKeyDispatched += value;
        remove => _client.OnKeyDispatched -= value;
    }

    /// <summary>
    /// Raised for every mouse event that goes over the wire.
    /// </summary>
    /// <remarks>
    /// The same diagnostic purpose as
    /// <see cref="OnKeyDispatched"/>: a click that never reaches the
    /// application looks exactly like a click that was never sent.
    /// </remarks>
    public event Action<string>? OnMouseDispatched
    {
        add => _client.OnMouseDispatched += value;
        remove => _client.OnMouseDispatched -= value;
    }

    /// <summary>Send a raw CDP command.</summary>
    public Task<string?> SendRawAsync(
        string method, System.Text.Json.Nodes.JsonObject? parameters = null,
        CancellationToken token = default)
        => _client.SendRawAsync(method, parameters, token);

    /// <summary>
    /// Put the page into an active, focused state.
    /// </summary>
    /// <remarks>
    /// Chromium throttles a window it believes is occluded: it stops
    /// producing frames, and a wheel event sent to such a renderer
    /// never completes - it waits for the compositor, which is not
    /// running.  The panel covers the browser, so from Chromium's
    /// point of view the browser IS occluded.
    ///
    /// When we launch the browser ourselves we pass the flags that
    /// prevent this.  A browser the user started by hand cannot be
    /// fixed with flags, so we turn the active state back on over the
    /// protocol instead.  Doing it here - on the router - means both
    /// paths go through the same code.
    /// </remarks>
    public async Task<bool> EnsureActiveAsync(CancellationToken token = default)
    {
        var focusOk = await _client.SendRawAsync(
            "Emulation.setFocusEmulationEnabled",
            new JsonObject { ["enabled"] = true },
            token).ConfigureAwait(false);

        var lifeOk = await _client.SendRawAsync(
            "Page.setWebLifecycleState",
            new JsonObject { ["state"] = "active" },
            token).ConfigureAwait(false);

        return focusOk is not null && lifeOk is not null;
    }

    /// <summary>Close the CDP connection and forget the position.</summary>
    public void Disconnect()
    {
        _client.Disconnect();
        _havePosition = false;
        _leftDown = false;
        _rightDown = false;
        _middleDown = false;
    }

    /// <summary>Connect to a page's debug WebSocket.</summary>
    public Task<bool> ConnectAsync(string webSocketDebuggerUrl, CancellationToken token = default)
        => _client.ConnectAsync(webSocketDebuggerUrl, token);

    /// <summary>
    /// Evaluate an expression in the page and return its value.
    /// </summary>
    /// <remarks>
    /// This is the same connection the mouse events go over.  A browser
    /// allows only one DevTools client per target, so opening a second
    /// connection for a query would either be refused or would steal
    /// the replies belonging to the mouse commands.
    /// </remarks>
    public Task<string?> EvaluateAsync(string expression, CancellationToken token = default)
        => _client.EvaluateAsync(expression, token);

    /// <summary>
    /// Read the page's viewport metrics.
    /// </summary>
    /// <remarks>
    /// This is how we learn the page's inner size and its device pixel
    /// ratio, which the viewport mapping needs.  It is re-read when
    /// the user resizes the browser window, so the mapping stays
    /// correct.
    /// </remarks>
    public async Task<bool> RefreshMetricsAsync(CancellationToken token = default)
    {
        if (Geometry is null) return false;

        var raw = await ReadMetricsValueAsync(token).ConfigureAwait(false);
        if (raw is null) return false;

        var (innerWidth, innerHeight, dpr) = raw.Value;

        // The router takes ownership of the ratio here, so the value
        // that scales the viewport and the value that converts back to
        // CSS pixels are the same number by construction.
        _devicePixelRatio = dpr;

        Geometry = new BrowserWindowGeometry
        {
            Handle = Geometry.Handle,
            CaptureWidth = Geometry.CaptureWidth,
            CaptureHeight = Geometry.CaptureHeight,
            DevicePixelRatio = dpr,
            WindowRect = Geometry.WindowRect,
            FrameRect = Geometry.FrameRect,
            ClientRect = Geometry.ClientRect,
        };

        _viewport = Geometry.ComputeViewport(innerWidth, innerHeight);
        return true;
    }

    /// <summary>
    /// Recompute the viewport assuming the capture IS the page.
    /// </summary>
    /// <remarks>
    /// This is the fallback for the case where the window's geometry is
    /// not known - the frame rect, the client rect and the capture's
    /// own rectangle are all unavailable, so the only thing left to
    /// map with is the page's own viewport.
    ///
    /// The mapping it produces is the identity: a texture coordinate
    /// (u, v) lands on the page's CSS pixel (u * innerWidth,
    /// v * innerHeight).  That is exactly right for a browser in
    /// --app mode, where the client area is the viewport and there is
    /// no caption bar to account for.
    /// </remarks>
    public async Task<bool> RefreshViewportAsync(CancellationToken token = default)
    {
        var raw = await ReadMetricsValueAsync(token).ConfigureAwait(false);
        if (raw is null) return false;

        var (innerWidth, innerHeight, dpr) = raw.Value;

        // As in RefreshMetricsAsync: the router owns the ratio, so the
        // viewport that is built here and the conversions that read it
        // back can never use different numbers.
        _devicePixelRatio = dpr;

        _viewport = new ViewportRect(
            0, 0,
            (int)Math.Round(innerWidth * dpr),
            (int)Math.Round(innerHeight * dpr));

        if (Geometry is not null)
        {
            Geometry = new BrowserWindowGeometry
            {
                Handle = Geometry.Handle,
                CaptureWidth = Geometry.CaptureWidth,
                CaptureHeight = Geometry.CaptureHeight,
                DevicePixelRatio = dpr,
                WindowRect = Geometry.WindowRect,
                FrameRect = Geometry.FrameRect,
                ClientRect = Geometry.ClientRect,
            };
        }

        return true;
    }

    private ViewportRect _viewport;

    /// <summary>The viewport's rectangle inside the captured image.</summary>
    public ViewportRect Viewport => _viewport;

    /// <summary>
    /// The result of the last Runtime.evaluate, if it produced one.
    /// </summary>
    /// <remarks>
    /// Runtime.evaluate's RESULT does not come back as the command's
    /// acknowledgement - the acknowledgement only says "the command
    /// was accepted".  The value arrives as a separate
    /// Runtime.evaluateResult event on the same socket.
    ///
    /// So the client keeps the last event's payload around, and this
    /// accessor hands it back.  It is a deliberately small amount of
    /// state: we only need one number out of the page at a time, and a
    /// full event-routing layer would be a lot of machinery for that.
    /// </remarks>
    private async Task<(int Width, int Height, double Dpr)?> ReadMetricsValueAsync(CancellationToken token)
    {
        var value = await _client.EvaluateAsync(
            "JSON.stringify({iw:window.innerWidth,ih:window.innerHeight,dpr:window.devicePixelRatio})",
            token).ConfigureAwait(false);
        if (value is null) return null;

        // The expression returns a JSON STRING, so the value arrives
        // unescaped and is parsed as JSON here.
        try
        {
            var node = JsonNode.Parse(value);
            if (node is null) return null;
            var iw = node["iw"]?.GetValue<int>() ?? 0;
            var ih = node["ih"]?.GetValue<int>() ?? 0;
            var dpr = node["dpr"]?.GetValue<double>() ?? 1.0;
            if (iw <= 0 || ih <= 0) return null;
            return (iw, ih, dpr);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Move the browser's mouse to the cursor's position.
    /// </summary>
    public async Task<bool> HoverAsync(float u, float v, int modifiers, CancellationToken token = default)
    {
        if (!_viewport.ToCss(u, v, _devicePixelRatio, out var x, out var y))
            return false;

        if (_havePosition && x == _lastX && y == _lastY) return true;

        _lastX = x;
        _lastY = y;
        _havePosition = true;

        await _dispatchLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await _client.MouseMovedAsync(x, y, modifiers, token).ConfigureAwait(false);
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    /// <summary>
    /// Press or release a button at the cursor's position.
    /// </summary>
    public async Task<bool> ButtonAsync(
        PointerButton button, bool isDown, float u, float v, int modifiers, int clickCount = 1,
        CancellationToken token = default)
    {
        if (!_viewport.ToCss(u, v, _devicePixelRatio, out var x, out var y))
            return false;

        var name = button switch
        {
            PointerButton.Left => "left",
            PointerButton.Right => "right",
            _ => "middle",
        };

        // CDP's `buttons` field is the bitmask of buttons HELD at the
        // time of the event, so it has to include the button being
        // pressed - the button goes down on the press event.
        var buttons = 0;
        if (_leftDown) buttons |= 1;
        if (_rightDown) buttons |= 2;
        if (_middleDown) buttons |= 4;

        switch (button)
        {
            case PointerButton.Left when isDown: buttons |= 1; break;
            case PointerButton.Right when isDown: buttons |= 2; break;
            case PointerButton.Middle when isDown: buttons |= 4; break;
        }

        await _dispatchLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Move first, so the click lands where the cursor is even
            // if the pointer has not moved since the last frame.
            if (!_havePosition || x != _lastX || y != _lastY)
            {
                await _client.MouseMovedAsync(x, y, modifiers, token).ConfigureAwait(false);
                _lastX = x;
                _lastY = y;
                _havePosition = true;
            }

            var ok = isDown
                ? await _client.MousePressedAsync(name, x, y, clickCount, buttons, modifiers, token).ConfigureAwait(false)
                : await _client.MouseReleasedAsync(name, x, y, clickCount, buttons, modifiers, token).ConfigureAwait(false);

            switch (button)
            {
                case PointerButton.Left: _leftDown = isDown; break;
                case PointerButton.Right: _rightDown = isDown; break;
                case PointerButton.Middle: _middleDown = isDown; break;
            }

            return ok;
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    /// <summary>
    /// The kind of key event a keystroke is dispatched as.
    /// </summary>
    /// <remarks>
    /// The rules, in one place, because they are easy to get subtly
    /// wrong and hard to debug afterwards:
    ///
    ///  * A RELEASE is always a "keyUp".  It never carries text, and
    ///    it is never a "rawKeyDown" - a naive "no text means raw"
    ///    rule sends the release as another press, and the application
    ///    never sees the key go up.
    ///
    ///  * A PRINTABLE key's PRESS is a "keyDown" that carries the
    ///    character.  The character arrives once, in the key event,
    ///    which is what a text field expects.
    ///
    ///  * Everything else - arrows, Escape, the function keys - is a
    ///    "rawKeyDown" with no text at all.  The text field must be
    ///    ABSENT rather than empty, because an empty text field still
    ///    counts as "this event has text" and the application would
    ///    commit an empty edit.
    ///
    /// A separate "char" event is never sent: the key event already
    /// carries the character, and sending both would type it twice.
    /// </remarks>
    public static string KeyEventType(ResolvedKey key) => key switch
    {
        { IsDown: false } => "keyUp",
        { Text: not null } => "keyDown",
        _ => "rawKeyDown",
    };

    /// <summary>
    /// The text a key event carries.
    /// </summary>
    /// <remarks>
    /// A release never carries text, whatever the key produced.
    /// </remarks>
    public static string? KeyEventText(ResolvedKey key) => key.IsDown ? key.Text : null;

    /// <summary>
    /// Send a keystroke to the app.
    /// </summary>
    /// <remarks>
    /// The shape of the event depends on what the key is:
    ///
    ///  * A PRINTABLE key gets a "keyDown" that carries the text.  The
    ///    character arrives once, in the key event, which is what a
    ///    text field expects.
    ///
    ///  * A NON-PRINTABLE key (an arrow, Escape, a function key) gets
    ///    a "rawKeyDown" with no text at all.  The text field must be
    ///    ABSENT rather than empty, because an empty text field still
    ///    counts as "this event has text" and the app would commit an
    ///    empty edit.
    ///
    ///  * A RELEASE is always a "keyUp", whatever the key is.  A
    ///    release carries no text and never did - sending it as a
    ///    "rawKeyDown" would tell the app that the key went down
    ///    again, and a held key would never be released.
    ///
    ///  * A "char" event is not sent separately, because the key
    ///    event already carries the character and sending both would
    ///    type it twice.
    ///
    /// The modifier bitmask carries the state of the OTHER modifiers,
    /// which is how a shortcut like Ctrl+S is expressed.
    /// </remarks>
    public async Task<bool> KeyAsync(
        ResolvedKey key, int modifiers, CancellationToken token = default)
    {
        if (key.Key.Length == 0) return false;

        var type = KeyEventType(key);
        var text = KeyEventText(key);

        var ok = await _client.KeyEventAsync(
            type,
            key.Key,
            key.Code,
            key.VirtualKey,
            text,
            modifiers,
            key.IsAutoRepeat,
            key.IsKeypad,
            key.Location,
            key.UnmodifiedText,
            token).ConfigureAwait(false);

        if (!ok && !key.IsDown)
        {
            // A release that the app refused is not fatal - it usually
            // means the app never saw the press either, which happens
            // when the app was not focused at the time.
            return false;
        }

        return ok;
    }

    /// <summary>
    /// Scroll the page.
    /// </summary>
    /// <param name="delta">
    /// The wheel delta in PIXELS.  Win32 reports 120 per detent; CDP
    /// expects pixels, so the 120 is scaled by the usual 1/120 of a
    /// line = 100 px convention, which is what Chromium itself uses.
    /// </param>
    /// <remarks>
    /// The wheel event is dispatched AT THE CURSOR.  Chromium routes
    /// the wheel to whatever is under the pointer, and a wheel event at
    /// (0, 0) would land on the top-left corner of the page instead of
    /// under the user's cursor - and would not scroll the element the
    /// user is actually pointing at.
    /// </remarks>
    public async Task<bool> ScrollAsync(int delta, int modifiers, CancellationToken token = default)
    {
        if (delta == 0) return true;

        await _dispatchLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await _client.MouseWheelAsync(_lastX, _lastY, delta, modifiers, token).ConfigureAwait(false);
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _dispatchLock.Dispose();
    }
}

/// <summary>
/// Finds the OS window that belongs to a freshly launched browser.
/// </summary>
/// <remarks>
/// The launch API hands back a PROCESS, not a window.  The window
/// appears a moment later, so this polls the shell's window list
/// until a window with the expected title shows up.
///
/// The window is identified by its title, which in --app mode is the
/// page's title.  That is the same string the DevTools endpoint
/// reports, so it doubles as the CDP target's key.
/// </remarks>
public static class BrowserWindowFinder
{
    /// <summary>
    /// Wait for a browser window with the given title.
    /// </summary>
    /// <param name="title">The window title to look for.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>The window handle, or zero when none appeared.</returns>
    public static IntPtr WaitForWindow(string title, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var entry in WindowPicker.List())
            {
                if (string.Equals(entry.Title, title, StringComparison.OrdinalIgnoreCase))
                    return entry.Handle;
            }
            Thread.Sleep(200);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Wait for the main window of a browser process we launched.
    /// </summary>
    /// <param name="process">The browser process.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>The window handle, or zero when none appeared.</returns>
    /// <remarks>
    /// Matching on the PROCESS is much more reliable than matching on
    /// the title.  In --app mode the window's title is the page's
    /// title, which for a site like YouTube changes as the page
    /// loads - so a title-based wait can miss the window entirely,
    /// or latch onto the wrong one.
    ///
    /// The process's own main window handle is the authoritative
    /// answer, and it is exactly the window the CDP target belongs to.
    /// </remarks>
    public static IntPtr WaitForWindowOfProcess(Process process, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                process.Refresh();
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero) return handle;
            }
            catch (InvalidOperationException)
            {
                // The process exited.
                return IntPtr.Zero;
            }
            Thread.Sleep(200);
        }
        return IntPtr.Zero;
    }
}

/// <summary>
/// Launches a dedicated Chromium instance that we can drive.
/// </summary>
/// <remarks>
/// We launch our OWN browser, with our OWN profile directory, rather
/// than asking the user to start theirs with a flag.  Three reasons:
///
///  * The user's normal browser session is not disturbed - no
///    "please close and reopen Chrome with this flag".
///  * The instance is in --app mode, so there is no tab strip and no
///    address bar: the client area IS the viewport, which makes the
///    coordinate mapping exact.
///  * A dedicated --user-data-dir means no conflict with a running
///    Chrome instance (Chrome refuses to open a second instance on
///    the same profile).
///
/// The flags that matter:
///
///  * --app=URL                - no tab strip, no address bar.
///  * --remote-debugging-port  - the DevTools endpoint we drive.
///  * --user-data-dir          - our own profile.
///  * --disable-backgrounding-occluded-windows - the browser must keep
///    rendering even though our panel covers it.  Without this,
///    Chromium throttles the occluded window to a crawl and the panel
///    would freeze the moment the panel came on top.
///  * --disable-features=CalculateNativeWinOcclusion - the same
///    reason, for the occlusion detection itself.
///  * --no-first-run, --no-default-browser-check - so it does not
///    pop up its own welcome flow.
public static class BrowserLauncher
{
    /// <summary>Where our dedicated browser profile lives.</summary>
    public static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RokidDiyStation", "browser-profile");

    /// <summary>
    /// Find a Chromium executable.
    /// </summary>
    /// <remarks>
    /// The usual install locations, in the order we prefer them.
    /// Chromium's own binaries (Chrome, Edge, Brave) all speak the
    /// DevTools protocol, so any of them works.
    /// </remarks>
    public static string? FindExecutable()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
            @"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe",
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        // Fall back to PATH.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userCandidates = new[]
        {
            Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(localAppData, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(localAppData, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
        };

        foreach (var path in userCandidates)
        {
            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>
    /// Launch a dedicated browser instance in --app mode.
    /// </summary>
    /// <param name="url">The URL to open.</param>
    /// <param name="port">The DevTools port.</param>
    /// <param name="width">The initial window width, in pixels.</param>
    /// <param name="height">The initial window height, in pixels.</param>
    /// <returns>The process, or null when no browser could be found.</returns>
    public static Process? Launch(string url, int port = CdpDiscovery.DefaultPort, int width = 1600, int height = 900)
    {
        var exe = FindExecutable();
        if (exe is null) return null;

        Directory.CreateDirectory(ProfileDirectory);

        var args = string.Join(' ',
            $"--app=\"{url}\"",
            $"--remote-debugging-port={port}",
            $"--user-data-dir=\"{ProfileDirectory}\"",
            $"--window-size={width},{height}",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-backgrounding-occluded-windows",
            "--disable-features=CalculateNativeWinOcclusion",
            "--disable-renderer-backgrounding");

        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        return Process.Start(psi);
    }
}
