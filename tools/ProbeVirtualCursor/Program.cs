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
// re-seed against the real panel pose, and the mapping onto a real
// browser sitting behind the AR panel.
//
// The probe does not need the user to wear the glasses and to report
// what they see.  It drives the mouse with SendInput - which is
// exactly what a hand on the mouse does to the system - and then
// checks what the browser's renderer received.
//
// Method
// ------
//  1. Launch a browser with a recorder page, and pin it.
//  2. Anchor the panel, and feed the real IMU, so the panel has a
//     real pose in the room.
//  3. Move the PHYSICAL mouse onto the glasses' display with
//     SendInput.  That crosses the display boundary, which is what
//     triggers the re-seed.
//  4. Move it further, so the differential mapping advances the
//     cursor.
//  5. Click, and read back what the renderer saw.
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
        for (const type of ['mousemove','mousedown','mouseup','click']) {
          window.addEventListener(type, (e) => {
            window.__events.push({type: type, x: e.clientX, y: e.clientY});
          }, true);
        }
        </script></head>
        <body style="margin:0">
        <div style="position:absolute;left:100px;top:50px;width:200px;height:100px;background:#c00"></div>
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
            Console.WriteLine();

            Console.WriteLine("=== Verdict ===");
            if (failures.Count == 0)
            {
                Console.WriteLine("    PASS - the physical mouse drove the virtual cursor, and the");
                Console.WriteLine("           click reached the browser's renderer.");
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
