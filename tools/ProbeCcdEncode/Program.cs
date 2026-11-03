// Which half of the mode-index union holds the index?
//
// DISPLAYCONFIG_PATH_SOURCE_INFO.modeInfoIdx is a union of a plain
// UINT32 and a bitfield pair { cloneGroupId:16, sourceModeInfoIdx:16 },
// and the header declares cloneGroupId FIRST.  On a little-endian
// build that would put cloneGroupId in the low 16 bits - but the
// values the OS actually returned have the INDEX in the low half
// (path[1]: raw 0x00000003, and mode[3] is its source mode).
//
// The probe answers this with SDC_VALIDATE, which checks the supplied
// configuration WITHOUT applying it, so the desktop never changes.
// It tries the index in the low half and in the high half, and the
// only one the API accepts is the correct one.
//
// The same question is asked of the target's modeInfoIdx.
using System;
using System.Runtime.InteropServices;

internal static class Program
{
    private const uint QDC_DATABASE_CURRENT = 0x00000004;

    private const uint SDC_VALIDATE = 0x00000040;
    private const uint SDC_APPLY = 0x00000080;
    private const uint SDC_USE_DATABASE_CURRENT = 0x0000000F;
    private const uint SDC_ALLOW_CHANGES = 0x00000400;
    private const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x00002000;
    private const uint SDC_PATH_PERSIST_IF_REQUIRED = 0x00000800;

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

    [DllImport("user32.dll", EntryPoint = "SetDisplayConfig")]
    private static extern int SetDisplayConfig(
        uint flags,
        [In] PathInfo[] pathInfoArray,
        uint pathInfoArraySize,
        [In] ModeInfo[] modeInfoArray,
        uint modeInfoArraySize);

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

    private static PathInfo[] _paths = Array.Empty<PathInfo>();
    private static ModeInfo[] _modes = Array.Empty<ModeInfo>();

    private static int Main()
    {
        uint pc = 64, mc = 128, tc = 128, sc = 128;
        var paths = new PathInfo[pc];
        var modes = new ModeInfo[mc];
        var targets = new TargetName[tc];
        var sources = new SourceName[sc];

        GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out var p0, out var m0, out _, out _);
        Console.WriteLine($"size call: paths={p0} modes={m0}");
        var rc = QueryDisplayConfig(QDC_DATABASE_CURRENT, ref pc, paths, ref mc, modes, ref tc, targets, ref sc, sources);
        Console.WriteLine($"query rc={rc} paths={pc} modes={mc} targets={tc} sources={sc}");
        if (rc != 0) return 1;

        Array.Resize(ref paths, (int)pc);
        Array.Resize(ref modes, (int)mc);
        _paths = paths;
        _modes = modes;

        for (uint i = 0; i < pc; i++)
        {
            Console.WriteLine($"  path[{i}] flags={paths[i].flags} " +
                              $"srcIdx=0x{paths[i].sourceInfo.modeInfoIdx:X8} " +
                              $"tgtIdx=0x{paths[i].targetInfo.modeInfoIdx:X8}");
        }
        Console.WriteLine();

        if (pc < 2) { Console.WriteLine("not enough paths to test"); return 1; }

        // Pick a path that has BOTH a source and a target mode.
        int pick = -1;
        for (uint i = 0; i < pc; i++)
        {
            if (paths[i].sourceInfo.modeInfoIdx != 0 && paths[i].targetInfo.modeInfoIdx != 0) { pick = (int)i; break; }
        }
        if (pick < 0) { Console.WriteLine("no path with both indices"); return 1; }
        Console.WriteLine($"testing with path[{pick}]");
        Console.WriteLine();

        var srcIdx = paths[pick].sourceInfo.modeInfoIdx & 0xFFFF;
        var tgtIdx = paths[pick].targetInfo.modeInfoIdx & 0xFFFF;

        Validate("baseline (unchanged)", p => { });
        Validate("source index in the LOW half", p => p[pick].sourceInfo.modeInfoIdx = srcIdx);
        Validate("source index in the HIGH half", p => p[pick].sourceInfo.modeInfoIdx = srcIdx << 16);
        Validate("target index in the LOW half", p => p[pick].targetInfo.modeInfoIdx = tgtIdx);
        Validate("target index in the HIGH half", p => p[pick].targetInfo.modeInfoIdx = tgtIdx << 16);
        Validate("source index = 0xFFFF (invalid)", p => p[pick].sourceInfo.modeInfoIdx = 0xFFFF);
        Validate("target index = 0xFFFF (invalid)", p => p[pick].targetInfo.modeInfoIdx = 0xFFFF);

        return 0;
    }

    private static void Validate(string label, Action<PathInfo[]> mutate)
    {
        var paths = (PathInfo[])_paths.Clone();
        mutate(paths);
        var modes = (ModeInfo[])_modes.Clone();

        var rc = SetDisplayConfig(SDC_VALIDATE | SDC_USE_DATABASE_CURRENT | SDC_ALLOW_CHANGES,
            paths, (uint)paths.Length, modes, (uint)modes.Length);
        Console.WriteLine($"  {label,-34} rc={rc}");

        rc = SetDisplayConfig(SDC_VALIDATE | SDC_ALLOW_CHANGES,
            paths, (uint)paths.Length, modes, (uint)modes.Length);
        Console.WriteLine($"    (without SDC_USE_DATABASE_CURRENT)  rc={rc}");
    }
}
