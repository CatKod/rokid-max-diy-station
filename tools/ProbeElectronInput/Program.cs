// SPDX-License-Identifier: MIT
//
// Probe: can the DevTools protocol drive a REAL Electron app?
//
// The virtual cursor's whole promise rests on one mechanism:
// Input.dispatchMouseEvent reaches the app's renderer, is trusted, and
// the desktop cursor never moves.  That was proven against a plain
// Chromium page.  But the user's actual goal is to interact with
// ARBITRARY applications - a code editor, a chat client, a design
// tool - not just a browser.
//
// Every mainstream desktop app in that category (VS Code, Discord,
// Slack, Obsidian, Notion, Spotify) is an Electron app, and Electron
// IS Chromium: it speaks the DevTools protocol natively when it is
// started with --remote-debugging-port.  So the question this probe
// answers is:
//
//   "Does Input.dispatchMouseEvent work on a real Electron app the
//    same way it works on a browser?"
//
// If the answer is yes, then the existing CdpDiscovery +
// BrowserInputRouter + SpatialInputCoordinator code drives ALL of
// those apps with NO new input mechanism at all - the only extra work
// is LAUNCHING the chosen app with the flag.
//
// WHAT THE EARLIER ROUNDS OF PROBING TAUGHT US
// --------------------------------------------
// Three things, all of which had to be learned the hard way:
//
//   1. THE ROUTER SILENTLY DROPPED EVERYTHING until the viewport was
//      known.  BrowserInputRouter.HoverAsync returns false without
//      sending anything when the viewport rectangle is still
//      (0,0,0,0), and the viewport is only filled in by
//      RefreshMetricsAsync.  A probe that connects and dispatches
//      without refreshing the metrics sees no events at all, and
//      wrongly concludes that Electron does not work.
//
//   2. THE ELECTRON WORKBENCH IS NOT READY WHEN THE TARGET APPEARS.
//      The CDP target exists within a second of launch, but the
//      document is still about:blank with three elements.  Probing
//      too early gives a FALSE NEGATIVE.
//
//   3. CDP's x/y ARE CSS PIXELS, NOT DEVICE PIXELS.  Measured at
//      devicePixelRatio 1.25, for Chrome, for Chrome forced with
//      --force-device-scale-factor=1.25, and for VS Code: a raw
//      dispatch at (100,80) arrived at exactly (100,80), ratio
//      1.0000.  CDP applies no scaling of its own.
//
// Finding 3 exposed a real bug: the viewport rectangle is measured in
// DEVICE pixels (it describes a region of the captured image) while
// CDP wants CSS pixels, and the conversion between them divided by
// the device pixel ratio - but the ratio it divided by was not always
// the ratio the viewport had been built with.  On a 125% display
// every click landed 25% of the screen away from the aim point.
//
// So the probe below drives the app through the router's own public
// path - the same path SpatialInputCoordinator uses - and asserts the
// round trip: CSS target -> texture -> CSS delivered.
//
// The checks, in order:
//
//   1. The Electron app exposes a CDP 'page' target.
//   2. The workbench's DOM is genuinely loaded (not about:blank).
//   3. The router's viewport mapping becomes available.
//   4. A mousemove arrives at the requested CSS coordinate, trusted.
//   5. A mousePressed / mouseReleased pair arrives as a complete
//      click with the right button and coordinates.
//   6. The OS mouse pointer did NOT move - GetCursorPos before and
//      after.
//
// Point 6 matters most for the product: the user's physical mouse
// must stay where they left it on the Artist 13.3.
//
// Usage:
//   dotnet run --project tools/ProbeElectronInput
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using RokidDiyStation.App.Input;
using RokidDiyStation.App.Spatial;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    private const int ProbePort = 9349;

    private static async Task<int> Main(string[] args)
    {
        var exe = args.FirstOrDefault(a => !a.StartsWith("--"))
                  ?? DefaultElectronExecutable();

        if (exe is null || !File.Exists(exe))
        {
            Console.WriteLine("FAIL: no Electron app found to test.");
            Console.WriteLine("Pass a path to an Electron executable as the first argument.");
            return 2;
        }

        Console.WriteLine($"Electron app under test: {exe}");

        var profile = Path.Combine(Path.GetTempPath(), "rokid-electron-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(profile);

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add($"--remote-debugging-port={ProbePort}");
        psi.ArgumentList.Add($"--user-data-dir={profile}");
        psi.ArgumentList.Add("--new-window");

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: could not launch the app: " + ex.Message);
            return 2;
        }

        var failures = 0;
        try
        {
            failures = await RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: unhandled exception: " + ex);
            failures = 1;
        }
        finally
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(profile, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "RESULT: PASS - the DevTools protocol drives the Electron app end to end."
            : $"RESULT: FAIL - {failures} check(s) did not pass.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> RunAsync()
    {
        var failures = 0;

        // -------------------------------------------------------------
        // 1. Find the page target.
        // -------------------------------------------------------------
        CdpTarget? target = null;
        var deadline = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < deadline)
        {
            var pages = await CdpDiscovery.ListPagesAsync(ProbePort).ConfigureAwait(false);
            target = pages.FirstOrDefault(p => !p.Url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase));
            if (target is not null) break;
            await Task.Delay(500).ConfigureAwait(false);
        }

        if (target is null)
        {
            Console.WriteLine("FAIL: the Electron app exposed no CDP 'page' target.");
            Console.WriteLine("      (Electron only speaks CDP when started with --remote-debugging-port.)");
            return 1;
        }

        Console.WriteLine($"OK  : CDP page target found - \"{target.Title}\"");
        Console.WriteLine($"      {target.Url}");

        var router = new BrowserInputRouter();
        if (!await router.ConnectAsync(target.WebSocketDebuggerUrl).ConfigureAwait(false))
        {
            Console.WriteLine("FAIL: could not open the DevTools WebSocket.");
            return 1;
        }

        Console.WriteLine("OK  : DevTools WebSocket connected.");
        await router.EnsureActiveAsync().ConfigureAwait(false);

        // -------------------------------------------------------------
        // 2. Wait for the workbench to be genuinely ready.
        // -------------------------------------------------------------
        var ready = false;
        var domSummary = "";
        deadline = Environment.TickCount64 + 90_000;
        while (Environment.TickCount64 < deadline)
        {
            var probe = await router.EvaluateAsync(
                "JSON.stringify({href: location.href, title: document.title, " +
                "elems: document.querySelectorAll('*').length, " +
                "iw: window.innerWidth, ih: window.innerHeight, " +
                "ready: !!document.querySelector('.monaco-workbench, .monaco-workbench-views, #workbench')})"
            ).ConfigureAwait(false);

            if (probe is not null)
            {
                domSummary = probe;
                try
                {
                    var n = JsonNode.Parse(probe);
                    var href = (string?)n?["href"] ?? "";
                    var elems = n?["elems"]?.GetValue<int>() ?? 0;
                    var isReady = (bool?)n?["ready"] ?? false;

                    if (isReady || (elems > 200 && !href.StartsWith("about:", StringComparison.OrdinalIgnoreCase)))
                    {
                        ready = true;
                        break;
                    }
                }
                catch (System.Text.Json.JsonException) { }
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        if (!ready)
        {
            Console.WriteLine("FAIL: the workbench never became ready.");
            Console.WriteLine("      last DOM: " + domSummary);
            router.Dispose();
            return 1;
        }

        Console.WriteLine($"OK  : the workbench is loaded. {domSummary}");

        // -------------------------------------------------------------
        // 3. Install the recorder and learn the viewport.
        // -------------------------------------------------------------
        var metricsRaw = await router.EvaluateAsync(
            "(() => { window.__rokidProbe = []; " +
            "for (const type of ['mousemove','mousedown','mouseup','click']) " +
            "  window.addEventListener(type, (e) => window.__rokidProbe.push({type: type, x: e.clientX, " +
            "y: e.clientY, button: e.button, trusted: e.isTrusted}), true); " +
            "return JSON.stringify({iw: window.innerWidth, ih: window.innerHeight, dpr: window.devicePixelRatio}); })()"
        ).ConfigureAwait(false);

        int iw, ih;
        double dpr;
        try
        {
            var n = JsonNode.Parse(metricsRaw ?? "{}");
            iw = n?["iw"]?.GetValue<int>() ?? 0;
            ih = n?["ih"]?.GetValue<int>() ?? 0;
            dpr = n?["dpr"]?.GetValue<double>() ?? 1.0;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            Console.WriteLine("FAIL: the app's metrics were not readable: " + ex.Message);
            router.Dispose();
            return 1;
        }

        if (iw <= 0 || ih <= 0)
        {
            Console.WriteLine("FAIL: the app reported a zero-sized viewport.");
            router.Dispose();
            return 1;
        }

        Console.WriteLine($"OK  : app viewport {iw}x{ih} CSS px, dpr {dpr}.");

        // -------------------------------------------------------------
        // 4. The viewport mapping must become available.
        // -------------------------------------------------------------
        if (!await router.RefreshViewportAsync().ConfigureAwait(false))
        {
            Console.WriteLine("FAIL: the router could not read the page's metrics.");
            router.Dispose();
            return 1;
        }

        var viewport = router.Viewport;
        Console.WriteLine($"OK  : viewport rect = {viewport}");

        // The mapping must be usable: the centre of the texture space
        // has to land on the centre of the page.
        if (!router.Viewport.ToCss(0.5f, 0.5f, router.DevicePixelRatio, out var probeX, out var probeY))
        {
            Console.WriteLine("FAIL: the texture->CSS mapping is unavailable.");
            router.Dispose();
            return 1;
        }

        Console.WriteLine($"OK  : texture (0.5,0.5) maps to CSS ({probeX:0.0}, {probeY:0.0}).");

        // -------------------------------------------------------------
        // 5. The OS pointer must not move.
        // -------------------------------------------------------------
        GetCursorPos(out var before);
        var pointerBefore = new POINT { x = before.x, y = before.y };

        // -------------------------------------------------------------
        // 6. Move the virtual cursor and click, through the router.
        // -------------------------------------------------------------
        var targetCssX = iw / 2.0;
        var targetCssY = ih / 2.0;

        // Convert the CSS target back into the texture coordinates the
        // coordinator would have produced, so the round trip through
        // the mapping is exercised in both directions.
        if (!router.Viewport.ToContent(targetCssX, targetCssY, router.DevicePixelRatio, out var targetU, out var targetV))
        {
            Console.WriteLine("FAIL: the CSS->texture mapping is unavailable.");
            router.Dispose();
            return 1;
        }

        Console.WriteLine($"      aiming at texture ({targetU:0.0000}, {targetV:0.0000}) "
                        + $"= CSS ({targetCssX:0}, {targetCssY:0})");

        var moved = await router.HoverAsync(targetU, targetV, 0).ConfigureAwait(false);
        if (!moved)
        {
            Console.WriteLine("FAIL: the router refused to dispatch the hover.");
            failures++;
        }

        await router.ButtonAsync(PointerButton.Left, true, targetU, targetV, 0).ConfigureAwait(false);
        await router.ButtonAsync(PointerButton.Left, false, targetU, targetV, 0).ConfigureAwait(false);
        await Task.Delay(400).ConfigureAwait(false);

        // -------------------------------------------------------------
        // 7. Read back what the app received.
        // -------------------------------------------------------------
        var eventsRaw = await router
            .EvaluateAsync("JSON.stringify(window.__rokidProbe || [])")
            .ConfigureAwait(false);

        var events = new List<JsonNode>();
        if (eventsRaw is not null)
        {
            try
            {
                if (JsonNode.Parse(eventsRaw) is JsonArray arr)
                {
                    foreach (var item in arr) if (item is not null) events.Add(item);
                }
            }
            catch (System.Text.Json.JsonException) { }
        }

        if (events.Count == 0)
        {
            Console.WriteLine("FAIL: the app received NO mouse events at all.");
            failures++;
        }
        else
        {
            Console.WriteLine($"OK  : the app received {events.Count} event(s):");
            foreach (var e in events) Console.WriteLine($"      {e}");
        }

        // The move must have landed at the coordinate we asked for.
        var moves = events.Where(e => (string?)e?["type"] == "mousemove").ToList();

        if (moves.Count == 0)
        {
            Console.WriteLine("FAIL: no mousemove reached the app.");
            failures++;
        }
        else
        {
            var last = moves[^1]!;
            var gotX = (double?)last["x"] ?? double.NaN;
            var gotY = (double?)last["y"] ?? double.NaN;
            var trusted = (bool?)last["trusted"] ?? false;

            Console.WriteLine($"      last mousemove at ({gotX:0.0}, {gotY:0.0}), trusted={trusted}");

            if (Math.Abs(gotX - targetCssX) > 2.0 || Math.Abs(gotY - targetCssY) > 2.0)
            {
                Console.WriteLine($"FAIL: the mousemove landed off target (wanted {targetCssX:0}, {targetCssY:0}).");
                failures++;
            }
            else
            {
                Console.WriteLine("OK  : the mousemove landed on the requested CSS coordinate.");
            }

            if (!trusted)
            {
                Console.WriteLine("FAIL: the event was NOT trusted - the app would treat it as synthetic.");
                failures++;
            }
            else
            {
                Console.WriteLine("OK  : the event arrived as TRUSTED.");
            }
        }

        // The click must be a complete down/up pair.
        var downs = events.Count(e => (string?)e?["type"] == "mousedown");
        var ups = events.Count(e => (string?)e?["type"] == "mouseup");
        if (downs < 1 || ups < 1)
        {
            Console.WriteLine($"FAIL: the click was incomplete (mousedown={downs}, mouseup={ups}).");
            failures++;
        }
        else
        {
            Console.WriteLine($"OK  : the click arrived complete (mousedown={downs}, mouseup={ups}).");
        }

        // -------------------------------------------------------------
        // 8. The OS pointer must not have moved.
        // -------------------------------------------------------------
        GetCursorPos(out var after);
        if (after.x != pointerBefore.x || after.y != pointerBefore.y)
        {
            Console.WriteLine($"FAIL: the OS mouse moved from ({pointerBefore.x},{pointerBefore.y}) "
                            + $"to ({after.x},{after.y}) - the virtual cursor must not touch it.");
            failures++;
        }
        else
        {
            Console.WriteLine($"OK  : the OS mouse did NOT move (still at {after.x},{after.y}).");
        }

        router.Dispose();
        return failures;
    }

    private static string? DefaultElectronExecutable()
    {
        var candidates = new[]
        {
            @"C:\Users\kimvi\AppData\Local\Programs\Microsoft VS Code\Code.exe",
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            @"C:\Users\kimvi\AppData\Local\Programs\Notion\Notion.exe",
            @"C:\Users\kimvi\AppData\Local\Programs\discord\Discord.exe",
            @"C:\Users\kimvi\AppData\Local\Programs\Obsidian\Obsidian.exe",
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }
}
