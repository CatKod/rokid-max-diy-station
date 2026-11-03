// Why does DisplayConfigController.Query() fail on this machine even
// though the raw size call succeeds?
//
// The size call reports 56 paths / 112 modes / 0 targets / 0 sources for
// QDC_DATABASE_CURRENT, and the controller over-reserves to 64 / 128 /
// 128 / 128 before calling QueryDisplayConfig.  The query then fails.
//
// This probe calls QueryDisplayConfig directly with several different
// capacity combinations to find the one the API actually accepts.
using System;
using System.Runtime.InteropServices;

using RokidDiyStation.App.Spatial;

internal static class Program
{
    private const uint QDC_DATABASE_CURRENT = 0x00000001;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000004;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint pathCount, out uint modeCount, out uint targetCount, out uint sourceCount);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathInfoArraySize,
        [Out] DisplayConfigController.DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint modeInfoArraySize,
        [Out] DisplayConfigController.DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        ref uint targetInfoArraySize,
        [Out] DisplayConfigController.DISPLAYCONFIG_TARGET_DEVICE_NAME[] targetInfoArray,
        ref uint sourceInfoArraySize,
        [Out] DisplayConfigController.DISPLAYCONFIG_SOURCE_DEVICE_NAME[] sourceInfoArray);

    private static int Main()
    {
        foreach (var (label, flags) in new (string, uint)[]
        {
            ("DATABASE_CURRENT", QDC_DATABASE_CURRENT),
            ("ONLY_ACTIVE", QDC_ONLY_ACTIVE_PATHS),
        })
        {
            Console.WriteLine($"=== {label} ===");
            GetDisplayConfigBufferSizes(flags, out var p, out var m, out var t, out var s);
            Console.WriteLine($"  size call: paths={p} modes={m} targets={t} sources={s}");

            Try(label, flags, (int)p, (int)m, (int)t, (int)s, "exact");
            Try(label, flags, (int)p, (int)m, 128, 128, "names padded, arrays exact");
            Try(label, flags, 64, 128, 128, 128, "all padded");
            Try(label, flags, 0, 0, 0, 0, "all zero");
            Console.WriteLine();
        }

        return 0;
    }

    private static void Try(string label, uint flags, int pathCap, int modeCap, int targetCap, int sourceCap, string what)
    {
        uint pc = (uint)pathCap, mc = (uint)modeCap, tc = (uint)targetCap, sc = (uint)sourceCap;

        var paths = new DisplayConfigController.DISPLAYCONFIG_PATH_INFO[pathCap];
        var modes = new DisplayConfigController.DISPLAYCONFIG_MODE_INFO[modeCap];
        var targets = new DisplayConfigController.DISPLAYCONFIG_TARGET_DEVICE_NAME[targetCap];
        var sources = new DisplayConfigController.DISPLAYCONFIG_SOURCE_DEVICE_NAME[sourceCap];

        var rc = QueryDisplayConfig(flags, ref pc, paths, ref mc, modes, ref tc, targets, ref sc, sources);
        Console.WriteLine($"  {what,-32} rc={rc,-4} used paths={pc} modes={mc} targets={tc} sources={sc}");
    }
}
