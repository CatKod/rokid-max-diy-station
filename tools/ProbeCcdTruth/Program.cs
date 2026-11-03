// The definitive check of the CCD layout, using the SDK's exact
// definitions.
//
// FINDINGS SO FAR
// ---------------
// The app's DisplayConfigController uses flags and struct fields that
// do not match wingdi.h:
//
//   flag                app      SDK
//   QDC_ALL_PATHS       -        0x01
//   QDC_ONLY_ACTIVE     0x04     0x02
//   QDC_DATABASE_CURRENT 0x01    0x04
//
// The app's QDC_DATABASE_CURRENT is really QDC_ALL_PATHS, and its
// QDC_ONLY_ACTIVE_PATHS is really QDC_DATABASE_CURRENT - the two are
// swapped.
//
//   field                 app      SDK
//   PIXELFORMAT_32BPP    0x02     0x04
//   ROTATION_IDENTITY    0        1
//
//   SOURCE_MODE field order (app: PositionX, PositionY, Width, Height,
//   PixelFormat, Rotation; SDK: Width, Height, PixelFormat, Position)
//
//   TARGET_DEVICE_NAME: the app declares the two EDID ids as UINT32
//   where the SDK says UINT16, which pushes the device-path string 4
//   bytes off - so the "MTT1337" lookup can never match.
//
//   TARGET_PREFERRED_MODE: the SDK returns plain width/height fields,
//   not a mode union.
//
// THIS PROBE
// ----------
// 1. reads every target's device path with the SDK-exact struct, and
// 2. hands the queried topology straight back to SetDisplayConfig
//    unchanged - which must succeed if the layout is right.
using System;
using System.Runtime.InteropServices;

internal static class Program
{
    private const uint QDC_ALL_PATHS = 0x00000001;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint QDC_DATABASE_CURRENT = 0x00000004;

    private const uint SDC_VALIDATE = 0x00000040;
    private const uint SDC_APPLY = 0x00000080;
    private const uint SDC_USE_DATABASE_CURRENT = 0x0000000F;
    private const uint SDC_ALLOW_CHANGES = 0x00000400;
    private const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x00002000;

    private const uint PATH_ACTIVE = 0x00000001;

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

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTargetName(ref TargetName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetPreferredModeInfo(ref PreferredMode request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSourceName(ref SourceName request);

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

    [StructLayout(LayoutKind.Sequential)]
    public struct PreferredMode
    {
        public DeviceInfoHeader header;
        public uint width;
        public uint height;
        public TargetMode targetMode;
    }

    private static int Main()
    {
        Console.WriteLine("--- struct sizes ---");
        Console.WriteLine($"  PathInfo      = {Marshal.SizeOf<PathInfo>()} (SDK 72)");
        Console.WriteLine($"  ModeInfo      = {Marshal.SizeOf<ModeInfo>()} (SDK 64)");
        Console.WriteLine($"  TargetName    = {Marshal.SizeOf<TargetName>()} (SDK 420)");
        Console.WriteLine($"  SourceName    = {Marshal.SizeOf<SourceName>()} (SDK 84)");
        Console.WriteLine($"  PreferredMode = {Marshal.SizeOf<PreferredMode>()} (SDK 76)");
        Console.WriteLine($"  SourceMode    = {Marshal.SizeOf<SourceMode>()} (SDK 20)");
        Console.WriteLine();

        uint pc = 64, mc = 128, tc = 128, sc = 128;
        var paths = new PathInfo[pc];
        var modes = new ModeInfo[mc];
        var targets = new TargetName[tc];
        var sources = new SourceName[sc];

        GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out _, out _, out _, out _);
        var rc = QueryDisplayConfig(QDC_DATABASE_CURRENT, ref pc, paths, ref mc, modes, ref tc, targets, ref sc, sources);
        Console.WriteLine($"query rc={rc} paths={pc} modes={mc} targets={tc} sources={sc}");
        if (rc != 0) return 1;
        Array.Resize(ref paths, (int)pc);
        Array.Resize(ref modes, (int)mc);

        Console.WriteLine();
        Console.WriteLine("--- each path's target device path (via DisplayConfigGetDeviceInfo) ---");
        for (uint i = 0; i < pc; i++)
        {
            var p = paths[i];

            var tn = new TargetName
            {
                header = new DeviceInfoHeader
                {
                    type = 2, // GET_TARGET_NAME
                    size = (uint)Marshal.SizeOf<TargetName>(),
                    adapterId = p.targetInfo.adapterId,
                    id = p.targetInfo.id
                }
            };
            var trc = GetTargetName(ref tn);

            var sn = new SourceName
            {
                header = new DeviceInfoHeader
                {
                    type = 1, // GET_SOURCE_NAME
                    size = (uint)Marshal.SizeOf<SourceName>(),
                    adapterId = p.sourceInfo.adapterId,
                    id = p.sourceInfo.id
                }
            };
            var src = GetSourceName(ref sn);

            var pref = new PreferredMode
            {
                header = new DeviceInfoHeader
                {
                    type = 3, // GET_TARGET_PREFERRED_MODE
                    size = (uint)Marshal.SizeOf<PreferredMode>(),
                    adapterId = p.targetInfo.adapterId,
                    id = p.targetInfo.id
                }
            };
            var prc = GetPreferredModeInfo(ref pref);

            Console.WriteLine($"  path[{i}] active={(p.flags & PATH_ACTIVE) != 0} " +
                              $"srcIdx={p.sourceInfo.modeInfoIdx} tgtIdx={p.targetInfo.modeInfoIdx}");
            Console.WriteLine($"    target   rc={trc} friendly='{tn.monitorFriendlyDeviceName}'");
            Console.WriteLine($"    path     '{tn.monitorDevicePath}'");
            Console.WriteLine($"    source   rc={src} '{sn.viewGdiDeviceName}'");
            Console.WriteLine($"    preferred rc={prc} {pref.width}x{pref.height}");

            // The source mode this path uses.
            var srcMode = modes[p.sourceInfo.modeInfoIdx & 0xFFFF];
            Console.WriteLine($"    source mode: {srcMode.sourceMode.Width}x{srcMode.sourceMode.Height} " +
                              $"pf={srcMode.sourceMode.PixelFormat} @({srcMode.sourceMode.position.x},{srcMode.sourceMode.position.y})");
        }

        Console.WriteLine();
        Console.WriteLine("--- SetDisplayConfig with the queried topology UNCHANGED ---");
        rc = SetDisplayConfig(SDC_VALIDATE | SDC_USE_DATABASE_CURRENT | SDC_ALLOW_CHANGES | SDC_ALLOW_PATH_ORDER_CHANGES,
            paths, (uint)paths.Length, modes, (uint)modes.Length);
        Console.WriteLine($"  SDC_VALIDATE (no change)  rc={rc}");

        rc = SetDisplayConfig(SDC_USE_DATABASE_CURRENT | SDC_ALLOW_CHANGES | SDC_ALLOW_PATH_ORDER_CHANGES,
            paths, (uint)paths.Length, modes, (uint)modes.Length);
        Console.WriteLine($"  APPLY (no change)         rc={rc}");

        return 0;
    }
}
