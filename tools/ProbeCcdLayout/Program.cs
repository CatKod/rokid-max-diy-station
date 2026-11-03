// The CCD constants in the app did not match the Windows SDK.
//
// The SDK (wingdi.h) says:
//
//   QDC_ALL_PATHS          0x00000001
//   QDC_ONLY_ACTIVE_PATHS  0x00000002
//   QDC_DATABASE_CURRENT   0x00000004
//
// The app used QDC_DATABASE_CURRENT = 0x01 and
// QDC_ONLY_ACTIVE_PATHS = 0x04 - the two are swapped, and 0x01 is
// really QDC_ALL_PATHS, which fails with ERROR_INVALID_PARAMETER on
// this machine.  So the app was querying the ALL-paths flavour and
// concluding the topology was unreadable.
//
// This probe uses the SDK values and also answers the remaining
// question the structs raise: how the OS encodes the mode index in
// PATH_SOURCE_INFO and PATH_TARGET_INFO.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

internal static class Program
{
    private const uint QDC_ALL_PATHS = 0x00000001;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint QDC_DATABASE_CURRENT = 0x00000004;

    private const uint MODE_INFO_TYPE_SOURCE = 1;
    private const uint MODE_INFO_TYPE_TARGET = 2;

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
        Console.WriteLine($"sizes: PathInfo={Marshal.SizeOf<PathInfo>()} ModeInfo={Marshal.SizeOf<ModeInfo>()} " +
                          $"TargetName={Marshal.SizeOf<TargetName>()} SourceName={Marshal.SizeOf<SourceName>()} " +
                          $"SourceMode={Marshal.SizeOf<SourceMode>()} TargetMode={Marshal.SizeOf<TargetMode>()} " +
                          $"VideoSignalInfo={Marshal.SizeOf<VideoSignalInfo>()}");
        Console.WriteLine();

        foreach (var (label, flags) in new (string, uint)[]
        {
            ("QDC_ALL_PATHS", QDC_ALL_PATHS),
            ("QDC_ONLY_ACTIVE_PATHS", QDC_ONLY_ACTIVE_PATHS),
            ("QDC_DATABASE_CURRENT", QDC_DATABASE_CURRENT),
        })
        {
            Console.WriteLine($"=== {label} ===");
            var rc = GetDisplayConfigBufferSizes(flags, out var p, out var m, out var t, out var s);
            Console.WriteLine($"  size call rc={rc} paths={p} modes={m} targets={t} sources={s}");

            uint pc = 64, mc = 128, tc = 128, sc = 128;
            var paths = new PathInfo[pc];
            var modes = new ModeInfo[mc];
            var targets = new TargetName[tc];
            var sources = new SourceName[sc];
            rc = QueryDisplayConfig(flags, ref pc, paths, ref mc, modes, ref tc, targets, ref sc, sources);
            Console.WriteLine($"  query rc={rc} used paths={pc} modes={mc} targets={tc} sources={sc}");
            if (rc != 0) { Console.WriteLine(); continue; }

            for (uint i = 0; i < pc; i++)
            {
                var path = paths[i];
                string tpath = "?";
                for (uint k = 0; k < tc; k++)
                {
                    if (targets[k].header.adapterId.LowPart == path.targetInfo.adapterId.LowPart &&
                        targets[k].header.id == path.targetInfo.id)
                    { tpath = targets[k].monitorDevicePath ?? "(null)"; break; }
                }

                // Which mode indices actually describe this path?
                var sourceIdx = new List<uint>();
                var targetIdx = new List<uint>();
                for (uint k = 0; k < mc; k++)
                {
                    var mi = modes[k];
                    if (mi.infoType == MODE_INFO_TYPE_SOURCE &&
                        mi.adapterId.LowPart == path.sourceInfo.adapterId.LowPart &&
                        mi.id == path.sourceInfo.id)
                        sourceIdx.Add(k);
                    if (mi.infoType == MODE_INFO_TYPE_TARGET &&
                        mi.adapterId.LowPart == path.targetInfo.adapterId.LowPart &&
                        mi.id == path.targetInfo.id)
                        targetIdx.Add(k);
                }

                Console.WriteLine($"  path[{i}] flags={path.flags}");
                Console.WriteLine($"    target  {tpath}");
                Console.WriteLine($"    source.modeInfoIdx raw=0x{path.sourceInfo.modeInfoIdx:X8} " +
                                  $"low16={path.sourceInfo.modeInfoIdx & 0xFFFF} high16={path.sourceInfo.modeInfoIdx >> 16}");
                Console.WriteLine($"      actual source mode indices: [{string.Join(",", sourceIdx)}]");
                Console.WriteLine($"    target.modeInfoIdx raw=0x{path.targetInfo.modeInfoIdx:X8} " +
                                  $"low16={path.targetInfo.modeInfoIdx & 0xFFFF} high16={path.targetInfo.modeInfoIdx >> 16}");
                Console.WriteLine($"      actual target mode indices: [{string.Join(",", targetIdx)}]");
            }
            Console.WriteLine();
        }

        return 0;
    }
}
