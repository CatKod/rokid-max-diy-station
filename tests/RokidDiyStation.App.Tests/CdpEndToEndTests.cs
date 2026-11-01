// SPDX-License-Identifier: MIT
//
// End-to-end verification of the CDP input path.
//
// This is the test that answers the only question that matters for
// the product: does a synthetic mouse event actually reach the
// browser's renderer, and does it arrive as a TRUSTED event?
//
// The mechanism under test is Input.dispatchMouseEvent.  The browser
// builds a blink::WebMouseEvent from the parameters and forwards it
// to the renderer over Mojo IPC.  Crucially it does NOT go through
// the Win32 input queue, so:
//   * the browser does not need to be foreground,
//   * the browser does not need focus,
//   * the desktop cursor never moves.
//
// The last point is what makes the whole virtual-cursor design
// possible, and it is what this test proves.
//
// It is opt-in because it needs a real Chromium on the machine:
//
//   $env:ROKID_E2E = "1"; dotnet test --filter E2E
//
// Without the variable the test is skipped, so the ordinary test run
// stays hermetic.
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;
using RokidDiyStation.App.Input;

namespace RokidDiyStation.App.Tests;

public class CdpEndToEndTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("ROKID_E2E") == "1";

    /// <summary>
    /// A page that records what it receives.
    /// </summary>
    /// <remarks>
    /// The recorder lives in the page, so the assertion is made against
    /// what the RENDERER saw - not against what we sent.  That is the
    /// only meaningful assertion: it proves the event crossed the
    /// protocol, reached the renderer, and was trusted.
    ///
    /// The recorder is installed with a plain script tag in the page
    /// source, so it runs before any later script.
    /// </remarks>
    private const string RecorderHtml = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Rokid E2E Recorder</title>
        <script>
        window.__events = [];
        window.__trusted = [];
        for (const type of ['mousemove','mousedown','mouseup','click','wheel']) {
          window.addEventListener(type, (e) => {
            window.__events.push({type: type, x: e.clientX, y: e.clientY,
                                 button: e.button, deltaY: e.deltaY || 0});
            window.__trusted.push(e.isTrusted);
          }, true);
        }
        </script></head>
        <body style="margin:0">
        <div id="box" style="position:absolute;left:100px;top:50px;width:200px;height:100px;background:#c00">
        </div>
        <div style="height:4000px"></div>
        </body></html>
        """;

    [SkippableFact]
    public async Task AClickReachesTheRendererAsATrustedEvent()
    {
        Skip.IfNot(Enabled, "Set ROKID_E2E=1 to run the end-to-end CDP test.");

        // A dedicated port, so the test does not fight with a browser
        // the user (or the app) already has on the default port.
        const int port = 9333;

        // --- Launch a browser we can drive --------------------------
        var profile = Path.Combine(Path.GetTempPath(), "rokid-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        var htmlPath = Path.Combine(profile, "recorder.html");
        Directory.CreateDirectory(profile);
        await File.WriteAllTextAsync(htmlPath, RecorderHtml);

        var exe = BrowserLauncher.FindExecutable();
        Assert.NotNull(exe);

        var psi = new ProcessStartInfo(exe!)
        {
            UseShellExecute = false,
        };
        // The app mode URL is a file:// URL so no web server is needed.
        psi.ArgumentList.Add("--app=file:///" + htmlPath.Replace('\\', '/'));
        psi.ArgumentList.Add($"--remote-debugging-port={port}");
        psi.ArgumentList.Add($"--user-data-dir={profile}");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("--window-size=1200,800");
        // The SAME anti-throttling flags the app uses.  Without them
        // Chromium stops producing frames for an occluded window, and
        // Input.dispatchMouseEvent then BLOCKS waiting for the
        // renderer to acknowledge the event - the command never
        // returns, and the input pipeline stalls.
        psi.ArgumentList.Add("--disable-backgrounding-occluded-windows");
        psi.ArgumentList.Add("--disable-renderer-backgrounding");
        psi.ArgumentList.Add("--disable-features=CalculateNativeWinOcclusion");
        psi.ArgumentList.Add("--disable-background-timer-throttling");

        var process = Process.Start(psi);
        Assert.NotNull(process);

        try
        {
            // --- Find the page ---------------------------------------
            var page = await WaitForPageAsync(port, TimeSpan.FromSeconds(30));
            Assert.NotNull(page);

            // --- Connect ---------------------------------------------
            var router = new BrowserInputRouter();
            Assert.True(await router.ConnectAsync(page!.WebSocketDebuggerUrl));

            // --- Work out the page's geometry ------------------------
            // The SAME connection is used for the metrics and for the
            // assertions.  A browser allows only one client per
            // target, and a second connection would either be refused
            // or would steal the replies.
            var metrics = await ReadMetricsAsync(router);
            Assert.NotNull(metrics);

            // The capture is in PHYSICAL pixels, the page's metrics are
            // in CSS pixels, so the capture size is the CSS size times
            // the device pixel ratio.
            router.Geometry = new BrowserWindowGeometry
            {
                CaptureWidth = (int)Math.Round(metrics!.Value.Iw * metrics.Value.Dpr),
                CaptureHeight = (int)Math.Round(metrics.Value.Ih * metrics.Value.Dpr),
                DevicePixelRatio = metrics.Value.Dpr,
                WindowRect = new Spatial.NativeMethods.RECT
                {
                    left = 0, top = 0,
                    right = (int)Math.Round(metrics.Value.Iw * metrics.Value.Dpr),
                    bottom = (int)Math.Round(metrics.Value.Ih * metrics.Value.Dpr),
                },
                FrameRect = new Spatial.NativeMethods.RECT
                {
                    left = 0, top = 0,
                    right = (int)Math.Round(metrics.Value.Iw * metrics.Value.Dpr),
                    bottom = (int)Math.Round(metrics.Value.Ih * metrics.Value.Dpr),
                },
                ClientRect = new Spatial.NativeMethods.RECT
                {
                    left = 0, top = 0,
                    right = (int)Math.Round(metrics.Value.Iw * metrics.Value.Dpr),
                    bottom = (int)Math.Round(metrics.Value.Ih * metrics.Value.Dpr),
                },
            };
            Assert.True(await router.RefreshMetricsAsync());
            Assert.True(await router.EnsureActiveAsync());

            // --- Move, click, scroll --------------------------------
            // The capture covers the window's client area, which in
            // --app mode IS the viewport, so a texture coordinate maps
            // onto the page as (u * innerWidth, v * innerHeight) in CSS
            // pixels.  The red box sits at (100..300, 50..150), so the
            // target is its centre.
            var iw = metrics!.Value.Iw;
            var ih = metrics.Value.Ih;
            var targetU = (float)(200.0 / iw);
            var targetV = (float)(100.0 / ih);

            var vp = router.Viewport;
            var hoverOk = await router.HoverAsync(targetU, targetV, 0);
            Assert.True(hoverOk,
                $"hover failed. err={router.LastError ?? "<none>"} connected={router.IsConnected} "
                + $"viewport={vp.Left},{vp.Top},{vp.Width},{vp.Height} dpr={router.Geometry?.DevicePixelRatio} "
                + $"capture={router.Geometry?.CaptureWidth}x{router.Geometry?.CaptureHeight}");
            Assert.True(await router.ButtonAsync(
                Spatial.PointerButton.Left, true, targetU, targetV, 0), router.LastError);
            Assert.True(await router.ButtonAsync(
                Spatial.PointerButton.Left, false, targetU, targetV, 0), router.LastError);
            Assert.True(await router.ScrollAsync(120, 0), router.LastError);

            await Task.Delay(300);

            // --- Ask the page what it saw ----------------------------
            var recorded = await router.EvaluateAsync(
                "JSON.stringify(window.__events)");

            Assert.NotNull(recorded);
            var events = JsonNode.Parse(recorded!)!.AsArray();

            var types = events.Select(e => e!["type"]!.GetValue<string>()).ToList();
            Assert.Contains("mousemove", types);
            Assert.Contains("mousedown", types);
            Assert.Contains("mouseup", types);
            Assert.Contains("click", types);
            Assert.Contains("wheel", types);

            // The click must have landed INSIDE the red box.
            var click = events.First(e => e!["type"]!.GetValue<string>() == "click")!;
            var clickX = click["x"]!.GetValue<double>();
            var clickY = click["y"]!.GetValue<double>();
            Assert.InRange(clickX, 100, 300);
            Assert.InRange(clickY, 50, 150);

            // And it must be TRUSTED - that is the whole point of using
            // CDP instead of synthesising DOM events in the page.
            var trusted = await router.EvaluateAsync(
                "JSON.stringify(window.__trusted)");
            var trustedFlags = JsonNode.Parse(trusted!)!.AsArray()
                .Select(e => e!.GetValue<bool>()).ToList();
            Assert.NotEmpty(trustedFlags);
            Assert.DoesNotContain(false, trustedFlags);

            router.Dispose();
        }
        finally
        {
            try { if (!process!.HasExited) process.Kill(true); } catch { }
            try { Directory.Delete(profile, true); } catch { }
        }
    }


    [SkippableFact]
    public async Task TheDesktopCursorDoesNotMove()
    {
        Skip.IfNot(Enabled, "Set ROKID_E2E=1 to run the end-to-end CDP test.");

        const int port = 9334;

        var profile = Path.Combine(Path.GetTempPath(), "rokid-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        var htmlPath = Path.Combine(profile, "recorder.html");
        Directory.CreateDirectory(profile);
        await File.WriteAllTextAsync(htmlPath, RecorderHtml);

        var exe = BrowserLauncher.FindExecutable();
        Assert.NotNull(exe);

        var psi = new ProcessStartInfo(exe!) { UseShellExecute = false };
        psi.ArgumentList.Add("--app=file:///" + htmlPath.Replace('\\', '/'));
        psi.ArgumentList.Add($"--remote-debugging-port={port}");
        psi.ArgumentList.Add($"--user-data-dir={profile}");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");

        var process = Process.Start(psi);
        Assert.NotNull(process);

        try
        {
            var page = await WaitForPageAsync(port, TimeSpan.FromSeconds(30));
            Assert.NotNull(page);

            var router = new BrowserInputRouter();
            Assert.True(await router.ConnectAsync(page!.WebSocketDebuggerUrl));

            // Remember where the OS cursor is, drive the page hard, and
            // check that the OS cursor did not move.
            Assert.True(Spatial.NativeMethods.GetCursorPos(out var before));

            for (var i = 0; i < 20; i++)
            {
                await router.HoverAsync(0.1f + i * 0.03f, 0.5f, 0);
            }
            await router.ButtonAsync(Spatial.PointerButton.Left, true, 0.5f, 0.5f, 0);
            await router.ButtonAsync(Spatial.PointerButton.Left, false, 0.5f, 0.5f, 0);

            Assert.True(Spatial.NativeMethods.GetCursorPos(out var after));
            Assert.Equal(before.x, after.x);
            Assert.Equal(before.y, after.y);

            router.Dispose();
        }
        finally
        {
            try { if (!process!.HasExited) process.Kill(true); } catch { }
            try { Directory.Delete(profile, true); } catch { }
        }
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static async Task<CdpTarget?> WaitForPageAsync(int port, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            var pages = await CdpDiscovery.ListPagesAsync(port);
            var page = pages.FirstOrDefault(p => p.Url.Contains("recorder.html", StringComparison.OrdinalIgnoreCase))
                       ?? pages.FirstOrDefault();
            if (page is not null) return page;
            await Task.Delay(250);
        }
        return null;
    }

    private static async Task<(double Iw, double Ih, double Dpr)?> ReadMetricsAsync(BrowserInputRouter router)
    {
        var raw = await router.EvaluateAsync(
            "JSON.stringify({iw:window.innerWidth,ih:window.innerHeight,dpr:window.devicePixelRatio})");
        if (raw is null) return null;
        var node = JsonNode.Parse(raw)!.AsObject();
        return (
            node["iw"]!.GetValue<double>(),
            node["ih"]!.GetValue<double>(),
            node["dpr"]!.GetValue<double>());
    }

    /// <summary>
    /// Evaluate an expression in a page over a throwaway CDP connection.
    /// </summary>
    /// <remarks>
    /// The returned text is the value, already unescaped by the client.
    /// For the recorder expressions the value is a JSON string, so the
    /// inner text comes back ready to parse.
    /// </remarks>
    private static async Task<string?> EvaluateAsync(string wsUrl, string expression)
    {
        var client = new CdpClient();
        try
        {
            if (!await client.ConnectAsync(wsUrl)) return null;
            return await client.EvaluateAsync(expression);
        }
        finally
        {
            client.Dispose();
        }
    }
}
