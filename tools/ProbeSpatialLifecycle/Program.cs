// The END-TO-END check of the spatial display's lifecycle, through the
// APP'S OWN code.
//
// This probe references the application assembly and calls
// VirtualDisplayController.Open() and .Close() - the exact methods
// the tray app calls - and then checks the desktop through the
// operating system, not through the app's own bookkeeping.
//
// The three claims the product rests on:
//
//   1. OPEN puts the virtual display on the desktop, at (-1920, 0),
//      and the virtual screen grows to include it;
//   2. CLOSE takes it off the desktop again, and the virtual screen
//      shrinks back;
//   3. and OPEN works AGAIN afterwards - the lifecycle is REVERSIBLE.
//
// The third is the one that matters most.  The legacy detach used to
// make it impossible, and a leftover virtual monitor is a trap: a
// window dragged onto it goes behind the glasses.
using System;
using System.Runtime.InteropServices;

using RokidDiyStation.App.Spatial;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int n);

    private static int Main()
    {
        Console.WriteLine("=== before ===");
        Console.WriteLine($"  the virtual screen is {GetSystemMetrics(78)}x{GetSystemMetrics(79)}");
        Console.WriteLine($"  IsAttached() = {VirtualDisplayController.IsAttached()}");

        for (int round = 1; round <= 3; round++)
        {
            Console.WriteLine();
            Console.WriteLine($"=== round {round}: OPEN ===");
            var opened = VirtualDisplayController.Open();
            Console.WriteLine($"  Open()          = {opened}");
            Console.WriteLine($"  IsAttached()    = {VirtualDisplayController.IsAttached()}");
            Console.WriteLine($"  the virtual screen is {GetSystemMetrics(78)}x{GetSystemMetrics(79)}");

            var monitor = VirtualDisplayLocator.FindVirtual();
            if (monitor is not null)
            {
                Console.WriteLine($"  the monitor     = {monitor.DeviceName} {monitor.Width}x{monitor.Height} at ({monitor.Bounds.Left},{monitor.Bounds.Top})");
                Console.WriteLine($"  it is virtual   = {monitor.IsVirtual}");
            }
            else
            {
                Console.WriteLine("  the monitor     = (not found)");
            }

            System.Threading.Thread.Sleep(1500);

            Console.WriteLine();
            Console.WriteLine($"=== round {round}: CLOSE ===");
            var closed = VirtualDisplayController.Close();
            Console.WriteLine($"  Close()         = {closed}");
            Console.WriteLine($"  IsAttached()    = {VirtualDisplayController.IsAttached()}");
            Console.WriteLine($"  the virtual screen is {GetSystemMetrics(78)}x{GetSystemMetrics(79)}");

            System.Threading.Thread.Sleep(1500);
        }

        Console.WriteLine();
        Console.WriteLine("=== after ===");
        Console.WriteLine($"  the virtual screen is {GetSystemMetrics(78)}x{GetSystemMetrics(79)}");
        Console.WriteLine($"  IsAttached() = {VirtualDisplayController.IsAttached()}");

        return 0;
    }
}
