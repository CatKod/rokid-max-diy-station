// Which CCD query variant actually works on this machine?
//
// The probe's own earlier run showed the SIZES call succeeding for
// QDC_DATABASE_CURRENT (57 paths) but failing for QDC_ALL_PATHS.  The
// open question is whether the full QUERY succeeds, and whether the
// name arrays are what make it fail.
using System;
using System.Runtime.InteropServices;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology, edidManufactureId, edidProductCodeId, connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology, edidManufactureId, edidProductCodeId, connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint cloneGroupId; public uint sourceModeInfoIdx; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
        public uint rotation; public uint scaling; public RATIONAL refreshRate;
        public uint scanLineOrdering; public uint targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pc, out uint mc, out uint tc, out uint sc);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathSize, [Out] PATH_INFO[] paths,
        ref uint modeSize, IntPtr modes,
        ref uint targetSize, IntPtr targets,
        ref uint sourceSize, IntPtr sources);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static extern int QueryDisplayConfigWithNames(
        uint flags,
        ref uint pathSize, [Out] PATH_INFO[] paths,
        ref uint modeSize, IntPtr modes,
        ref uint targetSize, [Out] TARGET_DEVICE_NAME[] targets,
        ref uint sourceSize, [Out] SOURCE_DEVICE_NAME[] sources);

    private static int Main()
    {
        const uint DATABASE_CURRENT = 0x1;
        const uint ONLY_ACTIVE = 0x4;

        foreach (var (name, flag) in new (string, uint)[] { ("DATABASE_CURRENT", DATABASE_CURRENT), ("ONLY_ACTIVE", ONLY_ACTIVE) })
        {
            Console.WriteLine($"=== {name} ===");
            GetDisplayConfigBufferSizes(flag, out var pc, out var mc, out var tc, out var sc);
            Console.WriteLine($"  sizes: paths={pc} modes={mc} targets={tc} sources={sc}");

            // Variant A: with name arrays.
            {
                var paths = new PATH_INFO[64];
                var modes = Marshal.AllocHGlobal(128 * 64);
                var targets = new TARGET_DEVICE_NAME[128];
                var sources = new SOURCE_DEVICE_NAME[128];
                try
                {
                    var p = (uint)paths.Length; var m = 128u * 64u;
                    var t = (uint)targets.Length; var s = (uint)sources.Length;
                    var rc = QueryDisplayConfigWithNames(flag, ref p, paths, ref m, modes, ref t, targets, ref s, sources);
                    Console.WriteLine($"  A with names: rc={rc} paths={p} modes={m} targets={t} sources={s}");
                }
                finally { Marshal.FreeHGlobal(modes); }
            }

            // Variant B: without name arrays.
            {
                var paths = new PATH_INFO[64];
                var modes = Marshal.AllocHGlobal(128 * 64);
                try
                {
                    var p = (uint)paths.Length; var m = 128u * 64u;
                    var t = 0u; var s = 0u;
                    var rc = QueryDisplayConfig(flag, ref p, paths, ref m, modes, ref t, IntPtr.Zero, ref s, IntPtr.Zero);
                    Console.WriteLine($"  B no names:   rc={rc} paths={p} modes={m}");
                }
                finally { Marshal.FreeHGlobal(modes); }
            }
        }

        return 0;
    }
}
