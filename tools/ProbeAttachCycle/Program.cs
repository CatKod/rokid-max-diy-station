// Exercise the REAL attach/detach cycle of the shipping code.
//
// This is the end-to-end test the whole change was for:
//
//   1. the virtual monitor is published by the driver but NOT on the
//      desktop (that is the state after a reboot),
//   2. Attach() brings it onto the desktop at (-1920, 0),
//   3. Detach() takes it off again,
//   4. Attach() brings it back - which is the reversibility the
//      legacy API could not provide.
//
// Every step prints the desktop so a regression is visible.
using System;

using RokidDiyStation.App.Spatial;

internal static class Program
{
    private static int Main()
    {
        Dump("the initial state");

        Console.WriteLine();
        Console.WriteLine("=== attach ===");
        Console.WriteLine("  Attach(-1920, 0) = " + DisplayConfigController.Attach(-1920, 0));
        System.Threading.Thread.Sleep(2000);
        Dump("after the attach");

        Console.WriteLine();
        Console.WriteLine("=== detach ===");
        Console.WriteLine("  Detach() = " + DisplayConfigController.Detach());
        System.Threading.Thread.Sleep(2000);
        Dump("after the detach");

        Console.WriteLine();
        Console.WriteLine("=== attach again (the reversibility) ===");
        Console.WriteLine("  Attach(-1920, 0) = " + DisplayConfigController.Attach(-1920, 0));
        System.Threading.Thread.Sleep(2000);
        Dump("after the second attach");

        return 0;
    }

    private static void Dump(string label)
    {
        Console.WriteLine($"--- {label} ---");
        Console.WriteLine($"  IsVirtualActive() = {DisplayConfigController.IsVirtualActive()}");
        var found = DisplayConfigController.TryFindVirtual(out var path, out var w, out var h);
        Console.WriteLine($"  TryFindVirtual()  = {found}  {w}x{h}");
        if (found)
            Console.WriteLine($"    device = {DisplayConfigController.TargetPath(path.targetInfo.adapterId, path.targetInfo.id)}");

        foreach (var m in VirtualDisplayLocator.Enumerate())
        {
            Console.WriteLine($"    {m.DeviceName,-18} {m.Bounds.Width}x{m.Bounds.Height} @ ({m.Bounds.Left},{m.Bounds.Top}) " +
                              $"virtual={m.IsVirtual}");
        }
    }
}
