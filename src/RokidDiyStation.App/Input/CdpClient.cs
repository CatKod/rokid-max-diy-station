// SPDX-License-Identifier: MIT
//
// A Chrome DevTools Protocol client, just big enough to drive a mouse.
//
// Why CDP at all
// --------------
// The panel window is WS_EX_TOPMOST and opaque, and it covers the
// entire Rokid display.  Win32 routes MOUSE EVENTS BY SPATIAL
// HIT-TESTING at the cursor's coordinates, walking the Z-order from
// the top - not by foreground focus.  So a click sent with SendInput
// lands on our panel and can NEVER reach the browser behind it.
//
// Making the browser HWND_TOPMOST to get above the panel would mean
// it paints OVER the AR panel, which destroys the effect.  And
// PostMessage does not work either: Chromium checks the real
// GetKeyState() and ::GetCursorPos() and discards the synthetic
// event.
//
// CDP sidesteps the whole Win32 input queue.  InputHandler::
// DispatchMouseEvent builds a blink::WebMouseEvent and forwards it
// straight to the renderer over Mojo IPC.  The browser does not need
// to be foreground, does not need focus, and the desktop cursor never
// moves.  That is exactly the property this product needs.
//
// The protocol over the wire
// --------------------------
// The browser exposes an HTTP endpoint that lists its debug targets,
// each with a `webSocketDebuggerUrl`.  Over that WebSocket, commands
// are JSON objects with an `id`, and the browser answers with a JSON
// object carrying the same `id`.  Events (which we do not need) come
// without an `id`.
//
// We use System.Net.WebSockets.ClientWebSocket, which is in the
// base class library - no extra dependency.
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RokidDiyStation.App.Input;

/// <summary>
/// A connection to one debuggable page in a Chromium browser.
/// </summary>
public sealed class CdpClient : IDisposable
{
    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _nextId = 1;
    private volatile bool _closed;

    /// <summary>The target's title, for matching a window to a page.</summary>
    public string Title { get; init; } = "";

    /// <summary>The target's URL.</summary>
    public string Url { get; init; } = "";

    /// <summary>The target's id.</summary>
    public string TargetId { get; init; } = "";

    /// <summary>True while the connection is usable.</summary>
    public bool IsConnected => _socket is { State: WebSocketState.Open } && !_closed;

    /// <summary>
    /// Connect to a page's debug WebSocket.
    /// </summary>
    /// <param name="webSocketDebuggerUrl">The URL from the target list.</param>
    public async Task<bool> ConnectAsync(string webSocketDebuggerUrl, CancellationToken token = default)
    {
        try
        {
            var socket = new ClientWebSocket();
            // The DevTools endpoint rejects requests that do not look
            // like a browser's, and it does not like an Origin header
            // at all — Chrome rejects the connection outright when
            // one is present.
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            await socket.ConnectAsync(new Uri(webSocketDebuggerUrl), token).ConfigureAwait(false);
            _socket = socket;
            _closed = false;
            return true;
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException
                                      or HttpRequestException
                                      or UriFormatException
                                      or OperationCanceledException)
        {
            _socket = null;
            return false;
        }
    }

    /// <summary>
    /// Send a command and wait for its answer.
    /// </summary>
    /// <param name="method">The CDP method name, e.g. "Input.dispatchMouseEvent".</param>
    /// <param name="parameters">The method's parameters.</param>
    /// <returns>True when the browser acknowledged the command.</returns>
    public async Task<bool> SendAsync(string method, JsonObject? parameters = null, CancellationToken token = default)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return false;

        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject(),
        };

        var payload = Encoding.UTF8.GetBytes(message.ToJsonString());

        try
        {
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            _closed = true;
            return false;
        }

        // Read until we see the answer with our id.  Events and other
        // traffic may arrive in between, so we keep reading.
        while (true)
        {
            var response = await ReceiveAsync(token).ConfigureAwait(false);
            if (response is null) return false;

            if (response.TryGetPropertyValue("id", out var idNode)
                && idNode is not null
                && idNode.GetValue<int>() == id)
            {
                // An error response means the browser refused the
                // command, which is a legitimate answer - the caller
                // wants to know it did not work.
                if (response.TryGetPropertyValue("error", out var error) && error is not null)
                    return false;
                return true;
            }
        }
    }

    /// <summary>
    /// Read one complete message from the socket.
    /// </summary>
    private async Task<JsonObject?> ReceiveAsync(CancellationToken token)
    {
        var socket = _socket;
        if (socket is null) return null;

        var buffer = new byte[16 * 1024];
        var builder = new MemoryStream();

        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _closed = true;
                    return null;
                }

                builder.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            _closed = true;
            return null;
        }

        var text = Encoding.UTF8.GetString(builder.ToArray());
        try
        {
            var node = JsonNode.Parse(text) as JsonObject;
            if (node is null) return null;

            // A Runtime.evaluateResult event carries the value we asked
            // for.  It has no `id`, so it is an EVENT rather than a
            // command acknowledgement.
            if (!node.ContainsKey("id")
                && node.TryGetPropertyValue("method", out var methodNode)
                && methodNode is not null
                && methodNode.GetValue<string>() == "Runtime.evaluateResult")
            {
                if (node.TryGetPropertyValue("result", out var resultNode)
                    && resultNode is JsonObject resultObject
                    && resultObject.TryGetPropertyValue("value", out var valueNode)
                    && valueNode is not null)
                {
                    LastEvaluateResult = valueNode.ToJsonString().Trim('"');
                }
            }

            return node;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
    public string? LastEvaluateResult { get; private set; }

    /// <summary>
    /// Evaluate an expression in the page and return its value.
    /// </summary>
    /// <param name="expression">The JavaScript to evaluate.</param>
    /// <param name="token">A cancellation token.</param>
    /// <returns>The result's string value, or null on failure.</returns>
    public async Task<string?> EvaluateAsync(string expression, CancellationToken token = default)
    {
        LastEvaluateResult = null;

        var ok = await SendAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
        }, token).ConfigureAwait(false);

        if (!ok) return null;

        // The result may already have been consumed by SendAsync's
        // read loop, or it may still be in flight.  Read until we see
        // it, or until the socket goes quiet.
        var deadline = Environment.TickCount64 + 2000;
        while (LastEvaluateResult is null && Environment.TickCount64 < deadline)
        {
            var message = await ReceiveAsync(token).ConfigureAwait(false);
            if (message is null) break;
        }

        return LastEvaluateResult;
    }

    // -----------------------------------------------------------------
    // Input.dispatchMouseEvent
    // -----------------------------------------------------------------

    /// <summary>
    /// Move the mouse to a CSS position.
    /// </summary>
    public Task<bool> MouseMovedAsync(double x, double y, int modifiers = 0, CancellationToken token = default)
        => SendAsync("Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = "mouseMoved",
            ["x"] = x,
            ["y"] = y,
            ["button"] = "none",
            ["buttons"] = 0,
            ["modifiers"] = modifiers,
            ["pointerType"] = "mouse",
        }, token);

    /// <summary>
    /// Press a mouse button.
    /// </summary>
    /// <param name="button">"left", "right" or "middle".</param>
    /// <param name="clickCount">1 for a click, 2 for a double-click.</param>
    /// <param name="buttons">The bitmask of buttons currently held.</param>
    public Task<bool> MousePressedAsync(
        string button, double x, double y, int clickCount = 1, int buttons = 0, int modifiers = 0,
        CancellationToken token = default)
        => SendAsync("Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = "mousePressed",
            ["x"] = x,
            ["y"] = y,
            ["button"] = button,
            ["buttons"] = buttons,
            ["clickCount"] = clickCount,
            ["modifiers"] = modifiers,
            ["pointerType"] = "mouse",
        }, token);

    /// <summary>
    /// Release a mouse button.
    /// </summary>
    public Task<bool> MouseReleasedAsync(
        string button, double x, double y, int clickCount = 1, int buttons = 0, int modifiers = 0,
        CancellationToken token = default)
        => SendAsync("Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = "mouseReleased",
            ["x"] = x,
            ["y"] = y,
            ["button"] = button,
            ["buttons"] = buttons,
            ["clickCount"] = clickCount,
            ["modifiers"] = modifiers,
            ["pointerType"] = "mouse",
        }, token);

    /// <summary>
    /// Turn the mouse wheel.
    /// </summary>
    /// <param name="deltaX">Horizontal wheel delta.</param>
    /// <param name="deltaY">Vertical wheel delta.</param>
    public Task<bool> MouseWheelAsync(double deltaX, double deltaY, int modifiers = 0, CancellationToken token = default)
        => SendAsync("Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = "mouseWheel",
            ["x"] = 0,
            ["y"] = 0,
            ["deltaX"] = deltaX,
            ["deltaY"] = deltaY,
            ["modifiers"] = modifiers,
            ["pointerType"] = "mouse",
        }, token);

    /// <summary>
    /// The modifier bitmask CDP expects.
    /// </summary>
    /// <remarks>
    /// Alt = 1, Ctrl = 2, Meta / Windows = 4, Shift = 8.
    /// </remarks>
    public static int ModifierMask(bool alt, bool control, bool meta, bool shift)
    {
        var mask = 0;
        if (alt) mask |= 1;
        if (control) mask |= 2;
        if (meta) mask |= 4;
        if (shift) mask |= 8;
        return mask;
    }

    /// <summary>
    /// Close the socket, keeping the client reusable.
    /// </summary>
    public void Disconnect()
    {
        _closed = true;
        var socket = _socket;
        _socket = null;
        try { socket?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _closed = true;
        try { _socket?.Dispose(); } catch { }
        _socket = null;
        _sendLock.Dispose();
    }
}
