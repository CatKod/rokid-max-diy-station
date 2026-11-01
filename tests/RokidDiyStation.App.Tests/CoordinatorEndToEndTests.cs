// SPDX-License-Identifier: MIT
//
// End-to-end verification of the WHOLE input path, through the
// coordinator - the same object the app uses at runtime.
//
// The lower-level test in CdpEndToEndTests proves that a CDP command
// reaches the renderer.  This one proves that the app's own chain
// does:
//
//   SpatialInputCoordinator.CursorMoved / Deliver
//     -> BrowserInputRouter (u, v -> CSS pixels)
//     -> CDP Input.dispatchMouseEvent
//     -> the browser's renderer
//
// The difference matters: the coordinator has a worker thread, a
// queue, a coalescing hover and a geometry refresh, and each of those
// is a place where the delivery could quietly break.
//
// It is opt-in because it needs a real Chromium on the machine:
//
//   $env:ROKID_E2E = "1"; dotnet test --filter Coordinator
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;
using RokidDiyStation.App.Input;
using RokidDiyStation.App.Spatial;

namespace RokidDiyStation.App.Tests;

public class CoordinatorEndToEndTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("ROKID_E2E") == "1";

    private const string RecorderHtml = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Rokid Coordinator Recorder</title>
        <script>
        window.__events = [];
        window.__trusted = [];
        for (const type of ['mousemove','mousedown','mouseup','click','wheel']) {
          window.addEventListener(type, (e) => {
            window.__events.push({type: type, x: e.clientX, y: e.clientY,
                                 deltaY: e.deltaY || 0});
            window.__trusted.push(e.isTrusted);
          }, true);
        }
        </script></head>
        <body style="margin:0">
        <div id="box" style="position:absolute;left:100px;top:50px;width:200px;height:100px;background:#c00"></div>
        <div style="height:4000px"></div>
        </body></html>
        """;

    [SkippableFact]
    public async Task TheCoordinatorDeliversAClickAndAWheelToTheRenderer()
    {
        Skip.IfNot(Enabled, "Set ROKID_E2E=1 to run the end-to-end coordinator test.");

        const int port = 9336;

        var profile = Path.Combine(Path.GetTempPath(), "rokid-coord-" + Guid.NewGuid().ToString("N")[..8]);
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
        psi.ArgumentList.Add("--window-size=1200,800");

        var process = Process.Start(psi);
        Assert.NotNull(process);

        using var coordinator = new SpatialInputCoordinator(() => (1200, 800));
        coordinator.Port = port;
        var log = new List<string>();
        coordinator.OnLog += line => log.Add(line);

        try
        {
            // --- Wait for the page ----------------------------------
            CdpTarget? page = null;
            var deadline = Environment.TickCount64 + 30000;
            while (Environment.TickCount64 < deadline && page is null)
            {
                var pages = await CdpDiscovery.ListPagesAsync(port);
                page = pages.FirstOrDefault(p => p.Url.Contains("recorder.html", StringComparison.OrdinalIgnoreCase))
                        ?? pages.FirstOrDefault();
                if (page is null) await Task.Delay(250);
            }
            Assert.NotNull(page);

            // --- Attach, the way the app does ----------------------
            coordinator.Start();
            var attached = await coordinator.AttachAsync(IntPtr.Zero, page!.Title, page.Url);
            Assert.True(attached, "the coordinator did not attach to the page: " + string.Join(" | ", log)
                + " title=" + page!.Title + " url=" + page.Url
                + " pages=" + (await CdpDiscovery.ListPagesAsync(port)).Count
                + " port=" + coordinator.Port);
            Assert.True(coordinator.IsInteractive);

            // The coordinator talks to the browser over the same
            // connection, so the page's own recorder is read back
            // through a second client - which is fine, because the
            // browser allows more than one client as long as they do
            // not drive the same target's input.
            using var reader = new CdpClient();
            Assert.True(await reader.ConnectAsync(page.WebSocketDebuggerUrl));
            await reader.EvaluateAsync("window.__events=[];window.__trusted=[]");

            // --- Move the virtual cursor onto the red box ----------
            // The page's viewport is 1200x800, and the red box's
            // centre is at (200, 100), so the texture coordinate is
            // (200/1200, 100/800).
            coordinator.CursorMoved(200f / 1200f, 100f / 800f);
            await Task.Delay(300);

            // --- Click through the coordinator ----------------------
            coordinator.Deliver(
                new PointerEvent(PointerButton.Left, true, 0, false, false, false),
                200f / 1200f, 100f / 800f);
            coordinator.Deliver(
                new PointerEvent(PointerButton.Left, false, 0, false, false, false),
                200f / 1200f, 100f / 800f);

            // --- Wheel through the coordinator ---------------------
            coordinator.Deliver(
                new PointerEvent(PointerButton.Middle, false, 120, false, false, false),
                200f / 1200f, 100f / 800f);

            await Task.Delay(800);

            // --- What did the renderer see? -------------------------
            var recorded = await reader.EvaluateAsync("JSON.stringify(window.__events)");
            Assert.NotNull(recorded);
            var events = JsonNode.Parse(recorded!)!.AsArray();

            var types = events.Select(e => e!["type"]!.GetValue<string>()).ToList();
            Assert.Contains("mousemove", types);
            Assert.Contains("mousedown", types);
            Assert.Contains("mouseup", types);
            Assert.Contains("click", types);
            Assert.Contains("wheel", types);

            // The click landed inside the red box.
            var click = events.First(e => e!["type"]!.GetValue<string>() == "click")!;
            Assert.InRange(click["x"]!.GetValue<double>(), 100, 300);
            Assert.InRange(click["y"]!.GetValue<double>(), 50, 150);

            // The wheel scrolled the page.
            var scrollY = await reader.EvaluateAsync("window.scrollY");
            Assert.True(double.TryParse(scrollY, out var scrolled) && scrolled > 0,
                "the wheel did not scroll the page, scrollY=" + scrollY);

            // And everything was trusted.
            var trusted = await reader.EvaluateAsync("JSON.stringify(window.__trusted)");
            var trustedFlags = JsonNode.Parse(trusted!)!.AsArray()
                .Select(e => e!.GetValue<bool>()).ToList();
            Assert.NotEmpty(trustedFlags);
            Assert.DoesNotContain(false, trustedFlags);
        }
        finally
        {
            try { if (!process!.HasExited) process.Kill(true); } catch { }
            try { Directory.Delete(profile, true); } catch { }
        }
    }
}
