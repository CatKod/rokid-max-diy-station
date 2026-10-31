// SPDX-License-Identifier: MIT
//
// Render the pinned panel and save what actually reaches the Micro-OLED
// as a PNG.
//
// The self-test already measures the back buffer numerically, but a
// number cannot tell you whether the captured window is MIRRORED
// left-to-right or UPSIDE DOWN — and those are exactly the kinds of
// bug that a 17% "coverage" reading reports as a pass.
//
// So this tool does what the self-test does, and then writes the
// back buffer out as an image that a human (or a vision model) can
// look at.
//
// Usage:
//   dotnet run --project tools/PanelSnapshot
using System.Diagnostics;
using System.Numerics;
using RokidDiyStation.App.Spatial;
using RokidDiyStation.Core.HidCapture;
using RokidDiyStation.Core.Protocol;

internal static class Program
{
    private static int Main(string[] args)
    {
        var outPath = args.Length > 0 ? args[0] : "panel.png";
        var filter = args.Length > 1 ? args[1] : null;

        Console.WriteLine("=== Rokid panel snapshot ===");

        var windows = WindowPicker.List();
        foreach (var w in windows.Take(10))
            Console.WriteLine($"    0x{w.Handle.ToInt64():X}  {w}");

        IntPtr source;
        if (filter is not null)
        {
            source = windows.FirstOrDefault(w => w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)).Handle;
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
        Console.WriteLine($"    capturing 0x{source.ToInt64():X}");

        using var station = new SpatialStation();
        station.OnLog += line => Console.WriteLine($"    [station] {line}");
        station.OnError += line => Console.WriteLine($"    [error] {line}");
        station.Start(source);

        // Feed the live IMU and anchor, exactly like the self-test.
        using var device = HidSharpEnumerator.OpenFirst();
        var sw = Stopwatch.StartNew();
        var n = 0;
        while (sw.Elapsed.TotalSeconds < 3.0 && n < 2000)
        {
            if (!device.TryRead(timeoutMs: 100, out var report)) continue;
            var parsed = PacketParser.Parse(report);
            if (!parsed.IsOk) continue;
            station.FeedImu(parsed.Value);
            n++;
        }
        Console.WriteLine($"    fed {n} IMU samples");
        station.AnchorHere();

        // Let the capture settle and the renderer produce frames.
        Thread.Sleep(2500);

        var path = station.Renderer.SnapshotBackBuffer(outPath);
        if (path is null)
        {
            Console.WriteLine("    FAIL: could not snapshot the back buffer.");
            return 1;
        }
        Console.WriteLine($"    wrote {path}");
        // Report the geometry alongside, so the image can be sanity
        // checked against the numbers.
        var view = station.Anchor.GetView();
        var corners = view.PanelCorners;
        if (corners.Length == 4)
        {
            var centre = (corners[0] + corners[2]) * 0.5f;
            Console.WriteLine($"    panel centre  : {centre}");
            Console.WriteLine($"    eye           : {view.EyePosition}");
            Console.WriteLine($"    distance      : {(centre - view.EyePosition).Length():F3} m");
            var (r, p, y) = RokidDiyStation.Core.Fusion.SyntheticImuSource.ToEulerZYX(view.HeadOrientation);
            Console.WriteLine($"    head euler    : R={r:F1} P={p:F1} Y={y:F1}");
        }

        Console.WriteLine($"    coverage      : {station.Renderer.MeasurePanelCoverage():P2}");
        return 0;
    }
}
