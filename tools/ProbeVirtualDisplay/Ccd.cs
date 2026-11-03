// SPDX-License-Identifier: MIT
//
// A minimal CCD interop for the diagnostic probes.
//
// The production controller has its own, richer binding; this one
// exists so the probe can dump the topology and move displays around
// without going through the app's own attach/detach path - which is
// the thing being investigated.

using System.Runtime.InteropServices;
using System.Text;

using RokidDiyStation.App.Spatial;

namespace RokidDiyStation.Tools.ProbeVirtualDisplay;

public static class Ccd
{
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    public const uint QDC_DATABASE_CURRENT = 0x00000004;
    public const uint QDC_ALL_PATHS = 0x00000001;

    /// <summary>
    /// Query a specific flag set, with the modes.
    /// </summary>
    public static Path[]? Query(uint flags, out ModeInfo[] modes)
    {
        modes = Array.Empty<ModeInfo>();

        var rc = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
        if (rc != 0) return null;

        var paths = new Path[pathCount];
        var ms = new ModeInfo[modeCount];

        uint topologyId = 0;
        int result = (flags & QDC_DATABASE_CURRENT) != 0
            ? QueryDisplayConfigWithTopology(flags, ref pathCount, paths, ref modeCount, ms, ref topologyId)
            : QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, ms, IntPtr.Zero);

        if (result != 0) return null;

        Array.Resize(ref paths, (int)pathCount);
        Array.Resize(ref ms, (int)modeCount);
        modes = ms;
        return paths;
    }

    private const uint PATH_ACTIVE = 0x00000001;

    private static readonly uint[] ApplyFlagSets =
    {
        0x00000080 | 0x00000020 | 0x00000400 | 0x00002000, // APPLY|USE_SUPPLIED|ALLOW_CHANGES|ORDER
        0x00000080 | 0x00000020 | 0x00000400,                // APPLY|USE_SUPPLIED|ALLOW_CHANGES
        0x00000080 | 0x00000020,                            // APPLY|USE_SUPPLIED
        0x00000080,                                          // APPLY
    };

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
        public override string ToString() => $"{HighPart:X8}:{LowPart:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RATIONAL { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECTL { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct VIDEO_SIGNAL_INFO
    {
        public ulong PixelRate;
        public RATIONAL hSyncFreq, vSyncFreq;
        public uint ActiveCx, ActiveCy, TotalCx, TotalCy, VideoStandard, ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TARGET_MODE { public VIDEO_SIGNAL_INFO targetVideoSignalInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SOURCE_MODE { public uint Width, Height, PixelFormat; public POINTL position; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DESKTOP_IMAGE_INFO { public POINTL PathSourceSize; public RECTL DesktopImageRegion, DesktopImageClip; }

    [StructLayout(LayoutKind.Explicit)]
    public struct ModeInfo
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public TARGET_MODE targetMode;
        [FieldOffset(16)] public SOURCE_MODE sourceMode;
        [FieldOffset(16)] public DESKTOP_IMAGE_INFO desktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SourceInfo { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TargetInfo
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Path
    {
        public SourceInfo sourceInfo;
        public TargetInfo targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfoHeader { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TargetDeviceName
    {
        public DeviceInfoHeader header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathInfoArraySize,
        [Out] Path[] pathInfoArray, ref uint modeInfoArraySize,
        [Out] ModeInfo[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static extern int QueryDisplayConfigWithTopology(uint flags, ref uint pathInfoArraySize,
        [Out] Path[] pathInfoArray, ref uint modeInfoArraySize,
        [Out] ModeInfo[] modeInfoArray, ref uint currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "SetDisplayConfig")]
    private static extern int SetDisplayConfig(uint pathInfoArraySize,
        [In] Path[] pathInfoArray, uint modeInfoArraySize,
        [In] ModeInfo[] modeInfoArray, uint flags);

    /// <summary>
    /// A raw SetDisplayConfig call, with an arbitrary flag set.
    /// </summary>
    /// <remarks>
    /// The probe needs to try flag combinations the app does not use,
    /// so it cannot go through the app's own Apply() helper - which
    /// only ever tries its own list.
    /// </remarks>
    public static int SetDisplayConfigRaw(uint pathCount, Path[] paths,
        uint modeCount, ModeInfo[] modes, uint flags)
        => SetDisplayConfig(pathCount, paths, modeCount, modes, flags);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTargetDeviceInfo(ref TargetDeviceName request);

    public static LUID PathAdapter(Path p) => p.targetInfo.adapterId;
    public static uint PathTargetId(Path p) => p.targetInfo.id;

    /// <summary>
    /// Whether a target is the virtual display, using the APP's own
    /// identification code - so the probe exercises the production
    /// logic rather than a copy of it.
    /// </summary>
    public static bool IsVirtual(Path p)
    {
        var app = new DisplayConfigController.LUID
        {
            LowPart = p.targetInfo.adapterId.LowPart,
            HighPart = p.targetInfo.adapterId.HighPart,
        };
        return DisplayConfigController.IsVirtualTarget(app, p.targetInfo.id);
    }

    public static string TargetPath(LUID adapterId, uint id)
    {
        var req = new TargetDeviceName
        {
            header = new DeviceInfoHeader
            {
                type = 2,
                size = (uint)Marshal.SizeOf<TargetDeviceName>(),
                adapterId = adapterId,
                id = id,
            }
        };
        if (GetTargetDeviceInfo(ref req) != 0) return "";
        return req.monitorDevicePath ?? "";
    }

    public static string TargetFriendlyName(LUID adapterId, uint id)
    {
        var req = new TargetDeviceName
        {
            header = new DeviceInfoHeader
            {
                type = 2,
                size = (uint)Marshal.SizeOf<TargetDeviceName>(),
                adapterId = adapterId,
                id = id,
            }
        };
        if (GetTargetDeviceInfo(ref req) != 0) return "";
        return req.monitorFriendlyDeviceName ?? "";
    }

    public static Path[]? QueryActive(out ModeInfo[] modes)
    {
        modes = Array.Empty<ModeInfo>();

        var rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount);
        if (rc != 0) return null;

        var paths = new Path[pathCount];
        var ms = new ModeInfo[modeCount];

        var result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, ms, IntPtr.Zero);
        if (result != 0) return null;

        Array.Resize(ref paths, (int)pathCount);
        Array.Resize(ref ms, (int)modeCount);
        modes = ms;
        return paths;
    }

    /// <summary>
    /// Apply a path set, trying the flag sets in turn.
    /// </summary>
    public static bool Apply(Path[] paths, ModeInfo[] modes)
    {
        foreach (var flags in ApplyFlagSets)
        {
            var validate = SetDisplayConfig(
                (uint)paths.Length, paths, (uint)modes.Length, modes,
                flags & ~0x00000080u | 0x00000040u);
            if (validate != 0) continue;

            var apply = SetDisplayConfig(
                (uint)paths.Length, paths, (uint)modes.Length, modes, flags);
            Console.WriteLine($"    apply[0x{flags:X}] = {apply}");
            return apply == 0;
        }

        Console.WriteLine("    every flag set was refused");
        return false;
    }

    /// <summary>
    /// Write a position into a path's source mode.
    /// </summary>
    public static void SetSourcePosition(ModeInfo[] modes, Path path, int x, int y)
    {
        if (path.sourceInfo.modeInfoIdx == uint.MaxValue) return;
        if (path.sourceInfo.modeInfoIdx >= modes.Length) return;

        var m = modes[path.sourceInfo.modeInfoIdx];
        if (m.infoType != 1) return;

        m.sourceMode.position = new POINTL { x = x, y = y };
        modes[path.sourceInfo.modeInfoIdx] = m;
    }

    /// <summary>
    /// A path's source mode position.
    /// </summary>
    public static (int X, int Y) SourcePosition(ModeInfo[] modes, Path path)
    {
        if (path.sourceInfo.modeInfoIdx == uint.MaxValue) return (0, 0);
        if (path.sourceInfo.modeInfoIdx >= modes.Length) return (0, 0);

        var m = modes[path.sourceInfo.modeInfoIdx];
        if (m.infoType != 1) return (0, 0);

        return (m.sourceMode.position.x, m.sourceMode.position.y);
    }

    /// <summary>
    /// Attach the virtual display at the given position, using the
    /// app's own attach code.
    /// </summary>
    public static void AttachVirtual(int x, int y)
    {
        var ok = DisplayConfigController.Attach(x, y);
        Console.WriteLine($"  Attach({x},{y}) = {ok}");
    }

    public static void Dump(uint flags, string label)
    {
        var rc = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
        if (rc != 0)
        {
            Console.WriteLine($"    {label}: GetDisplayConfigBufferSizes failed: {rc}");
            return;
        }

        var paths = new Path[pathCount];
        var modes = new ModeInfo[modeCount];

        uint topologyId = 0;
        int result = (flags & QDC_DATABASE_CURRENT) != 0
            ? QueryDisplayConfigWithTopology(flags, ref pathCount, paths, ref modeCount, modes, ref topologyId)
            : QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

        if (result != 0)
        {
            Console.WriteLine($"    {label}: QueryDisplayConfig failed: {result}");
            return;
        }

        Console.WriteLine($"    {label}: {pathCount} path(s), {modeCount} mode(s)");

        for (int i = 0; i < pathCount; i++)
        {
            var p = paths[i];
            string name = TargetFriendlyName(p.targetInfo.adapterId, p.targetInfo.id);
            string path = TargetPath(p.targetInfo.adapterId, p.targetInfo.id);

            string pos = "(no source mode)";
            if (p.sourceInfo.modeInfoIdx != uint.MaxValue && p.sourceInfo.modeInfoIdx < modeCount)
            {
                var m = modes[p.sourceInfo.modeInfoIdx];
                if (m.infoType == 1)
                    pos = $"({m.sourceMode.position.x},{m.sourceMode.position.y}) {m.sourceMode.Width}x{m.sourceMode.Height}";
            }

            Console.WriteLine($"      [{i}] active={((p.flags & PATH_ACTIVE) != 0 ? "Y" : "N")} "
                + $"name='{name}' path='{Trim(path)}' "
                + $"srcIdx={p.sourceInfo.modeInfoIdx} tgtIdx={p.targetInfo.modeInfoIdx} pos={pos}");
        }
    }

    private static string Trim(string s)
    {
        var i = s.IndexOf("#", StringComparison.Ordinal);
        if (i < 0) return s;
        var j = s.IndexOf('#', i + 1);
        if (j < 0) return s;
        return s[..(j + 1)];
    }
}
