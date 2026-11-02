// SPDX-License-Identifier: MIT
//
// Render the pinned panel and save what actually reaches the Micro-OLED
// as a PNG.
//
// The self-test already measures the back buffer numerically, but a
// number cannot tell you whether the captured desktop is MIRRORED
// left-to-right or UPSIDE DOWN — and those are exactly the kinds of
// bug that a 17% "coverage" reading reports as a pass.
//
// So this tool does what the self-test does, and then writes the
// back buffer out as an image that a human (or a vision model) can
// look at.
//
// Usage:
//   dotnet run --project tools/PanelSnapshot
//   dotnet run --project tools/PanelSnapshot -- panel.png
using System.Diagnostics;
using System.Numerics;
using RokidDiyStation.App.Spatial;
using RokidDiyStation.Core.HidCapture;
using RokidDiyStation.Core.Protocol;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static class Program
{
    private static int Main(string[] args)
    {
        var outPath = args.Length > 0 ? args[0] : "panel.png";

        Console.WriteLine("=== Rokid panel snapshot ===");

        // The panel shows the VIRTUAL DISPLAY - a real Windows
        // monitor - not a single window.  So there is nothing to pick
        // from a list: the tool opens the spatial display and captures
        // whatever is on it.
        var monitor = SpatialStation.FindVirtualDisplay();
        if (monitor is null)
        {
            Console.WriteLine("    the virtual display is not attached");
        }
        else
        {
            Console.WriteLine($"    virtual display: {monitor.DeviceName} {monitor.Width}x{monitor.Height} "
                            + $"at ({monitor.Bounds.Left},{monitor.Bounds.Top})");
        }

        using var station = new SpatialStation();
        station.OnLog += line => Console.WriteLine($"    [station] {line}");
        station.OnError += line => Console.WriteLine($"    [error] {line}");
        station.Start();

        if (!station.OpenSpatialDisplay())
        {
            Console.WriteLine("    FAIL: the virtual display could not be opened. "
                            + "Is the Virtual Display Driver installed?");
            return 1;
        }

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

        var (cw, ch) = station.CaptureSize;
        Console.WriteLine($"    captured size : {cw}x{ch}");
        Console.WriteLine($"    coverage      : {station.Renderer.MeasurePanelCoverage():P2}");

        // Dump the SOURCE capture alongside the panel snapshot, so the
        // two can be compared for orientation in the SAME run.  The
        // desktop changes between runs, so comparing a panel snapshot
        // against a separately-captured image proves nothing.
        var sourcePath = Path.ChangeExtension(outPath, ".source.png");
        if (WriteSource(station, sourcePath))
            Console.WriteLine($"    source written: {sourcePath}");
        return 0;
    }

    /// <summary>
    /// Read the captured frame back and save it as a PNG.
    /// </summary>
    private static unsafe bool WriteSource(SpatialStation station, string path)
    {
        var texture = station.CapturedTexture;
        if (texture is null) return false;

        var device = station.Renderer.Device;
        if (device is null) return false;

        var (w, h) = station.CaptureSize;
        if (w <= 0 || h <= 0) return false;

        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        using var staging = device.CreateTexture2D(desc);
        var context = device.ImmediateContext;
        context.CopyResource(staging, texture);

        var mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bytes = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                var row = (byte*)mapped.DataPointer + (long)y * mapped.RowPitch;
                for (var x = 0; x < w; x++)
                {
                    var p = row + (long)x * 4;
                    var o = (y * w + x) * 4;
                    bytes[o + 0] = p[0];  // B
                    bytes[o + 1] = p[1];  // G
                    bytes[o + 2] = p[2];  // R
                    bytes[o + 3] = 255;   // A
                }
            }

            var rect = new System.Drawing.Rectangle(0, 0, w, h);
            var bits = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try { System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bits.Scan0, bytes.Length); }
            finally { bmp.UnlockBits(bits); }

            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return true;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}


