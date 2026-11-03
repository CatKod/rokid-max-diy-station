// Which query flags work on this machine, with which capacities?
//
// The earlier probe showed that QDC_ONLY_ACTIVE_PATHS (0x02) failed
// with ERROR_INVALID_PARAMETER while QDC_DATABASE_CURRENT (0x04)
// worked.  This checks whether that is about the FLAGS or about the
// CAPACITIES - because the size call reports the same counts (3 paths,
// 6 modes) for both, and the arrays were over-reserved in the earlier
// run.
//
// The fix must not depend on which of the two it is, so the answer
// matters.
using System;
using System.Runtime.InteropServices;

internal static class Program
{
    private const uint QDC_ALL_PATHS = 0x00000001;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint QDC_DATABASE_CURRENT = 0x00000004;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint pathCount, out uint modeCount, out uint targetCount, out uint sourceCount);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathInfoArraySize,
        [Out] PathInfo[] pathInfoArray,
        ref uint modeInfoArraySize,
        [Out] ModeInfo[] modeInfoArray,
        ref uint targetInfoArraySize,
        [Out] TargetName[] targetInfoArray,
        ref uint sourceInfoArraySize,
        [Out] SourceName[] sourceInfoArray);

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rational { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointL { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectL { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoSignalInfo
    {
        public ulong PixelRate;
        public Rational hSyncFreq;
        public Rational vSyncFreq;
        public uint ActiveCx, ActiveCy;
        public uint TotalCx, TotalCy;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TargetMode { public VideoSignalInfo targetVideoSignalInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SourceMode
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public PointL position;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DesktopImageInfo
    {
        public PointL PathSourceSize;
        public RectL DesktopImageRegion;
        public RectL DesktopImageClip;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct ModeInfo
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public TargetMode targetMode;
        [FieldOffset(16)] public SourceMode sourceMode;
        [FieldOffset(16)] public DesktopImageInfo desktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathSourceInfo
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathTargetInfo
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public Rational refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathInfo
    {
        public PathSourceInfo sourceInfo;
        public PathTargetInfo targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfoHeader
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TargetName
    {
        public DeviceInfoHeader header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SourceName
    {
        public DeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    private static int Main()
    {
        foreach (var (label, flags) in new (string, uint)[]
        {
            ("ALL_PATHS", QDC_ALL_PATHS),
            ("ONLY_ACTIVE_PATHS", QDC_ONLY_ACTIVE_PATHS),
            ("DATABASE_CURRENT", QDC_DATABASE_CURRENT),
        })
        {
            GetDisplayConfigBufferSizes(flags, out var p, out var m, out var t, out var s);
            Console.WriteLine($"=== {label} (size call: paths={p} modes={m} targets={t} sources={s}) ===");

            Try(flags, (int)p, (int)m, (int)t, (int)s, "exact sizes");
            Try(flags, (int)p, (int)m, 128, 128, "exact arrays, padded names");
            Try(flags, 64, 256, 128, 128, "all padded");
            Console.WriteLine();
        }

        return 0;
    }

    private static void Try(uint flags, int pathCap, int modeCap, int targetCap, int sourceCap, string what)
    {
        uint pc = (uint)pathCap, mc = (uint)modeCap, tc = (uint)targetCap, sc = (uint)sourceCap;
        var paths = new PathInfo[pathCap];
        var modes = new ModeInfo[modeCap];
        var targets = new TargetName[targetCap];
        var sources = new SourceName[sourceCap];

        var rc = QueryDisplayConfig(flags, ref pc, paths, ref mc, modes, ref tc, targets, ref sc, sources);
        Console.WriteLine($"  {what,-28} rc={rc,-4} used paths={pc} modes={mc} targets={tc} sources={sc}");

        if (rc == 0 && pc > 0)
        {
            for (uint i = 0; i < pc; i++)
                Console.WriteLine($"      path[{i}] flags={paths[i].flags} srcIdx={paths[i].sourceInfo.modeInfoIdx} tgtIdx={paths[i].targetInfo.modeInfoIdx}");
        }
    }
}
