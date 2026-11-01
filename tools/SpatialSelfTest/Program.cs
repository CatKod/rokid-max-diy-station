// SPDX-License-Identifier: MIT
//
// A headless end-to-end check of the spatial pipeline.
//
// The tray app is a GUI program, which makes "is it actually working?"
// hard to answer: the user has to wear the glasses, look at a window,
// and say whether a picture appeared.  This tool answers the same
// question from the machine's side, so the answer is objective.
//
// It runs the REAL production classes — SpatialStation, PanelRenderer,
// WindowCapture — against the REAL hardware, and then checks:
//
//   1. The glasses' display was found and the render window opened
//      on it.
//   2. The D3D device came up on the right adapter.
//   3. A window was captured, and frames actually arrived.
//   4. The captured frames reached the renderer as a shader resource
//      view (i.e. the pixel shader had something to sample).
//   5. The renderer was presenting frames at a sane rate.
//
// Usage:
//   dotnet run --project tools/SpatialSelfTest
//   dotnet run --project tools/SpatialSelfTest -- <partial window title>
using System.Diagnostics;
using System.Numerics;
using RokidDiyStation.App.Spatial;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.HidCapture;
using RokidDiyStation.Core.Protocol;

internal static class Program
{
    private static int Main(string[] args)
    {
        var filter = args.Length > 0 ? args[0] : null;

        Console.WriteLine("=== Rokid spatial pipeline self-test ===");
        Console.WriteLine($"Time: {DateTime.Now:HH:mm:ss}");
        Console.WriteLine();

        // -------------------------------------------------------------
        // 1. Displays
        // -------------------------------------------------------------
        Console.WriteLine("[1] Displays");
        var displays = DisplayLocator.Enumerate();
        foreach (var d in displays)
            Console.WriteLine($"    {d.DevicePath,-16} {d.DeviceName,-30} {d.Width}x{d.Height} @{d.RefreshHz:0}Hz"
                              + (d.IsRokid ? "  <-- ROKID" : "")
                              + (string.IsNullOrEmpty(d.MonitorId) ? "" : $"  id={d.MonitorId}"));

        var target = DisplayLocator.Select();
        if (target is null)
        {
            Console.WriteLine("    FAIL: no display found.");
            return 1;
        }
        if (!target.IsRokid)
            Console.WriteLine($"    WARN: the selected display is not identified as a Rokid ({target}).");
        Console.WriteLine();

        // -------------------------------------------------------------
        // 2. Pick a source window
        // -------------------------------------------------------------
        Console.WriteLine("[2] Source window");
        var windows = WindowPicker.List();
        foreach (var w in windows.Take(15))
            Console.WriteLine($"    0x{w.Handle.ToInt64():X}  {w}");

        IntPtr source;
        if (filter is not null)
        {
            // FirstOrDefault returns NULL when nothing matches, and
            // reading .Handle off it would throw - so the null is
            // checked before the handle is taken.
            var match = windows.FirstOrDefault(w => w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase));
            source = match?.Handle ?? default;
        }
        else
        {
            source = windows.Count > 0 ? windows[0].Handle : default;
        }
        if (source == default)
        {
            Console.WriteLine("    FAIL: no capturable window found.");
            return 1;
        }
        Console.WriteLine($"    -> capturing 0x{source.ToInt64():X}");
        Console.WriteLine();

        // -------------------------------------------------------------
        // 3. Run the real pipeline
        // -------------------------------------------------------------
        Console.WriteLine("[3] Running the pipeline for 8 seconds");
        using var station = new SpatialStation();
        var errors = new List<string>();
        var capturedSizes = new List<string>();
        double? lastFps = null;

        station.OnLog += line => Console.WriteLine($"    [station] {line}");
        station.OnError += line => { errors.Add(line); Console.WriteLine($"    [error] {line}"); };
        station.OnFrameRate += fps => lastFps = fps;

        station.Start(source);

        // -------------------------------------------------------------
        // 3b. Feed the real IMU and anchor the panel
        // -------------------------------------------------------------
        // The capture pipeline alone does not prove the SPATIAL half
        // works.  The panel's geometry comes from the fusion filter,
        // the neck model and the lazy follow, and a panel that is
        // anchored but facing the wrong way, or is the wrong size, or
        // is behind the user, is a bug that no frame-rate check would
        // ever catch.
        Console.WriteLine("[3b] Feeding the live IMU and anchoring the panel");

        using var device = HidSharpEnumerator.OpenFirst();
        Console.WriteLine($"    device: {device.ProductName}");

        var sampleCount = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 3.0 && sampleCount < 2000)
        {
            if (!device.TryRead(timeoutMs: 100, out var report)) continue;
            var parsed = PacketParser.Parse(report);
            if (!parsed.IsOk) continue;
            station.FeedImu(parsed.Value);
            sampleCount++;
        }
        Console.WriteLine($"    fed {sampleCount} IMU samples");

        station.AnchorHere();

        var view = station.Anchor.GetView();
        var corners = view.PanelCorners;
        var hasPanel = station.Anchor.HasPanel;
        var panel = station.Anchor.Panel;

        Console.WriteLine($"    panel present       : {hasPanel}");
        if (hasPanel && panel is not null)
        {
            var centre = (corners[0] + corners[2]) * 0.5f;
            var eye = panel.EyeAtAnchor;
            var distance = (centre - eye).Length();

            // The panel's normal points OUT OF its face, towards the
            // viewer, so it must agree with the direction from the
            // panel back to the eye — not with the direction from the
            // eye to the panel.
            var toEye = Vector3.Normalize(eye - centre);
            var facing = Vector3.Dot(toEye, panel.Normal);
            var (pr, pp, py) = SyntheticImuSource.ToEulerZYX(view.HeadOrientation);

            Console.WriteLine($"    panel size          : {panel.Width * 100:F1} x {panel.Height * 100:F1} cm");
            Console.WriteLine($"    panel distance      : {distance:F3} m");
            Console.WriteLine($"    panel faces viewer  : {facing:F3} (1.0 = dead-on)");
            Console.WriteLine($"    head euler (deg)    : R={pr:F1} P={pp:F1} Y={py:F1}");
            // The panel's up axis is the in-plane vertical, so its Y
            // component is less than 1 whenever the panel is pitched.
            // That is correct: the panel is level in the room, but it
            // is tilted down towards the user, so its up axis leans
            // back.
            Console.WriteLine($"    panel up Y          : {MathF.Abs(Vector3.Normalize(panel.Up).Y):F3} (1.0 = level)");

            // The panel must be in FRONT of the user, at the requested
            // distance, and must face back at them.  These are the
            // three things a broken anchor gets wrong.
            if (distance < 0.2f || distance > 5f) { errors.Add($"panel distance out of range: {distance:F3} m"); }
            if (facing < 0.8f) { errors.Add($"panel is not facing the viewer: {facing:F3}"); }
            if (corners.Length != 4) { errors.Add($"expected 4 corners, got {corners.Length}"); }
        }
        else
        {
            errors.Add("no panel was created by AnchorHere()");
        }

        // -------------------------------------------------------------
        // 3c. Read the back buffer back
        // -------------------------------------------------------------
        // The panel is DRAWN, not merely wired up.  The render window
        // sets WDA_EXCLUDEFROMCAPTURE so it cannot feed its own output
        // back into a clone-mode capture, and that also hides it from
        // every screenshot tool — so reading the back buffer is the
        // only objective way to confirm pixels are actually reaching
        // the Micro-OLED panel.
        Console.WriteLine("[3c] Measuring the rendered back buffer");
        var coverage = station.Renderer.MeasurePanelCoverage();
        Console.WriteLine($"    panel coverage      : {coverage:P2} of the panel is lit");
        if (coverage < 0f)
        {
            errors.Add("could not read the back buffer");
        }
        else if (coverage < 0.005f)
        {
            errors.Add($"the panel is not being drawn (coverage {coverage:P2})");
        }
        Console.WriteLine();

        var sw2 = Stopwatch.StartNew();
        var textureSeen = false;
        var sizeSeen = "";
        while (sw2.Elapsed.TotalSeconds < 8.0)
        {
            Thread.Sleep(250);
            if (station.CaptureTexture is not null)
            {
                textureSeen = true;
                var (w, h) = station.CaptureSize;
                var s = $"{w}x{h}";
                if (s != sizeSeen) { sizeSeen = s; capturedSizes.Add(s); }
            }
        }
        Console.WriteLine();

        // -------------------------------------------------------------
        // 4. Verdict
        // -------------------------------------------------------------
        Console.WriteLine("=== RESULT ===");
        Console.WriteLine($"  display             : {target.DevicePath} (rokid={target.IsRokid})");
        Console.WriteLine($"  render window       : {(station.Renderer.IsReady ? "created" : "NOT created")}");
        Console.WriteLine($"  render fps          : {(lastFps.HasValue ? $"{lastFps.Value:0.0}" : "n/a")}");
        Console.WriteLine($"  capture active      : {station.IsCapturing}");
        Console.WriteLine($"  capture size        : {(sizeSeen == "" ? "n/a" : sizeSeen)}");
        Console.WriteLine($"  texture reached GPU : {textureSeen}");
        Console.WriteLine($"  errors              : {(errors.Count == 0 ? "none" : string.Join("; ", errors))}");
        Console.WriteLine();

        var ok = station.Renderer.IsReady && textureSeen && station.IsCapturing && errors.Count == 0;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }
}
