// Enumerate the CCD targets with the SDK-exact struct layout, so the
// device-path lookup for the virtual monitor can be verified.
//
// The app's DISPLAYCONFIG_TARGET_DEVICE_NAME declared the two EDID ids
// as UINT32 where the SDK says UINT16, and the app read the preferred
// mode out of a DISPLAYCONFIG_MODE_INFO union where the SDK returns
// plain width/height fields.  This probe uses the SDK layout exactly
// and prints every target's device path.
using System;
using System.Runtime.InteropServices;

internal static class Program
{
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

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetPreferredModeInfo(ref PreferredMode request);

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
        Console.WriteLine($"TargetName size = {Marshal.SizeOf<TargetName>()} (SDK: 420)");
        Console.WriteLine($"SourceName size = {Marshal.SizeOf<SourceName>()} (SDK: 84)");
        Console.WriteLine($"PreferredMode size = {Marshal.SizeOf<PreferredMode>()} (SDK: 76)");
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

        Console.WriteLine();
        Console.WriteLine("=== targets ===");
        for (uint i = 0; i < tc; i++)
        {
            var t = targets[i];
            Console.WriteLine($"  target[{i}] adapter={t.header.adapterId.LowPart:X8} id={t.header.id} " +
                              $"edid={t.edidManufactureId:X4}/{t.edidProductCodeId:X4}");
            Console.WriteLine($"    friendly = '{t.monitorFriendlyDeviceName}'");
            Console.WriteLine($"    path     = '{t.monitorDevicePath}'");
            if (t.monitorDevicePath != null && t.monitorDevicePath.Contains("MTT1337", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("    *** THIS IS THE VIRTUAL MONITOR ***");
        }

        Console.WriteLine();
        Console.WriteLine("=== sources ===");
        for (uint i = 0; i < sc && i < 8; i++)
        {
            var s = sources[i];
            if (string.IsNullOrEmpty(s.viewGdiDeviceName)) continue;
            Console.WriteLine($"  source[{i}] adapter={s.header.adapterId.LowPart:X8} id={s.header.id} '{s.viewGdiDeviceName}'");
        }

        Console.WriteLine();
        Console.WriteLine("=== preferred mode of each target ===");
        for (uint i = 0; i < tc; i++)
        {
            var t = targets[i];
            var pref = new PreferredMode
            {
                header = new DeviceInfoHeader
                {
                    type = 3, // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE
                    size = (uint)Marshal.SizeOf<PreferredMode>(),
                    adapterId = t.header.adapterId,
                    id = t.header.id
                }
            };
            var r = GetPreferredModeInfo(ref pref);
            Console.WriteLine($"  target[{i}] rc={r} {pref.width}x{pref.height} " +
                              $"refresh={pref.targetMode.targetVideoSignalInfo.vSyncFreq.Numerator}/" +
                              $"{pref.targetMode.targetVideoSignalInfo.vSyncFreq.Denominator}");
        }

        return 0;
    }
}
