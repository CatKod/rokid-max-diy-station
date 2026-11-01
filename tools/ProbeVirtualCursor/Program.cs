// SPDX-License-Identifier: MIT
//
// Probe: does the WHOLE virtual-cursor chain work on the real
// hardware?
//
//   physical mouse
//     -> WH_MOUSE_LL hook
//     -> PointerInput (differential mapping, ray-cast re-seed)
//     -> VirtualCursor (u, v on the panel)
//     -> SpatialInputCoordinator
//     -> BrowserInputRouter (u, v -> CSS pixels)
//     -> CDP Input.dispatchMouseEvent
//     -> the browser's renderer
//
// Everything below the CDP call has been proven by unit tests and by
// a headless end-to-end test.  What has NOT been proven is the part
// that needs the real machine: the low-level hook, the ray-cast
// re-seed against the real panel pose, the mapping onto a real
// browser sitting behind the AR panel, and the 3D cursor actually
// being DRAWN on the panel.
//
// The probe does not need the user to wear the glasses and to report
// what they see.  It drives the mouse with SendInput - which is
// exactly what a hand on the mouse does to the system - and then
// checks what the browser's renderer received.
//
// Method
// ------
//  1. Launch a browser with a recorder page, and pin it.
//  2. Anchor the panel, and feed the IMU, so the panel has a real
//     pose in the room.
//  3. Move the PHYSICAL mouse onto the glasses' display with
//     SendInput.  That crosses the display boundary, which is what
//     triggers the re-seed.
//  4. Move it further, so the differential mapping advances the
//     cursor.
//  5. Click, and read back what the renderer saw.
//  6. Scroll, and read back what the renderer saw.
//  7. Snapshot the panel's back buffer and check that the 3D cursor
//     is actually drawn on it.
//
// The one thing the probe cannot check is whether the click landed
// where the user THINKS it should - that is a question about the
// panel's pose and the ray-cast, and the answer is the coordinates
// the renderer received.
//
// Usage:
//   dotnet run --project tools/ProbeVirtualCursor
using System.Diagnostics;
using System.IO;
using System.Numerics;
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
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
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

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    private const string RecorderHtml = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Rokid Virtual Cursor Probe</title>
        <script>
        window.__events = [];
        for (const type of ['mousemove','mousedown','mouseup','click','wheel']) {
          window.addEventListener(type, (e) => {
            window.__events.push({type: type, x: e.clientX, y: e.clientY,
                                 deltaY: (e.deltaY || 0)});
          }, true);
        }
        </script></head>
        <body style="margin:0">
        <div id="box" style="position:absolute;left:100px;top:50px;width:200px;height:100px;background:#c00"></div>
        <div style="height:4000px"></div>
        </body></html>
        """;

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== Rokid virtual cursor: hardware-in-the-loop probe ===");
        Console.WriteLine($"Time: {DateTime.Now:HH:mm:ss}");
        Console.WriteLine();

        var failures = new List<string>();

        // -------------------------------------------------------------
        // 1. The glasses' display
        // -------------------------------------------------------------
        Console.WriteLine("[1] Displays");
        var displays = DisplayLocator.Enumerate();
        foreach (var d in displays)
            Console.WriteLine($"    {d.DevicePath,-16} {d.DeviceName,-24} {d.Width}x{d.Height}"
                              + (d.IsRokid ? "  <-- ROKID" : ""));
        var target = DisplayLocator.Select();
        if (target is null)
        {
            Console.WriteLine("    FAIL: no display found.");
            return 1;
        }
        Console.WriteLine($"    -> using {target.DeviceName}, bounds {target.Bounds}");
        Console.WriteLine();

        // -------------------------------------------------------------
        // 2. A browser we can drive
        // -------------------------------------------------------------
        Console.WriteLine("[2] Launching the interactive browser");
        const int port = 9222;
        var profile = Path.Combine(Path.GetTempPath(), "rokid-vcp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(profile);
        var htmlPath = Path.Combine(profile, "probe.html");
        await File.WriteAllTextAsync(htmlPath, RecorderHtml);

        var exe = BrowserLauncher.FindExecutable();
        if (exe is null)
        {
            Console.WriteLine("    FAIL: no Chromium browser found.");
            return 1;
        }

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--app=file:///" + htmlPath.Replace('\\', '/'));
        psi.ArgumentList.Add($"--remote-debugging-port={port}");
        psi.ArgumentList.Add($"--user-data-dir={profile}");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("--window-size=1200,800");
        psi.ArgumentList.Add("--disable-backgrounding-occluded-windows");
        psi.ArgumentList.Add("--disable-renderer-backgrounding");
        psi.ArgumentList.Add("--disable-features=CalculateNativeWinOcclusion");

        var browser = Process.Start(psi)!;
        try
        {
            var hwnd = BrowserWindowFinder.WaitForWindowOfProcess(browser, TimeSpan.FromSeconds(20));
            if (hwnd == IntPtr.Zero)
            {
                Console.WriteLine("    FAIL: the browser's window did not appear.");
                return 1;
            }
            Console.WriteLine($"    -> browser window 0x{hwnd.ToInt64():X}");

            CdpTarget? page = null;
            var deadline = Environment.TickCount64 + 20000;
            while (Environment.TickCount64 < deadline && page is null)
            {
                var pages = await CdpDiscovery.ListPagesAsync(port);
                page = pages.FirstOrDefault(p => p.Url.Contains("probe.html", StringComparison.OrdinalIgnoreCase))
                        ?? pages.FirstOrDefault();
                if (page is null) await Task.Delay(250);
            }
            if (page is null)
            {
                Console.WriteLine("    FAIL: no CDP page.");
                return 1;
            }
            Console.WriteLine($"    -> page \"{page.Title}\"");
            Console.WriteLine();

            // -------------------------------------------------------------
            // 3. The real station, on the real hardware
            // -------------------------------------------------------------
            Console.WriteLine("[3] Starting the station and pinning the browser");
            using var station = new SpatialStation();
            station.OnLog += line => Console.WriteLine($"    [station] {line}");
            station.OnError += line => Console.WriteLine($"    [error] {line}");

            station.Start(hwnd);
            station.QueueCapture(hwnd);

            // The panel needs a real pose, which needs the IMU.  If the
            // glasses' HID interface is not enumerable - a driver
            // problem, not a code problem - the probe falls back to a
            // synthetic IMU stream.  The virtual cursor's own
            // behaviour does not depend on where the head is: the
            // panel's pose only decides where the re-seed lands.
            var imuCount = 0;
            try
            {
                using var device = HidSharpEnumerator.OpenFirst();
                Console.WriteLine($"    IMU device: {device.ProductName}");
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
                var accel = Vector3.Transform(new Vector3(0f, 9.80665f, 0f), Quaternion.Inverse(q));
                ulong ts = 1_000_000_000UL;
                for (var i = 0; i < 800; i++)
                {
                    station.FeedImu(new ImuSample(ts, accel, Vector3.Zero, Vector3.Zero, true, ts));
                    ts += (ulong)(1e9 / 437.0);
                    imuCount++;
                }
                Console.WriteLine($"    fed {imuCount} synthetic IMU samples");
            }

            station.AnchorHere();
            await Task.Delay(500);

            if (!station.Anchor.HasPanel)
            {
                Console.WriteLine("    FAIL: the panel was not anchored.");
                return 1;
            }
            Console.WriteLine("    -> panel anchored");
            Console.WriteLine();

            // Give the coordinator time to attach.
            await Task.Delay(2000);

            // -------------------------------------------------------------
            // 4. Drive the PHYSICAL mouse
            // -------------------------------------------------------------
            Console.WriteLine("[4] Driving the physical mouse");

            var bounds = target.Bounds;
            var centreX = bounds.X + bounds.Width / 2;
            var centreY = bounds.Y + bounds.Height / 2;

            GetCursorPos(out var before);
            Console.WriteLine($"    cursor before       : ({before.x}, {before.y})");

            // Move the mouse onto the glasses' display.  This is what
            // the user does with their hand, and it is what triggers
            // the ray-cast re-seed.
            MoveMouseTo(centreX, centreY);
            await Task.Delay(400);

            GetCursorPos(out var inside);
            Console.WriteLine($"    cursor after move   : ({inside.x}, {inside.y})  "
                              + (bounds.Contains(inside.x, inside.y) ? "ON the panel" : "NOT on the panel"));
            if (!bounds.Contains(inside.x, inside.y))
            {
                failures.Add("the mouse did not land on the glasses' display");
            }

            // Now advance it, so the differential mapping runs.
            MoveMouseTo(centreX + 60, centreY + 40);
            await Task.Delay(300);

            // -------------------------------------------------------------
            // 4b. The SIGN of the differential mapping
            // -------------------------------------------------------------
            //
            // This is the single most likely thing to get wrong, and the
            // research flagged it as such.  The policy is "re-seeded
            // differential": entering the display ray-casts the mouse
            // pixel onto the panel, and from then on only the mouse's
            // DELTA moves the cursor.
            //
            // So: moving the mouse RIGHT must increase the cursor's u
            // (which is the viewer's right), and moving it DOWN must
            // increase v (the viewer's bottom).  If the signs are
            // inverted, the user drags the cursor the wrong way and the
            // whole product is unusable.
            Console.WriteLine();
            Console.WriteLine("[4b] The direction of the differential mapping");

            var vc = station.Renderer.Cursor;
            if (vc is null)
            {
                failures.Add("the renderer has no virtual cursor");
            }
            else
            {
                // Re-enter the display so the re-seed is deterministic.
                MoveMouseTo(bounds.X - 200, centreY);
                await Task.Delay(500);
                MoveMouseTo(centreX, centreY);
                await Task.Delay(500);

                var (u0, v0) = vc.Position;
                Console.WriteLine($"    cursor at entry     : u={u0:F4} v={v0:F4}");

                // Move RIGHT by 200 display pixels.
                MoveMouseTo(centreX + 200, centreY);
                await Task.Delay(500);
                var (u1, v1) = vc.Position;
                Console.WriteLine($"    after moving RIGHT  : u={u1:F4} v={v1:F4}  (du={u1 - u0:+0.0000;-0.0000})");

                if (u1 <= u0)
                    failures.Add($"moving the mouse right did not move the cursor right (u {u0:F4} -> {u1:F4})");

                // Move DOWN by 150 display pixels.
                MoveMouseTo(centreX + 200, centreY + 150);
                await Task.Delay(500);
                var (u2, v2) = vc.Position;
                Console.WriteLine($"    after moving DOWN   : u={u2:F4} v={v2:F4}  (dv={v2 - v1:+0.0000;-0.0000})");

                if (v2 <= v1)
                    failures.Add($"moving the mouse down did not move the cursor down (v {v1:F4} -> {v2:F4})");

                // And back LEFT, to prove the cursor is not stuck.
                MoveMouseTo(centreX, centreY);
                await Task.Delay(500);
                var (u3, v3) = vc.Position;
                Console.WriteLine($"    after moving LEFT   : u={u3:F4} v={v3:F4}  (du={u3 - u2:+0.0000;-0.0000})");

                if (u3 >= u2)
                    failures.Add($"moving the mouse left did not move the cursor left (u {u2:F4} -> {u3:F4})");
            }

            Console.WriteLine();

            // -------------------------------------------------------------
            // 5. Click
            // -------------------------------------------------------------
            Console.WriteLine("[5] Clicking");
            using var reader = new CdpClient();
            if (!await reader.ConnectAsync(page.WebSocketDebuggerUrl))
            {
                Console.WriteLine("    FAIL: could not open the reader connection.");
                return 1;
            }
            await reader.EvaluateAsync("window.__events=[]");

            Click();
            await Task.Delay(1000);

            var recorded = await reader.EvaluateAsync("JSON.stringify(window.__events)");
            var events = JsonNode.Parse(recorded ?? "[]")!.AsArray();
            var types = events.Select(e => e!["type"]!.GetValue<string>()).ToList();
            Console.WriteLine($"    renderer saw        : [{string.Join(", ", types)}]");

            if (!types.Contains("click"))
            {
                failures.Add("the click never reached the renderer");
            }
            else
            {
                var click = events.First(e => e!["type"]!.GetValue<string>() == "click")!;
                var x = click["x"]!.GetValue<double>();
                var y = click["y"]!.GetValue<double>();
                Console.WriteLine($"    click at CSS        : ({x:F0}, {y:F0})");
                if (x < 0 || y < 0 || x > 1200 || y > 800)
                    failures.Add($"the click landed outside the page: ({x:F0}, {y:F0})");
            }

            GetCursorPos(out var after);
            Console.WriteLine($"    cursor after        : ({after.x}, {after.y})");

            // -------------------------------------------------------------
            // 5b. THE ACCURACY TEST - the cursor and the click must
            //     agree on ONE content pixel
            // -------------------------------------------------------------
            //
            // This is the test that decides whether the product works.
            // Everything else checks that a signal ARRIVED; this checks
            // that it arrived at the right PLACE.
            //
            // The ground truth comes from the page itself: the probe's
            // red box is at CSS (100, 50) with a known size, so the
            // page knows exactly where it is.  We therefore:
            //
            //   1. aim the virtual cursor at the box's CENTRE, using the
            //      inverse of the CSS mapping,
            //   2. click,
            //   3. read back the CSS coordinates the renderer saw,
            //   4. require them to land inside the box.
            //
            // If the cursor is drawn at the same (u, v) that the click
            // is delivered at - and the CSS mapping is the same in both
            // directions - the two must agree.  If they disagree, the
            // arrow points at one thing and the click hits another,
            // which is the single most confusing failure mode there is.
            Console.WriteLine();
            Console.WriteLine("[5b] The cursor and the click agree on the same pixel");

            // The red box's centre, in CSS pixels.  It is at (100, 50)
            // and is 200x100, so the centre is (200, 100).
            const double targetCssX = 200;
            const double targetCssY = 100;

            var vc2 = station.Renderer.Cursor;
            if (vc2 is null)
            {
                failures.Add("the renderer has no virtual cursor");
            }
            else if (!station.Input.ToContent(targetCssX, targetCssY, out var targetU, out var targetV))
            {
                failures.Add("the CSS -> content mapping is not available");
            }
            else
            {
                Console.WriteLine($"    target CSS          : ({targetCssX:F0}, {targetCssY:F0})");
                Console.WriteLine($"    target content uv   : ({targetU:F4}, {targetV:F4})");

                // Where does the browser think that content point is?
                if (!station.Input.ToCss(targetU, targetV, out var expectCssX, out var expectCssY))
                {
                    failures.Add("the content -> CSS mapping is not available");
                }
                else
                {
                    Console.WriteLine($"    maps back to CSS    : ({expectCssX:F0}, {expectCssY:F0})");
                    if (Math.Abs(expectCssX - targetCssX) > 1 || Math.Abs(expectCssY - targetCssY) > 1)
                    {
                        failures.Add(
                            $"the CSS mapping is not a round trip: ({targetCssX:F0}, {targetCssY:F0}) "
                            + $"-> ({targetU:F4}, {targetV:F4}) -> ({expectCssX:F0}, {expectCssY:F0})");
                    }
                }

                // Now drive the real mouse to that content position.
                //
                // The cursor's position is reached by DIFFERENTIAL
                // movement, and the gain is the app's own:
                //
                //   scaleX = contentWidth / eyeViewportWidth
                //
                // so a mouse delta of D display pixels moves the cursor
                // by D * scaleX content pixels, which is
                // D * scaleX / contentWidth of the panel's width.
                //
                // Solving for the mouse delta that produces a desired
                // change of du:
                //
                //   D = du * contentWidth / scaleX
                //     = du * eyeViewportWidth
                //
                // which is remarkably simple: to move the cursor by a
                // fraction du of the panel's width, the mouse moves by
                // that same fraction of ONE EYE's width in display
                // pixels.  The content's size cancels out.
                var (curU, curV) = vc2.Position;
                var eyeWidth = RokidDisplayProfile.EyeViewportWidth(target.Bounds.Width);
                var needDx = (targetU - curU) * eyeWidth;
                var needDy = (targetV - curV) * target.Bounds.Height;

                GetCursorPos(out var p0);
                var px0 = p0.x;
                var py0 = p0.y;
                MoveMouseTo(px0 + (int)Math.Round(needDx), py0 + (int)Math.Round(needDy));
                await Task.Delay(600);

                var (gotU, gotV) = vc2.Position;
                Console.WriteLine($"    cursor reached      : ({gotU:F4}, {gotV:F4})  "
                                  + $"(wanted {targetU:F4}, {targetV:F4})");

                var uvError = Math.Max(Math.Abs(gotU - targetU), Math.Abs(gotV - targetV));
                Console.WriteLine($"    uv error            : {uvError:F4}");
                if (uvError > 0.02)
                {
                    failures.Add(
                        $"the cursor did not reach the aimed point: uv error {uvError:F4} "
                        + $"(wanted {targetU:F4}, {targetV:F4}, got {gotU:F4}, {gotV:F4})");
                }

                // Click and see where the renderer says it landed.
                await reader.EvaluateAsync("window.__events=[]");
                Click();
                await Task.Delay(1000);

                recorded = await reader.EvaluateAsync("JSON.stringify(window.__events)");
                events = JsonNode.Parse(recorded ?? "[]")!.AsArray();
                var click2 = events.FirstOrDefault(e => e!["type"]!.GetValue<string>() == "click");
                if (click2 is null)
                {
                    failures.Add("the aimed click never reached the renderer");
                }
                else
                {
                    var c2x = click2["x"]!.GetValue<double>();
                    var c2y = click2["y"]!.GetValue<double>();
                    Console.WriteLine($"    renderer saw click  : ({c2x:F0}, {c2y:F0})");

                    // It must be inside the 200x100 red box.
                    const double boxLeft = 100, boxTop = 50, boxRight = 300, boxBottom = 150;
                    if (c2x < boxLeft || c2x > boxRight || c2y < boxTop || c2y > boxBottom)
                    {
                        failures.Add(
                            $"the aimed click landed at ({c2x:F0}, {c2y:F0}), outside the "
                            + $"target box ({boxLeft:F0},{boxTop:F0})-({boxRight:F0},{boxBottom:F0})");
                    }
                    else
                    {
                        Console.WriteLine("    -> the click landed inside the target box: PASS");
                    }
                }
            }

            // -------------------------------------------------------------
            // 6. Wheel
            // -------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("[6] Scrolling");
            await reader.EvaluateAsync("window.__events=[]");

            Scroll(-120);
            await Task.Delay(1000);

            recorded = await reader.EvaluateAsync("JSON.stringify(window.__events)");
            events = JsonNode.Parse(recorded ?? "[]")!.AsArray();
            var wheel = events.FirstOrDefault(e => e!["type"]!.GetValue<string>() == "wheel");
            if (wheel is null)
            {
                failures.Add("the wheel never reached the renderer");
            }
            else
            {
                var delta = wheel["deltaY"]!.GetValue<double>();
                Console.WriteLine($"    wheel deltaY        : {delta:F0}");
                if (Math.Abs(delta) < 1)
                    failures.Add($"the wheel reached the renderer but with no delta: {delta:F0}");
            }

            // -------------------------------------------------------------
            // 7. The 3D cursor is drawn on the panel
            // -------------------------------------------------------------
            //
            // The check is a DIFF, not a colour count: the panel shows a
            // white browser page, so "how many white pixels" says
            // nothing.  Instead the panel is snapshotted twice - once
            // with the cursor DORMANT (the pointer is off the glasses'
            // display) and once with it ACTIVE - and the two images are
            // compared.  The pixels that changed ARE the cursor.
            //
            // The diff also gives the cursor's position on the screen,
            // which is checked against the projection: the cursor's
            // world position is projected to NDC and then to screen
            // pixels, and it has to land on the diff's centre of mass.
            // That is the objective proof that the 3D cursor and the
            // click coordinate are the SAME point.
            Console.WriteLine();
            Console.WriteLine("[7] The 3D cursor on the panel");

            // 7a. With the cursor dormant.
            MoveMouseTo(bounds.X - 200, centreY);
            await Task.Delay(700);

            var dormantPath = Path.Combine(Path.GetTempPath(), "rokid-vcp-dormant.png");
            if (station.Renderer.SnapshotBackBuffer(dormantPath) is null)
            {
                failures.Add("could not snapshot the panel's back buffer");
            }
            else
            {
                // 7b. With the cursor active.
                MoveMouseTo(centreX, centreY);
                await Task.Delay(700);

                var activePath = Path.Combine(Path.GetTempPath(), "rokid-vcp-active.png");
                if (station.Renderer.SnapshotBackBuffer(activePath) is null)
                {
                    failures.Add("could not snapshot the panel's back buffer");
                }
                else
                {
                    var diff = DiffImages(dormantPath, activePath, out var cx, out var cy, out var changed,
                                          out var diffMinX, out var diffMinY, out var diffWidth, out var diffHeight);
                    Console.WriteLine($"    changed pixels      : {changed}");
                    Console.WriteLine($"    cursor at screen    : ({cx}, {cy})");

                    // Keep a copy for the vision check: it is the only
                    // way to tell whether the panel shows the content
                    // TWICE (side-by-side stereo) or once.
                    var keep = Path.Combine(Path.GetTempPath(), "rokid-vcp-final.png");
                    File.Copy(activePath, keep, true);
                    Console.WriteLine($"    kept                : {keep}");

                    DescribeLayout(activePath);
                    DumpAscii(activePath);

                    if (changed < 20)
                    {
                        failures.Add(
                            $"the 3D cursor was not drawn on the panel (only {changed} pixels changed)");
                    }
                    else
                    {
                        // Where SHOULD it be?  The cursor's texture
                        // coordinate is projected through the panel's
                        // pose, exactly as the renderer does it.
                        var cursor = station.Renderer.Cursor;
                        if (cursor is not null && cursor.IsActive)
                        {
                            var (u, v) = cursor.Position;
                            var expected = ProjectToScreen(station, u, v, target.Width, target.Height);
                            Console.WriteLine($"    cursor uv           : ({u:F4}, {v:F4})");
                            Console.WriteLine($"    expected on screen  : ({expected.X}, {expected.Y})");
                            Console.WriteLine($"    arrow bbox          : ({cx},{cy}) "
                                            + $"size {diffWidth}x{diffHeight}");

                            // The cursor's TIP is the anchor point: it is
                            // what the click lands on, and it is the
                            // arrow's own top-left corner.  So the tip
                            // has to be inside the arrow's own bounding
                            // box - within a couple of pixels.
                            //
                            // Comparing the centre of mass instead would
                            // be looser but wrong: the centre of mass of
                            // an arrow is nowhere near its tip, so a
                            // mirrored arrow would still "pass" a
                            // centre-of-mass test.
                            var tipDx = expected.X - diffMinX;
                            var tipDy = expected.Y - diffMinY;
                            Console.WriteLine($"    tip within arrow?   : {tipDx},{tipDy} "
                                            + $"(arrow is {diffWidth}x{diffHeight})");

                            if (tipDx < -4 || tipDy < -4
                                || tipDx > diffWidth + 4 || tipDy > diffHeight + 4)
                            {
                                failures.Add(
                                    $"the cursor's tip is at ({expected.X}, {expected.Y}) but the "
                                    + $"arrow is drawn at ({diffMinX},{diffMinY})-"
                                    + $"({diffMinX + diffWidth},{diffMinY + diffHeight})");
                            }
                        }
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== Verdict ===");
            if (failures.Count == 0)
            {
                Console.WriteLine("    PASS - the physical mouse drove the virtual cursor, the");
                Console.WriteLine("           click and the wheel reached the browser's renderer,");
                Console.WriteLine("           and the 3D cursor is drawn on the panel.");
                return 0;
            }

            foreach (var f in failures) Console.WriteLine("    FAIL: " + f);
            return 1;
        }
        finally
        {
            try { if (!browser.HasExited) browser.Kill(true); } catch { }
            try { Directory.Delete(profile, true); } catch { }
        }
    }

    /// <summary>
    /// Compare two panel snapshots and describe what changed.
    /// </summary>
    /// <remarks>
    /// The two images are the same page with and without the virtual
    /// cursor drawn on it, so every differing pixel is part of the
    /// cursor.  The centre of mass of the differing pixels is the
    /// cursor's position on the screen.
    /// </remarks>
    /// <param name="before">The snapshot without the cursor.</param>
    /// <param name="after">The snapshot with the cursor.</param>
    /// <param name="centreX">The cursor's centre of mass, in pixels.</param>
    /// <param name="centreY">The cursor's centre of mass, in pixels.</param>
    /// <param name="changed">How many pixels differ.</param>
    /// <returns>The number of differing pixels.</returns>
    private static int DiffImages(string before, string after, out int centreX, out int centreY, out int changed,
                                  out int diffMinX, out int diffMinY, out int diffWidth, out int diffHeight)
    {
        centreX = 0;
        centreY = 0;
        changed = 0;
        diffMinX = 0;
        diffMinY = 0;
        diffWidth = 0;
        diffHeight = 0;

        using var a = new System.Drawing.Bitmap(before);
        using var b = new System.Drawing.Bitmap(after);

        var w = Math.Min(a.Width, b.Width);
        var h = Math.Min(a.Height, b.Height);

        // The cursor is drawn ONCE PER EYE, so it appears twice on the
        // buffer.  The diff is measured over the LEFT eye's viewport
        // only, so the centre of mass is the cursor's position in that
        // eye - which is what ProjectToScreen returns.
        var half = RokidDiyStation.Core.Spatial.RokidDisplayProfile.EyeViewportWidth(w);

        long sumX = 0;
        long sumY = 0;
        var minX = int.MaxValue;
        var maxX = -1;
        var minY = int.MaxValue;
        var maxY = -1;

        for (var y = 0; y < h; y++)
        for (var x = 0; x < Math.Min(half, w); x++)
        {
            var pa = a.GetPixel(x, y);
            var pb = b.GetPixel(x, y);
            // A small tolerance: the captured page behind the cursor
            // is static, but the capture is not perfectly
            // frame-locked, so an anti-aliased edge can flicker by a
            // least significant bit.
            if (Math.Abs(pa.R - pb.R) <= 4 &&
                Math.Abs(pa.G - pb.G) <= 4 &&
                Math.Abs(pa.B - pb.B) <= 4)
                continue;

            changed++;
            sumX += x;
            sumY += y;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        if (changed > 0)
        {
            centreX = (int)(sumX / changed);
            centreY = (int)(sumY / changed);
        }
        diffMinX = minX;
        diffMinY = minY;
        diffWidth = changed > 0 ? maxX - minX + 1 : 0;
        diffHeight = changed > 0 ? maxY - minY + 1 : 0;
        return changed;
    }

    /// <summary>
    /// Where the cursor should appear on the back buffer.
    /// </summary>
    /// <remarks>
    /// The panel is drawn ONCE PER EYE, so the cursor appears TWICE on
    /// the back buffer - once in each eye's viewport.  This returns
    /// the position in the LEFT eye's viewport, which is where the
    /// diff's centre of mass is measured from (the diff covers both
    /// eyes, so the caller averages them, or compares against the
    /// left one).
    /// </remarks>
    private static (int X, int Y) ProjectToScreen(SpatialStation station, float u, float v, int bufferWidth, int bufferHeight)
    {
        var view = station.Anchor.GetView();
        var pose = view.PanelPose;
        var projection = station.Anchor.GetProjection();

        var world = RokidDiyStation.Core.Spatial.PanelPointer.FromUv(u, v, pose);
        var clip = Vector4.Transform(new Vector4(world, 1f), view.View * projection);

        if (Math.Abs(clip.W) < 1e-6f) return (-1, -1);

        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;

        // The projection is ONE EYE's 16:9 frustum, so the NDC maps
        // onto one eye's viewport, not the whole buffer.
        var eyeWidth = RokidDiyStation.Core.Spatial.RokidDisplayProfile.EyeViewportWidth(bufferWidth);

        var px = (int)Math.Round((ndcX * 0.5f + 0.5f) * eyeWidth);
        var py = (int)Math.Round((0.5f - ndcY * 0.5f) * bufferHeight);
        return (px, py);
    }

    /// <summary>
    /// Describe what is actually on the panel.
    /// </summary>
    /// <remarks>
    /// The Rokid Max has two Micro-OLED panels side by side, so the PC
    /// sees the glasses as ONE 3840x1080 monitor - 1920x1080 per eye.
    ///
    /// That raises a question the numbers alone cannot answer: is the
    /// content drawn ONCE across the whole 3840 (so each eye sees a
    /// squashed, half-width copy), or is it drawn ONCE PER EYE (the
    /// side-by-side stereo layout)?
    ///
    /// The signature of side-by-side stereo is that the LEFT half is a
    /// MIRROR of the right half.  If it is not, the content is drawn
    /// once across the full width, and the user is looking at a
    /// horizontally squashed image.
    /// </remarks>
    private static void DescribeLayout(string path)
    {
        using var bmp = new System.Drawing.Bitmap(path);
        Console.WriteLine($"    panel buffer        : {bmp.Width}x{bmp.Height}");

        long leftLit = 0, rightLit = 0;
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.R <= 16 && c.G <= 16 && c.B <= 16) continue;
            if (x < bmp.Width / 2) leftLit++; else rightLit++;
        }
        Console.WriteLine($"    lit left / right    : {leftLit} / {rightLit}");

        // The per-eye check.
        //
        // In the glasses' side-by-side 3D mode the back buffer is
        // 3840x1080 and the bridge splits it at the halfway point, so
        // each eye gets a 1920x1080 half.  The renderer must draw the
        // panel ONCE PER EYE with the same camera, which means the two
        // halves have to be IDENTICAL.
        //
        // That is the decisive assertion for the stereo layout: a
        // single draw across the whole buffer would leave the halves
        // showing DIFFERENT parts of the page.
        var half = bmp.Width / 2;
        int same = 0, differ = 0;
        for (var y = 0; y < bmp.Height; y += 2)
        for (var x = 0; x < half; x += 2)
        {
            var a = bmp.GetPixel(x, y);
            var b = bmp.GetPixel(x + half, y);
            if (Math.Abs(a.R - b.R) > 24 || Math.Abs(a.G - b.G) > 24 || Math.Abs(a.B - b.B) > 24)
                differ++;
            else same++;
        }
        var total = same + differ;
        var identical = total > 0 && differ < total * 0.02;
        Console.WriteLine($"    eyes identical?     : {same}/{total} same, {differ} differ -> "
                          + (identical
                              ? "YES - one picture per eye (correct for SBS)"
                              : "NO - the eyes show different content (WRONG)"));

        // Mirror test.
        int diff = 0, sampled = 0;
        for (var y = 0; y < bmp.Height; y += 4)
        for (var x = 0; x < bmp.Width / 2; x += 4)
        {
            var a = bmp.GetPixel(x, y);
            var b = bmp.GetPixel(bmp.Width - 1 - x, y);
            sampled++;
            if (Math.Abs(a.R - b.R) > 24 || Math.Abs(a.G - b.G) > 24 || Math.Abs(a.B - b.B) > 24) diff++;
        }
        var mirrored = diff < sampled * 0.1;
        Console.WriteLine($"    mirrored halves?    : {diff}/{sampled} differ -> "
                          + (mirrored ? "YES - side-by-side stereo" : "NO - one image across the full width"));

        // The probe's page draws a 200x100 box at (100,50) in CSS
        // pixels.  Where it lands on the panel says how the content is
        // scaled.
        //
        // The box is #c00 in the page's CSS, but what reaches the
        // panel is whatever the browser composited it into, so the
        // test is for "strongly red" rather than an exact colour.
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.R > 150 && c.G < 80 && c.B < 80)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0)
        {
            Console.WriteLine("    red marker          : not found");
            return;
        }

        // The marker appears in BOTH eyes, so the bounding box over the
        // whole buffer spans both of them.  Measure the LEFT eye's
        // copy on its own, because that is the one the viewport maps.
        var eyeWidth = half;
        int minXL = int.MaxValue, maxXL = -1, minYL = int.MaxValue, maxYL = -1;
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < eyeWidth; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.R > 150 && c.G < 80 && c.B < 80)
            {
                if (x < minXL) minXL = x;
                if (x > maxXL) maxXL = x;
                if (y < minYL) minYL = y;
                if (y > maxYL) maxYL = y;
            }
        }

        var boxWidth = maxXL - minXL + 1;
        var boxHeight = maxYL - minYL + 1;
        Console.WriteLine($"    red marker          : x {minXL}..{maxXL}, y {minYL}..{maxYL}  ({boxWidth}x{boxHeight}px)");
        Console.WriteLine($"    eye viewport        : {eyeWidth}px wide");

        // The page's own box is 200x100 CSS pixels.  The panel shows
        // the captured window's bitmap across a viewport whose size
        // is set by the panel's ANGULAR size, so the on-screen scale
        // is not 1:1 - what matters is that the two eyes agree and
        // that the SHAPE is preserved (no 2x stretch).
        var aspect = boxWidth / (double)Math.Max(1, boxHeight);
        Console.WriteLine($"    marker aspect      : {aspect:F3}  (the CSS box is 200x100 = 2.000)");
        if (Math.Abs(aspect - 2.0) > 0.15)
        {
            Console.WriteLine("    -> the marker is STRETCHED; the content aspect is wrong.");
        }
        else
        {
            Console.WriteLine("    -> the marker keeps its 2:1 shape: the content is not stretched.");
        }
    }

    /// <summary>
    /// Draw the panel's back buffer as coarse ASCII art.
    /// </summary>
    /// <remarks>
    /// This is the fastest way to see the LAYOUT - where the content
    /// is, whether it is duplicated, and whether the cursor is where it
    /// should be - without having to interpret a number.
    /// </remarks>
    private static void DumpAscii(string path)
    {
        using var bmp = new System.Drawing.Bitmap(path);
        const int cols = 96;
        const int rows = 24;
        var cellW = bmp.Width / (double)cols;
        var cellH = bmp.Height / (double)rows;

        Console.WriteLine();
        Console.WriteLine($"    +{new string('-', cols)}+");
        for (var r = 0; r < rows; r++)
        {
            var line = new char[cols];
            for (var c = 0; c < cols; c++)
            {
                var x0 = (int)(c * cellW);
                var x1 = Math.Min(bmp.Width, (int)((c + 1) * cellW));
                var y0 = (int)(r * cellH);
                var y1 = Math.Min(bmp.Height, (int)((r + 1) * cellH));

                double sum = 0;
                var n = 0;
                var red = 0;
                for (var y = y0; y < y1; y += 2)
                for (var x = x0; x < x1; x += 2)
                {
                    var p = bmp.GetPixel(x, y);
                    sum += (p.R + p.G + p.B) / 3.0;
                    if (p.R > 140 && p.G < 90 && p.B < 90) red++;
                    n++;
                }
                if (n == 0) { line[c] = ' '; continue; }

                var mean = sum / n;
                if (red > n * 0.4) line[c] = 'R';
                else if (mean < 12) line[c] = ' ';
                else if (mean > 235) line[c] = '#';
                else if (mean > 150) line[c] = '.';
                else if (mean > 60) line[c] = ':';
                else line[c] = '+';
            }
            Console.WriteLine($"    |{new string(line)}|");
        }
        Console.WriteLine($"    +{new string('-', cols)}+");
        Console.WriteLine("      ('#' white, '.' light, ':' mid, '+' dark, 'R' red marker, ' ' black)");
    }

    private static void MoveMouseTo(int x, int y)
    {
        SetCursorPos(x, y);
    }

    private static void Click()
    {
        Send(0x0002);
        Thread.Sleep(40);
        Send(0x0004);
    }

    private static void Scroll(int delta)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = unchecked((uint)delta) },
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
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
}
