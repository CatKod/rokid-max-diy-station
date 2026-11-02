// Probe: does the spatial pointer feedback land the 3D cursor exactly
// where the OS cursor is?
//
// This is the Stage 2 verification.  The spatial display's whole input
// model rests on one claim: the OS cursor's absolute position inside
// the virtual monitor's rectangle maps onto the panel's UV space
// exactly, with no gain, no scaling and no drift.
//
// So the probe does the obvious thing - moves the OS cursor to known
// points inside the virtual monitor, and checks that the 3D cursor
// lands on the same UV.
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using RokidDiyStation.App.Spatial;
using RokidDiyStation.Core.Spatial;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private static int Main()
    {
        var monitor = VirtualDisplayLocator.FindVirtual();
        if (monitor is null)
        {
            Console.WriteLine("FAIL: no virtual display found.");
            return 1;
        }

        Console.WriteLine($"virtual display : {monitor.DeviceName} {monitor.Width}x{monitor.Height} at ({monitor.Bounds.Left},{monitor.Bounds.Top})");

        var cursor = new VirtualCursor();
        var feedback = new SpatialPointerFeedback(cursor, () => monitor);

        var failures = 0;

        // A grid of sample points inside the monitor.
        var points = new[]
        {
            (x: 0.05, y: 0.05),
            (x: 0.50, y: 0.50),
            (x: 0.95, y: 0.95),
            (x: 0.25, y: 0.75),
            (x: 0.75, y: 0.25),
            (x: 0.01, y: 0.99),
        };

        Console.WriteLine();
        Console.WriteLine("  sample      expected uv      actual uv      error");
        Console.WriteLine("  ----------  ---------------  ---------------  ---------");

        foreach (var (fx, fy) in points)
        {
            // The exact pixel the OS cursor would land on for this UV.
            var px = monitor.Bounds.Left + (int)(fx * monitor.Width);
            var py = monitor.Bounds.Top + (int)(fy * monitor.Height);

            SetCursorPos(px, py);
            System.Threading.Thread.Sleep(120);

            if (!GetCursorPos(out var actual)) { Console.WriteLine("  (GetCursorPos failed)"); failures++; continue; }

            feedback.Update();

            var (u, v) = cursor.Position;
            var expectedU = (actual.X - monitor.Bounds.Left) / (float)monitor.Width;
            var expectedV = (actual.Y - monitor.Bounds.Top) / (float)monitor.Height;

            var err = Math.Max(Math.Abs(u - expectedU), Math.Abs(v - expectedV));

            Console.WriteLine($"  ({fx:0.00},{fy:0.00})  ({expectedU:0.0000},{expectedV:0.0000})  ({u:0.0000},{v:0.0000})  {err:0.000000}");

            if (err > 0.001) failures++;
        }

        // Outside the monitor, the cursor must go away.
        Console.WriteLine();
        SetCursorPos(monitor.Bounds.Right + 200, monitor.Bounds.Top + 200);
        System.Threading.Thread.Sleep(120);
        feedback.Update();
        Console.WriteLine($"  outside the monitor -> inside={feedback.IsInside}");
        if (feedback.IsInside) failures++;

        // And inside again.
        SetCursorPos(monitor.Bounds.Left + 960, monitor.Bounds.Top + 540);
        System.Threading.Thread.Sleep(120);
        feedback.Update();
        Console.WriteLine($"  back inside        -> inside={feedback.IsInside}");
        if (!feedback.IsInside) failures++;

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("PASS: the 3D cursor tracks the OS cursor exactly.");
            return 0;
        }

        Console.WriteLine($"FAIL: {failures} problem(s).");
        return 1;
    }
}
