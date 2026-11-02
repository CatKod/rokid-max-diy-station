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

    /// <summary>
    /// Serialises the whole send-and-wait cycle.
    /// </summary>
    /// <remarks>
    /// A WebSocket has ONE reader.  If two commands were in flight at
    /// once, one of them would consume the other's reply and the
    /// answers would be attributed to the wrong commands - which
    /// shows up as a click landing in the wrong place, or as a
    /// command that never completes.
    ///
    /// The lock is held across the whole exchange, so the replies come
    /// back in the order the commands were sent.
    /// </remarks>
    private readonly SemaphoreSlim _exchangeLock = new(1, 1);

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
    /// The last error the browser reported, for diagnostics.
    /// </summary>
    /// <remarks>
    /// CDP answers a refused command with an error object.  Without
    /// this property the failure mode is a silent "false", which is
    /// very hard to diagnose from the outside.
    /// </remarks>
    public string? LastError { get; private set; }

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
    /// <param name="token">A cancellation token.</param>
    /// <returns>True when the browser acknowledged the command.</returns>
    public Task<bool> SendAsync(
        string method, JsonObject? parameters = null, CancellationToken token = default)
        => SendAsync(method, parameters, null, token);

    /// <summary>
    /// Send a command and wait for its answer.
    /// </summary>
    /// <param name="method">The CDP method name, e.g. "Input.dispatchMouseEvent".</param>
    /// <param name="parameters">The method's parameters.</param>
    /// <param name="resultReader">
    /// An optional callback that receives the command's value, if the
    /// command produced one.
    /// </param>
    /// <param name="token">A cancellation token.</param>
    /// <returns>True when the browser acknowledged the command.</returns>
    public async Task<bool> SendAsync(
        string method,
        JsonObject? parameters,
        Action<string>? resultReader,
        CancellationToken token = default)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            LastError = $"socket not open (socket={(socket is null ? "null" : socket.State.ToString())}, closed={_closed})";
            return false;
        }

        // The whole exchange is serialised: one reader per socket, and
        // the replies have to be matched to the commands in order.
        await _exchangeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await SendCoreAsync(socket, method, parameters, resultReader, token).ConfigureAwait(false);
        }
        finally
        {
            _exchangeLock.Release();
        }
    }

    private async Task<bool> SendCoreAsync(
        ClientWebSocket socket,
        string method,
        JsonObject? parameters,
        Action<string>? resultReader,
        CancellationToken token)
    {
        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject(),
        };

        // A trace of EVERY key event that goes over the wire, including
        // the ones sent outside KeyEventAsync.  A keystroke that
        // arrives twice is invisible from the outside - the application
        // just shows two characters - so seeing exactly what went out
        // is what makes the bug findable.
        if (method == "Input.dispatchKeyEvent")
            OnKeyDispatched?.Invoke(message.ToJsonString());

        // The same for the mouse.  A click that goes out but never
        // arrives is just as invisible from the outside, and the two
        // failure modes - not sent, and sent to the wrong place - look
        // identical without a trace.
        if (method == "Input.dispatchMouseEvent")
            OnMouseDispatched?.Invoke(message.ToJsonString());

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
        //
        // The read is TIMED OUT.  A browser that never answers - the
        // page navigated away, the tab was closed, the renderer
        // crashed - would otherwise wedge the caller forever, and the
        // caller is the input worker that has to stay responsive.
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        readCts.CancelAfter(ResponseTimeout);

        try
        {
            while (true)
            {
                var response = await ReceiveAsync(readCts.Token).ConfigureAwait(false);
                if (response is null) return false;

                if (response.TryGetPropertyValue("id", out var idNode)
                    && idNode is not null
                    && idNode.GetValue<int>() == id)
                {
                    // An error response means the browser refused the
                    // command, which is a legitimate answer - the caller
                    // wants to know it did not work.
                    if (response.TryGetPropertyValue("error", out var error) && error is not null)
                    {
                        LastError = error.ToJsonString();
                        return false;
                    }

                    // Commands that produce a VALUE carry it in the
                    // response itself, under "result".
                    if (resultReader is not null)
                    {
                        var value = ExtractResultValue(response);
                        if (value is not null) resultReader(value);
                    }

                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Our own timeout, not the caller's cancellation.
            return false;
        }
    }

    /// <summary>
    /// How long to wait for the browser to answer a command.
    /// </summary>
    /// <remarks>
    /// The browser answers in well under a millisecond on a local
    /// socket.  Five seconds is generous enough for a loaded machine
    /// and short enough that a wedged connection does not stall the
    /// input pipeline.
    /// </remarks>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Pull the value out of a command response.
    /// </summary>
    /// <remarks>
    /// The shape is <c>{ "id": N, "result": { "result": { "type": ...,
    /// "value": ... } } }</c>.  The inner "result" is the remote
    /// object's description, and its "value" member holds the actual
    /// value when returnByValue was requested.
    ///
    /// A STRING result comes back quoted and escaped - a page that
    /// returns <c>{"a":1}</c> as a string arrives as
    /// <c>"{\"a\":1}"</c>.  Parsing that raw text as JSON fails with a
    /// confusing "invalid start of a property name", so the string is
    /// unescaped here and the INNER text is returned.  A non-string
    /// result is returned as its raw JSON.
    /// </remarks>
    private static string? ExtractResultValue(JsonObject response)
    {
        if (!response.TryGetPropertyValue("result", out var resultNode)
            || resultNode is not JsonObject result)
            return null;

        if (!result.TryGetPropertyValue("result", out var innerNode)
            || innerNode is not JsonObject inner)
            return null;

        if (!inner.TryGetPropertyValue("value", out var valueNode)
            || valueNode is null)
            return null;

        // A string value: return the unescaped inner text.
        if (valueNode is JsonValue value && value.TryGetValue<string>(out var text))
            return text;

        // Anything else: the raw JSON.
        return valueNode.ToJsonString();
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
            LastError = "receive: " + ex.GetType().Name + ": " + ex.Message;
            return null;
        }

        var text = Encoding.UTF8.GetString(builder.ToArray());
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Evaluate an expression in the page and return its value.
    /// </summary>
    /// <param name="expression">The JavaScript to evaluate.</param>
    /// <param name="token">A cancellation token.</param>
    /// <returns>The result's value, as raw JSON, or null on failure.</returns>
    /// <remarks>
    /// The value comes back in the command's RESPONSE, under
    /// result.result.value - not as a separate event.  That matters:
    /// waiting for an event that never arrives hangs forever.
    ///
    /// The value is returned as raw JSON rather than a parsed string,
    /// because the caller may want a number, a string or an object.
    /// </remarks>
    public async Task<string?> EvaluateAsync(string expression, CancellationToken token = default)
    {
        string? value = null;

        var ok = await SendAsync(
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
            },
            v => value = v,
            token).ConfigureAwait(false);

        if (!ok) return null;
        return value;
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
    /// Send a raw CDP command and return its JSON result.
    /// </summary>
    /// <param name="method">The CDP method name.</param>
    /// <param name="parameters">The method's parameters.</param>
    /// <param name="token">A cancellation token.</param>
    /// <returns>The command's raw result, or null when it failed.</returns>
    public async Task<string?> SendRawAsync(
        string method, JsonObject? parameters = null, CancellationToken token = default)
    {
        string? value = null;
        var ok = await SendAsync(method, parameters, v => value = v, token).ConfigureAwait(false);
        if (!ok) return null;
        return value ?? "{}";
    }

    /// <summary>
    /// Turn the mouse wheel.
    /// </summary>
    /// <param name="deltaX">Horizontal wheel delta.</param>
    /// <param name="deltaY">Vertical wheel delta.</param>
    public Task<bool> MouseWheelAsync(
        double x, double y, double deltaY, int modifiers = 0, CancellationToken token = default)
        => SendAsync("Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = "mouseWheel",
            ["button"] = "none",
            ["x"] = x,
            ["y"] = y,
            ["deltaX"] = 0,
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

    // -----------------------------------------------------------------
    // Input.dispatchKeyEvent
    // -----------------------------------------------------------------

    /// <summary>
    /// Press or release a key.
    /// </summary>
    /// <param name="type">
    /// "keyDown", "keyUp", "rawKeyDown" or "char".
    /// </param>
    /// <param name="key">The DOM <c>KeyboardEvent.key</c> value.</param>
    /// <param name="code">The DOM <c>KeyboardEvent.code</c> value.</param>
    /// <param name="windowsVirtualKeyCode">The Win32 virtual key code.</param>
    /// <param name="text">The text the key produced, for printable keys.</param>
    /// <param name="modifiers">The modifier bitmask in effect.</param>
    /// <param name="autoRepeat">True when this is a key repeat.</param>
    /// <param name="isKeypad">True for the numeric keypad.</param>
    /// <param name="location">0 = standard, 1 = left, 2 = right, 3 = numpad.</param>
    public async Task<bool> KeyEventAsync(
        string type,
        string key,
        string code,
        int windowsVirtualKeyCode,
        string? text = null,
        int modifiers = 0,
        bool autoRepeat = false,
        bool isKeypad = false,
        int location = 0,
        string? unmodifiedText = null,
        CancellationToken token = default)
    {
        var parameters = new JsonObject
        {
            ["type"] = type,
            ["key"] = key,
            ["code"] = code,
            ["windowsVirtualKeyCode"] = windowsVirtualKeyCode,
            ["nativeVirtualKeyCode"] = windowsVirtualKeyCode,
            ["modifiers"] = modifiers,
            ["autoRepeat"] = autoRepeat,
            ["isKeypad"] = isKeypad,
            ["location"] = location,
        };

        // The text fields carry the character.  Chromium uses them
        // when the key is one that produces text, and they must be
        // ABSENT - not empty - for a key that does not, because an
        // empty string still counts as "this event has text" and
        // Chromium would then commit an empty edit.
        if (!string.IsNullOrEmpty(text))
        {
            parameters["text"] = text;

            // The unmodified text is the character without the
            // modifiers - the "a" of a key that produced "A".  It is
            // what an application reads to know what the key MEANS
            // rather than what it typed.  When the two are the same,
            // the single value is enough.
            parameters["unmodifiedText"] = string.IsNullOrEmpty(unmodifiedText)
                ? text
                : unmodifiedText;
        }

        var ok = await SendAsync("Input.dispatchKeyEvent", parameters, token).ConfigureAwait(false);
        return ok;
    }

    /// <summary>
    /// Raised for every key event that is dispatched.
    /// </summary>
    /// <remarks>
    /// This is a diagnostic hook: the double-typing bug - a keystroke
    /// that arrives twice - is invisible from the outside, because the
    /// application simply shows two characters.  Being able to see the
    /// exact shape of what went over the wire is what makes the bug
    /// findable.
    /// </remarks>
    public event Action<string>? OnKeyDispatched;

    /// <summary>
    /// Raised for every mouse event that is dispatched.
    /// </summary>
    /// <remarks>
    /// The same diagnostic purpose as <see cref="OnKeyDispatched"/>: a
    /// click that never reaches the application looks exactly like a
    /// click that was never sent, and only the trace tells the two
    /// apart.
    /// </remarks>
    public event Action<string>? OnMouseDispatched;

    /// <summary>
    /// Send a character as a "char" event.
    /// </summary>
    /// <remarks>
    /// This is the text-only event.  It exists because some
    /// applications - text inputs in particular - want the character
    /// without a key event, and because a key event that carries text
    /// and a separate char event would insert the character twice.
    /// </remarks>
    public Task<bool> CharEventAsync(
        string text,
        int modifiers = 0,
        CancellationToken token = default)
        => SendAsync("Input.dispatchKeyEvent", new JsonObject
        {
            ["type"] = "char",
            ["key"] = text,
            ["text"] = text,
            ["unmodifiedText"] = text,
            ["modifiers"] = modifiers,
        }, token);

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
        _exchangeLock.Dispose();
    }
}
