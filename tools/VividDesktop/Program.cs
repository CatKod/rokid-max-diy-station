// SPDX-License-Identifier: MIT
//
// A stand-in "desktop" for README screenshots.
//
// The spatial display is a REAL Windows monitor at negative
// coordinates, so putting an ordinary window on it is enough to
// make the capture show something.  This tool shows a deliberately
// colourful, legible window there and holds it open, so
// tools/PanelSnapshot has something worth capturing.
//
// Usage:
//   dotnet run --project tools/VividDesktop
//   dotnet run --project tools/VividDesktop -- --width 1700 --height 980
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

internal static class Program
{
    private static int Main(string[] args)
    {
        var width = ArgInt(args, "--width", 1700);
        var height = ArgInt(args, "--height", 980);
        var left = ArgInt(args, "--left", -1900);
        var top = ArgInt(args, "--top", 40);

        ApplicationConfiguration.Initialize();

        Application.Run(new VividForm(width, height, left, top));
        return 0;
    }
    private static int ArgInt(string[] args, string name, int fallback)
    {
        var i = Array.IndexOf(args, name);
        if (i < 0 || i + 1 >= args.Length) return fallback;
        return int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;
    }
}

/// <summary>
/// A borderless window parked on the virtual display, painted with
/// enough colour and text to make a screenshot worth looking at.
/// </summary>
internal sealed class VividForm : Form
{
    private static readonly string[] Palette =
    {
        "#FF5252", "#FFB74D", "#FFD54F", "#81C784", "#4FC3F7", "#9575CD", "#F06292",
    };

    private readonly int _width;
    private readonly int _height;
    private readonly int _left;
    private readonly int _top;

    public VividForm(int width, int height, int left, int top)
    {
        _width = width;
        _height = height;
        _left = left;
        _top = top;

        Text = "Vivid desktop";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(left, top);
        ClientSize = new Size(width, height);
        BackColor = Color.FromArgb(238, 242, 248);
        DoubleBuffered = true;

        // Fill the WHOLE virtual monitor, so the screenshot does not
        // show the user's wallpaper behind the card.  A public README
        // should not leak someone's personal desktop.
        MaximumSize = new Size(width, height);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Windows does NOT necessarily keep a window where it was
        // created if that place is on another monitor: the shell will
        // pull it back to the primary display.  Asking again, after
        // the window exists and has been shown, is what actually
        // works.
        MoveToVirtualDisplay();
    }

    /// <summary>
    /// Push the window onto the virtual display, and keep pushing it
    /// there for a moment.
    /// </summary>
    /// <remarks>
    /// The move is repeated a few times over the first second because
    /// the shell can still reposition the window while it is being
    /// created and shown.
    /// </remarks>
    private void MoveToVirtualDisplay()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            NativeMethods.SetWindowPos(
                Handle, IntPtr.Zero, _left, _top, _width, _height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            Thread.Sleep(150);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // A LIGHT background, deliberately.  The panel's own background
        // is nearly black, and the Micro-OLED is not very bright, so a
        // dark screenshot reads as an empty frame.  A light one also
        // makes mirroring and stretching obvious, which a dark image
        // hides.
        g.Clear(Color.FromArgb(238, 242, 248));

        using (var brush = new SolidBrush(Color.FromArgb(15, 20, 35)))
        {
            g.DrawString(
                "Rokid Max · DIY Station",
                new Font("Segoe UI", 54, FontStyle.Bold),
                brush, 70, 60);
        }

        using (var brush = new SolidBrush(Color.FromArgb(70, 80, 100)))
        {
            g.DrawString(
                "A real Windows desktop, anchored in the room.",
                new Font("Segoe UI", 30),
                brush, 76, 150);
            g.DrawString(
                "Move your head — the panel stays where you put it.",
                new Font("Segoe UI", 30),
                brush, 76, 200);
        }

        // A row of colour chips, so a viewer can tell at a glance that
        // the image is not mirrored and not washed out.
        for (var i = 0; i < Palette.Length; i++)
        {
            using var brush = new SolidBrush(ColorTranslator.FromHtml(Palette[i]));
            g.FillEllipse(brush, 70 + (i * 230), 280, 190, 190);

            using var label = new SolidBrush(Color.FromArgb(60, 70, 90));
            g.DrawString(
                Palette[i],
                new Font("Consolas", 18),
                label, 76 + (i * 230), 480);
        }

        using (var brush = new SolidBrush(Color.FromArgb(25, 30, 45)))
        {
            g.DrawString(
                "Ctrl+Shift+Win+Alt+G   ·   bring the mouse back",
                new Font("Consolas", 26),
                brush, 76, 570);
        }

        // Three bars of different lengths, to make any mirroring or
        // stretching obvious.
        for (var i = 0; i < 3; i++)
        {
            using var brush = new SolidBrush(ColorTranslator.FromHtml(Palette[3 + i]));
            g.FillRectangle(brush, 76, 660 + (i * 50), 900 - (i * 250), 16);
        }

        using var mono = new Font("Consolas", 22);
        using var dim = new SolidBrush(Color.FromArgb(120, 130, 150));
        g.DrawString(
            "IMU 60Hz   ·   world-locked   ·   1920x1080 virtual display",
            mono, dim, 76, 830);
    }
}

/// <summary>
/// The handful of Win32 calls needed to park a window on another
/// monitor.
/// </summary>
internal static class NativeMethods
{
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
}
