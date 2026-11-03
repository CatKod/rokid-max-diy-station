// Does the FIXED DisplayConfigController read this machine's topology?
//
// It compiles the real application source, so the answer is about the
// shipping code and not about a copy of it.
using System;

using RokidDiyStation.App.Spatial;

internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("IsAvailable()       = " + DisplayConfigController.IsAvailable());
        Console.WriteLine("IsVirtualActive()   = " + DisplayConfigController.IsVirtualActive());

        var found = DisplayConfigController.TryFindVirtual(out var path, out var w, out var h);
        Console.WriteLine($"TryFindVirtual()    = {found}  {w}x{h}");
        if (found)
        {
            Console.WriteLine($"  adapter   = {path.targetInfo.adapterId}");
            Console.WriteLine($"  target id = {path.targetInfo.id}");
            Console.WriteLine($"  device    = {DisplayConfigController.TargetPath(path.targetInfo.adapterId, path.targetInfo.id)}");
        }

        Console.WriteLine();
        Console.WriteLine("the monitors on the desktop:");
        foreach (var m in VirtualDisplayLocator.Enumerate())
        {
            Console.WriteLine($"  {m.DeviceName,-16} {m.Bounds.Width}x{m.Bounds.Height} @ ({m.Bounds.Left},{m.Bounds.Top}) " +
                              $"virtual={m.IsVirtual} id={m.DeviceId}");
        }

        return 0;
    }
}
