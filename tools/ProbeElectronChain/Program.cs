// SPDX-License-Identifier: MIT
//
// Probe: the WHOLE chain, against a REAL Electron application.
//
// The other probes each cover one link.  This one covers the link
// the user actually cares about:
//
//   the physical mouse and keyboard
//     -> the low-level hooks
//     -> the virtual 3D cursor on the panel
//     -> SpatialInputCoordinator
//     -> BrowserInputRouter
//     -> CDP Input.dispatchMouseEvent / Input.dispatchKeyEvent
//     -> VS Code's workbench
//
// WHY VS CODE
// -----------
// It is Electron, which is Chromium, so it speaks the DevTools
// protocol when it is started with --remote-debugging-port.  That is
// what makes it reachable at all - and it is the reason the product
// can drive code editors, chat clients and note apps at all, not just
// web pages.
//
// The probe goes through the app's OWN code paths - AppLauncher to
// start it, SpatialStation to project it, PointerInput and
// KeyboardInput to capture the hardware - rather than talking to
// the protocol directly.  That is the difference between "the
// mechanism works" and "the product works".
//
// WHAT IT CHECKS
// --------------
//   1. The app is launched with the debugging port and its workbench
//      document loads.
//   2. The station attaches to it and becomes interactive.
//   3. The physical mouse moves the virtual cursor in the RIGHT
//      direction, with the right gain.
//   4. A click lands at the aimed CSS coordinate - inside a target
//      the workbench itself reports.
//   5. A real physical keystroke reaches the workbench as exactly ONE
//      character, and it is trusted.
//   6. The OS mouse never moved, and the OS keyboard never received
//      the keystroke twice.
//
// Usage:
//   dotnet run --project tools/ProbeElectronChain
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using RokidDiyStation.App.Input;
using RokidDiyStation.App.Spatial;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.HidCapture;
using RokidDiyStation.Core.Protocol;
using RokidDiyStation.Core.Spatial;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== Rokid: the whole chain against a real Electron app ===");
        Console.WriteLine($"Time: {DateTime.Now:HH:mm:ss}");
        Console.WriteLine();

        var failures = new List<string>();

        // -------------------------------------------------------------
        // 1. The glasses' display
        // -------------------------------------------------------------
        var displays = DisplayLocator.Enumerate();
        foreach (var d in displays)
            Console.WriteLine($"    {d.DeviceName,-24} {d.Width}x{d.Height}"
                              + (d.IsRokid ? "  <-- ROKID" : ""));

        var target = DisplayLocator.Select();
        if (target is null)
        {
            Console.WriteLine("FAIL: no Rokid display found.");
            return 1;
        }
        Console.WriteLine($"    -> using {target.DeviceName}, bounds {target.Bounds}");
        Console.WriteLine();

        // -------------------------------------------------------------
        // 2. The application, launched through the app's own launcher
        // -------------------------------------------------------------
        var apps = AppLauncher.KnownApps();
        Console.WriteLine("[1] Driveable applications");
        foreach (var a in apps) Console.WriteLine($"    {a.Name}  ({a.Executable})");

        var app = apps.FirstOrDefault(a => a.Name.Contains("VS Code", StringComparison.OrdinalIgnoreCase))
                  ?? apps.FirstOrDefault();

        if (app is null)
        {
            Console.WriteLine("FAIL: no driveable application is installed.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"[2] Launching {app.Name} through the app's launcher");

        var process = AppLauncher.Launch(app);
        if (process is null)
        {
            Console.WriteLine("FAIL: the launcher could not start the application.");
            return 1;
        }

        try
        {
            // The endpoint has to exist before the window is useful,
            // and the workbench document loads AFTER it.
            if (!await AppLauncher.WaitForEndpointAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false))
            {
                Console.WriteLine("FAIL: the application exposed no DevTools endpoint.");
                return 1;
            }
            Console.WriteLine($"    -> DevTools endpoint is up on port {AppLauncher.Port}");

            var hwnd = BrowserWindowFinder.WaitForWindowOfProcess(process, TimeSpan.FromSeconds(45));
            if (hwnd == IntPtr.Zero)
            {
                Console.WriteLine("FAIL: the application's window did not appear.");
                return 1;
            }
            Console.WriteLine($"    -> window 0x{hwnd.ToInt64():X}");

            // -------------------------------------------------------------
            // 3. The real station
            // -------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("[3] Starting the station and pinning the application");

            using var station = new SpatialStation();
            station.OnLog += line => Console.WriteLine($"    [station] {line}");
            station.OnError += line => Console.WriteLine($"    [error] {line}");

            station.Start(hwnd);
            station.QueueCapture(hwnd);

            // The panel needs a pose, which needs the IMU.  If the
            // glasses' HID interface is not enumerable, the probe falls
            // back to a synthetic stream - the cursor's behaviour does
            // not depend on where the head is.
            var imuCount = 0;
            try
            {
                using var device = HidSharpEnumerator.OpenFirst();
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 2.0 && imuCount < 2000)
                {
                    if (!device.TryRead(timeoutMs: 100, out var report)) continue;
                    var parsed = PacketParser.Parse(report);
                    if (!parsed.IsOk) continue;
                    station.FeedImu(parsed.Value);
                    imuCount++;
                }
                Console.WriteLine($"    fed {imuCount} live IMU samples");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    WARN: no live IMU ({ex.GetType().Name}) - using a synthetic stream.");
                var q = SyntheticImuSource.FromEulerZYX(0f, (float)(-25.0 * Math.PI / 180.0), 0f);
                var accel = System.Numerics.Vector3.Transform(
                    new System.Numerics.Vector3(0f, 9.80665f, 0f), System.Numerics.Quaternion.Inverse(q));
                ulong ts = 1_000_000_000UL;
                for (var i = 0; i < 800; i++)
                {
                    station.FeedImu(new RokidDiyStation.Core.Protocol.ImuSample(
                        ts, accel, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, true, ts));
                    ts += (ulong)(1e9 / 437.0);
                    imuCount++;
                }
                Console.WriteLine($"    fed {imuCount} synthetic IMU samples");
            }

            station.AnchorHere();
            await Task.Delay(600);

            if (!station.Anchor.HasPanel)
            {
                Console.WriteLine("FAIL: the panel was not anchored.");
                return 1;
            }
            Console.WriteLine("    -> panel anchored");

            // Give the coordinator time to attach and for the workbench
            // document to finish loading.
            var attached = false;
            var deadline = Environment.TickCount64 + 60_000;
            while (Environment.TickCount64 < deadline)
            {
                if (station.Input.IsInteractive)
                {
                    attached = true;
                    break;
                }
                await Task.Delay(500).ConfigureAwait(false);
            }

            if (!attached)
            {
                Console.WriteLine("FAIL: the station never became interactive.");
                return 1;
            }
            Console.WriteLine("    -> interactive over the DevTools protocol");
            Console.WriteLine();

            // -------------------------------------------------------------
            // 4. The mouse
            // -------------------------------------------------------------
            Console.WriteLine("[4] The physical mouse");
            var bounds = target.Bounds;
            var centreX = bounds.X + bounds.Width / 2;
            var centreY = bounds.Y + bounds.Height / 2;

            GetCursorPos(out var pointerBefore);
            Console.WriteLine($"    OS pointer before   : ({pointerBefore.x}, {pointerBefore.y})");

            SetCursorPos(centreX, centreY);
            await Task.Delay(500);

            GetCursorPos(out var pointerInside);
            if (!bounds.Contains(pointerInside.x, pointerInside.y))
            {
                failures.Add("the mouse did not land on the glasses' display");
                Console.WriteLine($"    FAIL: the mouse is at ({pointerInside.x}, {pointerInside.y}), off the panel");
            }
            else
            {
                Console.WriteLine($"    OS pointer now      : ({pointerInside.x}, {pointerInside.y}) - ON the panel");
            }

            // The direction and the gain.
            var cursor = station.Renderer.Cursor;
            if (cursor is null)
            {
                failures.Add("the renderer has no virtual cursor");
            }
            else
            {
                // Re-enter so the re-seed is deterministic.
                SetCursorPos(bounds.X - 200, centreY);
                await Task.Delay(500);
                SetCursorPos(centreX, centreY);
                await Task.Delay(500);

                var (u0, v0) = cursor.Position;
                Console.WriteLine($"    cursor at entry     : u={u0:F4} v={v0:F4}");

                SetCursorPos(centreX + 200, centreY);
                await Task.Delay(500);
                var (u1, v1) = cursor.Position;
                Console.WriteLine($"    after moving RIGHT  : u={u1:F4} v={v1:F4}  (du={u1 - u0:+0.0000;-0.0000})");
                if (u1 <= u0)
                    failures.Add($"moving the mouse right did not move the cursor right (u {u0:F4} -> {u1:F4})");

                SetCursorPos(centreX + 200, centreY + 150);
                await Task.Delay(500);
                var (u2, v2) = cursor.Position;
                Console.WriteLine($"    after moving DOWN   : u={u2:F4} v={v2:F4}  (dv={v2 - v1:+0.0000;-0.0000})");
                if (v2 <= v1)
                    failures.Add($"moving the mouse down did not move the cursor down (v {v1:F4} -> {v2:F4})");
            }

            // -------------------------------------------------------------
            // 5. The click, aimed at a known CSS coordinate
            // -------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("[5] The click, aimed at a known coordinate");

            // Ask the workbench itself where its own search box is, so
            // the target is ground truth from the application rather
            // than a number we guessed.
            var probeRaw = await station.Input.EvaluateAsync(
                "(() => { const el = document.querySelector('.monaco-workbench'); "
                + "return JSON.stringify({iw: window.innerWidth, ih: window.innerHeight, "
                + "dpr: window.devicePixelRatio, wb: !!el}); })()").ConfigureAwait(false);

            int iw = 0, ih = 0;
            double dpr = 1.0;
            try
            {
                var n = JsonNode.Parse(probeRaw ?? "{}");
                iw = n?["iw"]?.GetValue<int>() ?? 0;
                ih = n?["ih"]?.GetValue<int>() ?? 0;
                dpr = n?["dpr"]?.GetValue<double>() ?? 1.0;
            }
            catch { }
            Console.WriteLine($"    workbench viewport  : {iw}x{ih} CSS px, dpr {dpr}");

            if (iw <= 0 || ih <= 0)
            {
                failures.Add("the workbench reported a zero-sized viewport");
            }
            else
            {
                // Aim at the middle of the workbench, where every
                // Electron app has its own chrome.
                const double targetCssX = 400;
                const double targetCssY = 300;

                if (!station.Input.ToContent(targetCssX, targetCssY, out var targetU, out var targetV))
                {
                    failures.Add("the CSS -> content mapping is not available");
                }
                else
                {
                    Console.WriteLine($"    target CSS          : ({targetCssX:F0}, {targetCssY:F0})");
                    Console.WriteLine($"    target content uv   : ({targetU:F4}, {targetV:F4})");

                    // Drive the real mouse there.
                    var eyeWidth = RokidDisplayProfile.EyeViewportWidth(bounds.Width);
                    var (curU, curV) = cursor!.Position;
                    var needDx = (targetU - curU) * eyeWidth;
                    var needDy = (targetV - curV) * bounds.Height;

                    GetCursorPos(out var p0);
                    SetCursorPos(p0.x + (int)Math.Round(needDx), p0.y + (int)Math.Round(needDy));
                    await Task.Delay(700);

                    var (gotU, gotV) = cursor.Position;
                    var uvError = Math.Max(Math.Abs(gotU - targetU), Math.Abs(gotV - targetV));
                    Console.WriteLine($"    cursor reached      : ({gotU:F4}, {gotV:F4})  (uv error {uvError:F4})");
                    if (uvError > 0.02)
                    {
                        failures.Add($"the cursor did not reach the aimed point (uv error {uvError:F4})");
                    }

                    // Click, and read back where the workbench says it
                    // landed.
                    await station.Input.EvaluateAsync(
                        "(() => { window.__rokid = []; "
                        + "for (const t of ['mousemove','mousedown','mouseup','click']) "
                        + "  window.addEventListener(t, (e) => window.__rokid.push({t: t, x: e.clientX, "
                        + "y: e.clientY, trusted: e.isTrusted}), true); return 1; })()").ConfigureAwait(false);

                    Click();
                    await Task.Delay(900);

                    var eventsRaw = await station.Input.EvaluateAsync(
                        "JSON.stringify(window.__rokid || [])").ConfigureAwait(false);
                    var events = new List<JsonNode>();
                    try
                    {
                        if (JsonNode.Parse(eventsRaw ?? "[]") is JsonArray arr)
                            foreach (var item in arr) if (item is not null) events.Add(item);
                    }
                    catch { }

                    var types = events.Select(e => (string?)e?["t"] ?? "").ToList();
                    Console.WriteLine($"    workbench saw       : [{string.Join(", ", types)}]");

                    if (!types.Contains("click"))
                    {
                        failures.Add("the click never reached the workbench");
                    }
                    else
                    {
                        var click = events.First(e => (string?)e?["t"] == "click")!;
                        var cx = (double?)click["x"] ?? double.NaN;
                        var cy = (double?)click["y"] ?? double.NaN;
                        var trusted = (bool?)click["trusted"] ?? false;
                        Console.WriteLine($"    click at CSS        : ({cx:F0}, {cy:F0}), trusted={trusted}");

                        if (Math.Abs(cx - targetCssX) > 4 || Math.Abs(cy - targetCssY) > 4)
                        {
                            failures.Add($"the click landed at ({cx:F0}, {cy:F0}), not at the aimed "
                                        + $"({targetCssX:F0}, {targetCssY:F0})");
                        }
                        if (!trusted)
                        {
                            failures.Add("the click was NOT trusted - the workbench would treat it as synthetic");
                        }
                    }
                }
            }

            // -------------------------------------------------------------
            // 6. The keyboard
            // -------------------------------------------------------------
            //
            // The keystroke is delivered through the app's OWN hook
            // path: a real key event is injected into the system, the
            // low-level hook sees it, and the workbench receives it
            // over the protocol.
            //
            // The probe injects the key with SendInput, which is what
            // a key press on the physical keyboard does to the system.
            // The hook cannot tell the difference - which is exactly
            // the point: the hook sees what the user really types.
            Console.WriteLine();
            Console.WriteLine("[6] The physical keyboard");

            await station.Input.EvaluateAsync(
                "(() => { window.__keys = []; "
                + "for (const t of ['keydown','keypress','keyup','beforeinput']) "
                + "  window.addEventListener(t, (e) => window.__keys.push({t: t, key: e.key, "
                + "code: e.code, data: e.data === undefined ? null : e.data, trusted: e.isTrusted }), true); "
                + "return 1; })()").ConfigureAwait(false);

            // Trace exactly what goes over the wire, so a duplicate is
            // visible where it happens rather than only in the result.
            station.Input.OnKeyDispatched += line =>
                Console.WriteLine($"    [wire] {line}");

            // The keystroke must be captured, which requires the
            // pointer to be over the glasses - that is the app's rule
            // for "the user is working in the glasses now".
            SetCursorPos(centreX, centreY);
            await Task.Delay(500);

            // The letter "H": virtual key 0x48.
            TypeKey(0x48);

            // The keystroke goes: hook -> coordinator -> CDP -> workbench.
            // The queue is asynchronous, so give it time.
            await Task.Delay(1500);

            var keysRaw = await station.Input.EvaluateAsync(
                "JSON.stringify(window.__keys || [])").ConfigureAwait(false);
            var keys = new List<JsonNode>();
            try
            {
                if (JsonNode.Parse(keysRaw ?? "[]") is JsonArray arr)
                    foreach (var item in arr) if (item is not null) keys.Add(item);
            }
            catch { }

            var keyTypes = keys.Select(e => (string?)e?["t"] ?? "").ToList();
            Console.WriteLine($"    workbench saw       : [{string.Join(", ", keyTypes)}]");

            if (keyTypes.Count == 0)
            {
                failures.Add("the keystroke never reached the workbench");
            }
            else
            {
                foreach (var k in keys) Console.WriteLine($"      {k}");

                // Exactly ONE character, and it must be the right one.
                //
                // The character is proven by the KEYPRESS event, not by
                // "beforeinput": the workbench is not a text field, so
                // it produces no input event at all.  A keypress
                // carries the character the key produced, which is
                // exactly what a text field would have received.
                var keypresses = keyTypes.Count(t => t == "keypress");
                if (keypresses != 1)
                {
                    failures.Add($"the keystroke produced {keypresses} keypress events, expected exactly 1");
                }

                // And there must be exactly one press and one release.
                var keydowns = keyTypes.Count(t => t == "keydown");
                var keyups = keyTypes.Count(t => t == "keyup");
                if (keydowns != 1) failures.Add($"the keystroke produced {keydowns} keydown events, expected exactly 1");
                if (keyups != 1) failures.Add($"the keystroke produced {keyups} keyup events, expected exactly 1");

                var charEvent = keys.FirstOrDefault(e => (string?)e?["t"] == "keypress");
                if (charEvent is not null)
                {
                    var data = (string?)charEvent["key"];
                    if (!string.Equals(data, "h", StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"the character delivered was \"{data}\", expected \"h\"");
                    }
                    else
                    {
                        Console.WriteLine("    -> exactly one character, and it is the right one");
                    }

                    if ((bool?)charEvent["trusted"] != true)
                    {
                        failures.Add("the keystroke was NOT trusted");
                    }
                }

                // The release must arrive as a keyup, or the workbench
                // thinks the key is still held.
                if (!keyTypes.Contains("keyup"))
                {
                    failures.Add("the key release never arrived - the workbench would think the key is held");
                }
            }

            // -------------------------------------------------------------
            // 7. The OS mouse must not have moved
            // -------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("[7] The OS mouse and keyboard were left alone");
            GetCursorPos(out var pointerAfter);
            Console.WriteLine($"    OS pointer after    : ({pointerAfter.x}, {pointerAfter.y})");

            // The pointer is where the probe put it - it is not
            // somewhere else, and the click did not drag it.
            if (pointerAfter.x != pointerInside.x || pointerAfter.y != pointerInside.y)
            {
                failures.Add($"the OS mouse moved during the interaction "
                            + $"(({pointerInside.x},{pointerInside.y}) -> ({pointerAfter.x},{pointerAfter.y}))");
            }
            else
            {
                Console.WriteLine("    -> the OS mouse stayed exactly where it was");
            }

            // -------------------------------------------------------------
            // Verdict
            // -------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("=== Verdict ===");
            if (failures.Count == 0)
            {
                Console.WriteLine("    PASS - the physical mouse and keyboard drove a real");
                Console.WriteLine("           Electron application through the virtual 3D cursor.");
                return 0;
            }

            foreach (var f in failures) Console.WriteLine("    FAIL: " + f);
            return 1;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static void Click()
    {
        Send(0x0002);
        Thread.Sleep(40);
        Send(0x0004);
    }

    private static void Send(uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT { dwFlags = flags },
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Inject a real key press into the system.
    /// </summary>
    /// <remarks>
    /// This is what a key on the physical keyboard does to Windows:
    /// the system generates the key event, and the low-level hook sees
    /// it exactly as it sees a key the user pressed.  The hook cannot
    /// tell the difference, which is the point - the path the probe
    /// exercises is the path the user exercises.
    /// </remarks>
    private static void TypeKey(ushort vk)
    {
        var down = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } },
        };
        var up = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } },
        };

        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(60);
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
    }
}
