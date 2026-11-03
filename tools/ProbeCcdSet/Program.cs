// Which SetDisplayConfig flag combination does this machine accept?
//
// Every call here carries SDC_VALIDATE, which checks the supplied
// configuration WITHOUT applying it, so the desktop never changes.
//
// The topology handed back is the one QueryDisplayConfig just
// returned, unmodified - so a successful validation proves the
// STRUCT LAYOUT is right (the OS understood the paths and modes),
// and a failure with ERROR_INVALID_PARAMETER means it did not.
using System;
using System.Runtime.InteropServices;

internal static class Program
{
    private const uint QDC_DATABASE_CURRENT = 0x00000004;

    private const uint SDC_VALIDATE = 0x00000040;
    private const uint SDC_APPLY = 0x00000080;
    private const uint SDC_NO_OPTIMIZATION = 0x00000100;
    private const uint SDC_SAVE_TO_DATABASE = 0x00000200;
    private const uint SDC_ALLOW_CHANGES = 0x00000400;
    private const uint SDC_PATH_PERSIST_IF_REQUIRED = 0x00000800;
    private const uint SDC_FORCE_MODE_ENUMERATION = 0x00001000;
    private const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x00002000;
    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    private const uint SDC_USE_DATABASE_CURRENT = 0x0000000F;

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

    private static int Main()
    {
        uint pc = 64, mc = 128, tc = 128, sc = 128;
        var paths = new PathInfo[pc];
        var modes = new ModeInfo[mc];
        var targets = new TargetName[tc];
        var sources = new SourceName[sc];

        GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out _, out _, out _, out _);
        var rc = QueryDisplayConfig(QDC_DATABASE_CURRENT, ref pc, paths, ref mc, modes, ref tc, targets, ref sc, sources);
        Console.WriteLine($"query rc={rc} paths={pc} modes={mc}");
        if (rc != 0) return 1;
        Array.Resize(ref paths, (int)pc);
        Array.Resize(ref modes, (int)mc);

        Try("VALIDATE | USE_SUPPLIED", SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG, paths, modes);
        Try("VALIDATE | USE_SUPPLIED | ALLOW_CHANGES", SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES, paths, modes);
        Try("VALIDATE | USE_DATABASE_CURRENT", SDC_VALIDATE | SDC_USE_DATABASE_CURRENT, paths, modes);
        Try("VALIDATE | USE_DATABASE_CURRENT | ALLOW_CHANGES", SDC_VALIDATE | SDC_USE_DATABASE_CURRENT | SDC_ALLOW_CHANGES, paths, modes);
        Try("VALIDATE | USE_SUPPLIED | ALLOW_CHANGES | PATH_ORDER", SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_ALLOW_PATH_ORDER_CHANGES, paths, modes);
        Try("VALIDATE | USE_SUPPLIED | ALLOW_CHANGES | PATH_ORDER | NO_OPT", SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_ALLOW_PATH_ORDER_CHANGES | SDC_NO_OPTIMIZATION, paths, modes);
        Try("VALIDATE | USE_SUPPLIED | ALLOW_CHANGES | PATH_ORDER | PERSIST", SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_ALLOW_PATH_ORDER_CHANGES | SDC_PATH_PERSIST_IF_REQUIRED, paths, modes);
        Try("VALIDATE | USE_SUPPLIED | ALLOW_CHANGES | PATH_ORDER | FORCE_ENUM", SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_ALLOW_PATH_ORDER_CHANGES | SDC_FORCE_MODE_ENUMERATION, paths, modes);

        // Try with only the TARGET modes, and only the SOURCE modes.
        var onlyTarget = new List<ModeInfo>();
        var onlySource = new List<ModeInfo>();
        foreach (var m in modes)
        {
            if (m.infoType == 2) onlyTarget.Add(m);
            else if (m.infoType == 1) onlySource.Add(m);
        }
        Console.WriteLine($"  (targets={onlyTarget.Count} sources={onlySource.Count} total={modes.Length})");

        return 0;
    }

    private static void Try(string label, uint flags, PathInfo[] paths, ModeInfo[] modes)
    {
        var rc = SetDisplayConfig(flags, paths, (uint)paths.Length, modes, (uint)modes.Length);
        Console.WriteLine($"  {label,-62} rc={rc}");
    }
}
