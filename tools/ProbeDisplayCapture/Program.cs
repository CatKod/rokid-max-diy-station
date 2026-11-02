// Probe: does WGC capture the VIRTUAL monitor, and does the texture
// contain a real desktop?
//
// This is the Stage 1 verification.  It answers three questions that
// the research could only answer on paper:
//
//   1. Does CreateForMonitor succeed on an IddCx virtual display, or
//      does it hand back a black screen (the failure mode the research
//      flagged as possible)?
//   2. Does the captured texture have the monitor's pixel size?
//   3. Does it actually contain the desktop - i.e. is it a live image
//      and not a black rectangle?
//
// The capture is written to a PNG so the result can be looked at.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using RokidDiyStation.App.Spatial;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static class Program
{
    private static int Main(string[] args)
    {
        var outPath = args.Length > 0 ? args[0] : "virtual-display-capture.png";

        Console.WriteLine("=== virtual desktop ===");
        foreach (var m in VirtualDisplayLocator.Enumerate())
        {
            Console.WriteLine($"  {m.DeviceName,-28} {m.Width}x{m.Height} at ({m.Bounds.Left},{m.Bounds.Top})"
                            + $"  virtual={m.IsVirtual}  id={m.DeviceId}");
        }

        var target = VirtualDisplayLocator.FindVirtual();
        if (target is null)
        {
            Console.WriteLine("FAIL: no virtual display found.");
            return 1;
        }

        Console.WriteLine($"\ncapturing {target.DeviceName} ({target.Width}x{target.Height})");

        using var device = D3D11.D3D11CreateDevice(
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null);
        if (device is null)
        {
            Console.WriteLine("FAIL: D3D11CreateDevice returned nothing.");
            return 1;
        }

        using var capture = new DisplayCapture();
        try
        {
            capture.Start(target.Handle, device);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: CreateForMonitor threw: {ex.Message}");
            return 1;
        }

        Console.WriteLine("  capture started");

        using (var form = new Form
        {
            Text = "Rokid spatial display capture probe",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(target.Bounds.Left + 60, target.Bounds.Top + 60, 900, 500),
            BackColor = Color.FromArgb(20, 90, 160),
            TopMost = true,
        })
        {
            var label = new Label
            {
                Text = "SPATIAL DISPLAY",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 36, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(40, 40),
            };
            form.Controls.Add(label);
            form.Show();
            Application.DoEvents();

            // Let the compositor settle and the driver deliver a frame.
            for (int i = 0; i < 40; i++)
            {
                capture.TryUpdate();
                Application.DoEvents();
                System.Threading.Thread.Sleep(50);
            }

            var texture = capture.CapturedTexture;
            if (texture is null)
            {
                Console.WriteLine("FAIL: the capture produced no texture.");
                return 1;
            }

            Console.WriteLine($"  captured texture: {capture.Width}x{capture.Height}");

            using var bmp = Readback(device, texture, capture.Width, capture.Height);
            if (bmp is null)
            {
                Console.WriteLine("FAIL: could not read the captured texture back.");
                return 1;
            }

            bmp.Save(outPath, ImageFormat.Png);

            // Is it actually an image, or a black rectangle?
            var stats = Measure(bmp);
            Console.WriteLine($"  saved          : {Path.GetFullPath(outPath)}");
            Console.WriteLine($"  mean luma      : {stats.MeanLuma:F1}");
            Console.WriteLine($"  distinct colors: {stats.DistinctColors}");
            Console.WriteLine($"  brightest pixel: {stats.MaxLuma}");

            if (stats.MeanLuma < 3.0 && stats.DistinctColors <= 2)
            {
                Console.WriteLine("\nFAIL: the capture is black. The virtual monitor is not being composed.");
                return 1;
            }

            Console.WriteLine("\nPASS: the virtual monitor is captured and carries a live desktop.");
            return 0;
        }
    }

    private static (double MeanLuma, int DistinctColors, int MaxLuma) Measure(Bitmap bmp)
    {
        double sum = 0;
        int max = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        var step = Math.Max(1, bmp.Width / 320);

        for (int y = 0; y < bmp.Height; y += step)
        for (int x = 0; x < bmp.Width; x += step)
        {
            var p = bmp.GetPixel(x, y);
            var luma = 0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B;
            sum += luma;
            if (luma > max) max = (int)luma;
            seen.Add(p.ToArgb());
        }

        var count = (long)((bmp.Height + step - 1) / step) * ((bmp.Width + step - 1) / step);
        return (sum / Math.Max(1, count), seen.Count, max);
    }

    /// <summary>
    /// Copy the captured texture into a CPU bitmap so it can be saved.
    /// </summary>
    /// <remarks>
    /// The capture texture is BGRA (B8G8R8A8), so the channels are
    /// swapped on the way out.
    /// </remarks>
    private static unsafe Bitmap? Readback(
        ID3D11Device device, ID3D11Texture2D source, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription { Count = 1 },
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        using var staging = device.CreateTexture2D(desc);
        var context = device.ImmediateContext;
        context.CopyResource(staging, source);

        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var src = (byte*)mapped.DataPointer;

            for (int y = 0; y < height; y++)
            {
                var row = src + (long)y * mapped.RowPitch;
                for (int x = 0; x < width; x++)
                {
                    var i = x * 4;
                    bmp.SetPixel(x, y, Color.FromArgb(row[i + 3], row[i + 2], row[i + 1], row[i]));
                }
            }
            return bmp;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
