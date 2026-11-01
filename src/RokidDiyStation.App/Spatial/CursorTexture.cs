// SPDX-License-Identifier: MIT
//
// The virtual cursor's texture.
//
// Why a generated texture rather than a shipped PNG
// --------------------------------------------------
// The cursor has to be legible over ANY content, and the Rokid's
// Micro-OLED panels are ADDITIVE: they emit light and cannot project
// darkness.  "Black" on this display is not black - it is clear
// glass, and you see the room through it.
//
// So a plain white arrow vanishes over a bright web page, and a plain
// black arrow vanishes over the see-through room.  The only shape
// that survives both is a WHITE CORE with a SOLID BLACK OUTLINE:
//
//   * over bright content, the dark outline reads as a dark shape;
//   * over the transparent room, the white core glows.
//
// That is the same trick a hardware cursor uses, and it works here
// precisely BECAUSE the display is additive - the outline is emitted
// darkness, which on Micro-OLED means transparency, and that is
// exactly what separates the white core from bright content.
//
// The outline is drawn at SOURCE resolution and then the whole thing
// is filtered down by the GPU's sampler, so the border stays crisp
// enough to read at the panel's on-screen size.
using System.Drawing;
using System.Drawing.Drawing2D;

namespace RokidDiyStation.App.Spatial;

/// <summary>
/// Builds the cursor texture in code.
/// </summary>
public static class CursorTexture
{
    /// <summary>The texture's size in pixels, before any scaling.</summary>
    public const int Size = 64;

    /// <summary>
    /// Draw the standard arrow pointer: a white core with a 2-pixel
    /// solid black outline.
    /// </summary>
    public static Bitmap Create()
    {
        var bmp = new Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // The classic arrow outline, in a 64x64 box: a sharp tip at
        // the top-left, a notch at the shoulder, and a tail.
        var outline = new[]
        {
            new PointF(8f, 6f),
            new PointF(8f, 46f),
            new PointF(19f, 35f),
            new PointF(26f, 50f),
            new PointF(33f, 46f),
            new PointF(26f, 32f),
            new PointF(40f, 32f),
        };

        // The black outline, drawn slightly fatter than the core.
        using (var pen = new Pen(Color.Black, 7f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        })
        {
            g.DrawLines(pen, outline);
        }
        g.FillPolygon(Brushes.Black, outline);

        // The white core, inset by the outline's width.
        using (var pen = new Pen(Color.White, 3f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        })
        {
            g.DrawLines(pen, outline);
        }
        g.FillPolygon(Brushes.White, outline);

        return bmp;
    }
}
