// SPDX-License-Identifier: MIT
//
// Which way up is the spatial panel?
//
// The panel samples the captured desktop through a UV transform, and
// the UVs are assigned per-vertex.  Getting them wrong mirrors the
// desktop left-to-right or flips it upside-down, and BOTH of those
// produce a perfectly healthy coverage number - so a number cannot
// catch them.
//
// The fix is an ASYMMETRIC marker: a big letter "F" with a bright
// block in its top-left corner.  Whatever the panel does to it is
// immediately visible.
//
// Usage:
//   dotnet run --project tools/PanelOrientation
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using RokidDiyStation.App.Spatial;
using RokidDiyStation.Core.HidCapture;
using RokidDiyStation.Core.Protocol;

internal static class Program
{
    private static int Main(string[] args)
    {
        var outPath = args.Length > 0 ? args[0] : "orientation.png";

        var monitor = SpatialStation.FindVirtualDisplay();
        if (monitor is null)
        {
            Console.WriteLine("FAIL: the virtual display is not attached.");
            return 1;
        }
        Console.WriteLine($"virtual display: {monitor.DeviceName} at ({monitor.Bounds.Left},{monitor.Bounds.Top})");

        // The marker, drawn ON the virtual display so it ends up in the
        // captured frame.
        using var form = new Form
        {
            Text = "orientation marker",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(monitor.Bounds.Left + 40, monitor.Bounds.Top + 40, 900, 600),
            BackColor = Color.FromArgb(20, 20, 60),
            TopMost = true,
            FormBorderStyle = FormBorderStyle.None,
        };
        var paint = new PaintEventHandler((s, e) =>
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(20, 20, 60));
            // Bright block in the TOP-LEFT corner.
            g.FillRectangle(new SolidBrush(Color.Red), 0, 0, 120, 120);
            // Big "F", so a flip is unmistakable.
            using var font = new Font("Arial", 420, FontStyle.Bold);
            using var brush = new SolidBrush(Color.White);
            g.DrawString("F", font, brush, 140, 20);
        });
        form.Paint += paint;
        form.Show();
        Application.DoEvents();

        using var station = new SpatialStation();
        station.OnLog += line => Console.WriteLine($"    [station] {line}");
        station.OnError += line => Console.WriteLine($"    [error] {line}");
        station.Start();

        if (!station.OpenSpatialDisplay())
        {
            Console.WriteLine("FAIL: could not open the spatial display.");
            return 1;
        }

        // Feed the IMU and anchor, so the panel has a pose.
        using var device = HidSharpEnumerator.OpenFirst();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var n = 0;
        while (sw.Elapsed.TotalSeconds < 3.0 && n < 2000)
        {
            if (!device.TryRead(timeoutMs: 100, out var report)) continue;
            var parsed = PacketParser.Parse(report);
            if (!parsed.IsOk) continue;
            station.FeedImu(parsed.Value);
            n++;
        }
        station.AnchorHere();

        // Let the marker render and the capture pick it up.
        for (int i = 0; i < 60; i++)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }

        var path = station.Renderer.SnapshotBackBuffer(outPath);
        if (path is null)
        {
            Console.WriteLine("FAIL: could not snapshot the back buffer.");
            return 1;
        }
        Console.WriteLine($"wrote {path}");
        Console.WriteLine($"coverage: {station.Renderer.MeasurePanelCoverage():P2}");

        // Where is the red block in the PANEL image?  Its position tells
        // us whether the panel is mirrored.
        using var bmp = new Bitmap(path);
        int bestX = -1, bestY = -1, bestScore = -1;
        for (int y = 0; y < bmp.Height; y += 4)
        for (int x = 0; x < bmp.Width; x += 4)
        {
            var c = bmp.GetPixel(x, y);
            // Reddish: high R, low G and B.
            if (c.R > 90 && c.G < 70 && c.B < 70)
            {
                var score = c.R - c.G - c.B;
                if (score > bestScore) { bestScore = score; bestX = x; bestY = y; }
            }
        }

        if (bestX < 0)
        {
            Console.WriteLine("FAIL: no red marker found in the panel image.");
            return 1;
        }

        // The panel occupies the middle of the 1920x1080 back buffer.
        var leftHalf = bestX < bmp.Width / 2;
        var topHalf = bestY < bmp.Height / 2;
        Console.WriteLine($"red marker at ({bestX},{bestY}) -> {(leftHalf ? "LEFT" : "RIGHT")} / {(topHalf ? "TOP" : "BOTTOM")}");

        // The marker is at the TOP-LEFT of the SOURCE desktop, so a
        // correct panel puts it in the top-left of the panel too.  The
        // panel's own UV mapping mirrors U on purpose (the panel's right
        // axis points to the viewer's left), so what "correct" means
        // here is what the user SEES: the desktop's left edge should
        // appear on the viewer's left.
        var ok = leftHalf && topHalf;
        Console.WriteLine(ok
            ? "PASS: the desktop reads the right way round on the panel."
            : "FAIL: the desktop is mirrored or flipped on the panel.");
        return ok ? 0 : 1;
    }
}
