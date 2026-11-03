// The DISPLAYCONFIG probe, using proper blittable structs.
//
// WHY THIS FILE EXISTS
// --------------------
// The virtual display (MttVDD / IddCx) has to be ATTACHED and DETACHED
// on demand, without administrator rights.  The legacy Win32 API
// (ChangeDisplaySettingsEx) can detach it, but once detached the
// adapter is no longer attached to the desktop, so the legacy API can
// never bring it back.
//
// The modern CCD API (QueryDisplayConfig / SetDisplayConfig) can do
// both, in one round trip, as a normal user.
//
// A NOTE ON THE FIRST ATTEMPT
// ---------------------------
// The first version of this probe used raw IntPtr buffers and hand
// computed byte offsets.  It crashed with an AccessViolation,
// because two of the sizes were wrong:
//
//   * DISPLAYCONFIG_SOURCE_DEVICE_NAME is 104 bytes, not 64.  The
//     name buffer was undersized, so QueryDisplayConfig wrote past
//     the end of the allocation.
//   * The offsets inside DISPLAYCONFIG_TARGET_DEVICE_NAME were
//     off by 16 bytes.
//
// The structs below are the real Windows SDK layouts, so there is
// nothing left to get wrong by hand.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    // -----------------------------------------------------------------
    // The Windows SDK structures
    // -----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
        public override string ToString() => $"{HighPart:X8}:{LowPart:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong PixelRate;
        public DISPLAYCONFIG_RATIONAL hSyncFreq;
        public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public uint hActiveSize;
        public uint vActiveSize;
        public uint hTotalSize;
        public uint vTotalSize;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    /// <remarks>
    /// The three mode payloads are a UNION, not three fields.  Writing
    /// them as sequential fields makes the struct 120 bytes instead of
    /// 64, and QueryDisplayConfig then writes past the end of the
    /// array - which is an access violation, not a clean error.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public DISPLAYCONFIG_TARGET_MODE targetMode;
        [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
        [FieldOffset(16)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_MODE
    {
        public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_MODE
    {
        public int PositionX;
        public int PositionY;
        public int Width;
        public int Height;
        public uint PixelFormat;
        public uint Rotation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECTL
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
    {
        public RECTL Path;
        public uint Size;
        public RECTL RasterData;
        public uint ValidFlags;
        public uint BGRX;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint cloneGroupId;
        public uint sourceModeInfoIdx;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public uint targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public uint edidManufactureId;
        public uint edidProductCodeId;
        public uint connectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public uint edidManufactureId;
        public uint edidProductCodeId;
        public uint connectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_PREFERRED_MODE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public DISPLAYCONFIG_MODE_INFO modeInfo;
    }

    // -----------------------------------------------------------------
    // The API
    // -----------------------------------------------------------------

    /// <remarks>
    /// The size parameters are IN/OUT: on the way in they say how many
    /// elements the buffer holds, on the way out they say how many the
    /// API actually filled in.  So they must be marshalled as
    /// <c>ref uint</c> with the capacity preset - declaring them as
    /// <c>out uint</c> hands the API an UNINITIALISED capacity, and
    /// it rejects the whole call with ERROR_INVALID_PARAMETER (87).
    /// </remarks>
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathInfoArraySize,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint modeInfoArraySize,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        ref uint targetInfoArraySize,
        [Out] DISPLAYCONFIG_TARGET_DEVICE_NAME[] targetInfoArray,
        ref uint sourceInfoArraySize,
        [Out] DISPLAYCONFIG_SOURCE_DEVICE_NAME[] sourceInfoArray);

    // The same API, but with the name arrays omitted.  The docs allow
    // a NULL name array when its size is 0, and the name structs are
    // the fussiest part of the layout to get right.
    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static extern int QueryDisplayConfigNoNames(
        uint flags,
        ref uint pathInfoArraySize,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint modeInfoArraySize,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        ref uint targetInfoArraySize,
        IntPtr targetInfoArray,
        ref uint sourceInfoArraySize,
        IntPtr sourceInfoArray);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(
        uint flags,
        [In] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        uint pathInfoArraySize,
        [In] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        uint modeInfoArraySize);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint pathCount, out uint modeCount, out uint targetCount, out uint sourceCount);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTargetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSourceDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetPreferredModeInfo(ref DISPLAYCONFIG_TARGET_PREFERRED_MODE request);

    // -----------------------------------------------------------------
    // Constants
    // -----------------------------------------------------------------

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000004;
    private const uint QDC_ALL_PATHS = 0x00000008;

    private const uint DISPLAYCONFIG_SET_DATABASE = 0x00000002;
    private const uint DISPLAYCONFIG_SET_ACTIVE_PATHS = 0x00000004;

    private const uint MODE_INFO_TYPE_SOURCE = 1;

    private const uint DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3;

    // -----------------------------------------------------------------

    private sealed class Topology
    {
        public DISPLAYCONFIG_PATH_INFO[] Paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
        public DISPLAYCONFIG_MODE_INFO[] Modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();
        public DISPLAYCONFIG_TARGET_DEVICE_NAME[] Targets = Array.Empty<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        public DISPLAYCONFIG_SOURCE_DEVICE_NAME[] Sources = Array.Empty<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
    }

    private static int Main()
    {
        var topology = Query(QDC_ALL_PATHS);
        Console.WriteLine($"=== ALL_PATHS: {topology.Paths.Length} paths, {topology.Modes.Length} modes, " +
                          $"{topology.Targets.Length} targets, {topology.Sources.Length} sources ===");

        if (topology.Paths.Length == 0) return 1;

        // --- find the virtual display -------------------------------
        var virtualIndex = -1;
        for (var i = 0; i < topology.Paths.Length; i++)
        {
            var p = topology.Paths[i];
            var friendly = TargetName(p.targetInfo.adapterId, p.targetInfo.id);
            var path = TargetPath(p.targetInfo.adapterId, p.targetInfo.id);
            var isVirtual = path.Contains("MTT1337", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  PATH[{i}] src={p.sourceInfo.id} tgt={p.targetInfo.id} " +
                              $"adapter={p.targetInfo.adapterId} -> {friendly} [{path}]" +
                              (isVirtual ? "   <<< VIRTUAL" : ""));
            if (isVirtual && virtualIndex < 0) virtualIndex = i;
        }

        if (virtualIndex < 0)
        {
            Console.WriteLine("FAIL: the virtual display is not in the topology at all.");
            return 1;
        }

        var vpath = topology.Paths[virtualIndex];
        var sourceName = SourceName(vpath.sourceInfo.adapterId, vpath.sourceInfo.id);
        Console.WriteLine();
        Console.WriteLine($"virtual: target id={vpath.targetInfo.id} adapter={vpath.targetInfo.adapterId}");
        Console.WriteLine($"virtual: source id={vpath.sourceInfo.id} adapter={vpath.sourceInfo.adapterId} view={sourceName}");

        // --- the preferred mode ------------------------------------
        var preferred = new DISPLAYCONFIG_TARGET_PREFERRED_MODE
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DEVICE_INFO_GET_TARGET_PREFERRED_MODE,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_PREFERRED_MODE>(),
                adapterId = vpath.targetInfo.adapterId,
                id = vpath.targetInfo.id
            }
        };
        if (GetPreferredModeInfo(ref preferred) != 0)
        {
            Console.WriteLine("FAIL: no preferred mode.");
            return 1;
        }
        Console.WriteLine($"preferred: {preferred.modeInfo.sourceMode.Width}x{preferred.modeInfo.sourceMode.Height}");

        // --- ADD ----------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== ADD ===");
        var added = TryAdd(vpath, preferred.modeInfo.sourceMode.Width, preferred.modeInfo.sourceMode.Height, -1920, 0);
        Thread.Sleep(1500);
        DumpMonitors();
        Console.WriteLine($"added: {added}");

        // --- REMOVE -------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== REMOVE ===");
        var removed = TryRemove(vpath.targetInfo.id);
        Thread.Sleep(1500);
        DumpMonitors();
        Console.WriteLine($"removed: {removed}");

        // --- ADD BACK -----------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== ADD BACK ===");
        var addedAgain = TryAdd(vpath, preferred.modeInfo.sourceMode.Width, preferred.modeInfo.sourceMode.Height, -1920, 0);
        Thread.Sleep(1500);
        DumpMonitors();
        Console.WriteLine($"added again: {addedAgain}");

        return 0;
    }

    // -----------------------------------------------------------------
    // Query
    // -----------------------------------------------------------------

    private static Topology Query(uint flags)
    {
        GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount,
            out var targetCount, out var sourceCount);

        Console.WriteLine($"  buffer sizes: paths={pathCount} modes={modeCount} " +
                          $"targets={targetCount} sources={sourceCount}");

        // GetDisplayConfigBufferSizes reports 0 for the target and
        // source counts on this build, and QueryDisplayConfig rejects
        // the call with ERROR_INVALID_PARAMETER (87) when those sizes
        // are 0.  The capacities below are deliberately generous:
        // the API treats them as a CAPACITY, and writes back the real
        // counts, so over-reserving is harmless.
        const int capacity = 512;

        var t = new Topology
        {
            Paths = new DISPLAYCONFIG_PATH_INFO[capacity],
            Modes = new DISPLAYCONFIG_MODE_INFO[capacity * 2],
            Targets = new DISPLAYCONFIG_TARGET_DEVICE_NAME[capacity],
            Sources = new DISPLAYCONFIG_SOURCE_DEVICE_NAME[capacity]
        };

        // The size parameters are IN/OUT: the capacity goes in, the
        // number of elements actually filled in comes back out.
        pathCount = (uint)t.Paths.Length;
        modeCount = (uint)t.Modes.Length;

        // The name arrays are omitted, so their sizes MUST be 0.  A
        // non-zero size with a NULL pointer is what makes the API
        // reject the call with ERROR_INVALID_PARAMETER (87).
        targetCount = 0;
        sourceCount = 0;

        var rc = QueryDisplayConfigNoNames(
            flags,
            ref pathCount,
            t.Paths,
            ref modeCount,
            t.Modes,
            ref targetCount,
            IntPtr.Zero,
            ref sourceCount,
            IntPtr.Zero);

        Console.WriteLine($"  QueryDisplayConfig -> {rc}");
        if (rc != 0) return new Topology();

        // The API writes back how many elements it actually filled in.
        if (pathCount > t.Paths.Length) pathCount = (uint)t.Paths.Length;
        if (modeCount > t.Modes.Length) modeCount = (uint)t.Modes.Length;
        if (targetCount > t.Targets.Length) targetCount = (uint)t.Targets.Length;
        if (sourceCount > t.Sources.Length) sourceCount = (uint)t.Sources.Length;

        Array.Resize(ref t.Paths, (int)pathCount);
        Array.Resize(ref t.Modes, (int)modeCount);
        Array.Resize(ref t.Targets, (int)targetCount);
        Array.Resize(ref t.Sources, (int)sourceCount);

        return t;
    }

    // -----------------------------------------------------------------
    // Add / remove
    // -----------------------------------------------------------------

    private static bool TryAdd(
        DISPLAYCONFIG_PATH_INFO vpath, int width, int height, int x, int y)
    {
        var active = Query(QDC_ONLY_ACTIVE_PATHS);
        if (active.Paths.Length == 0) return false;

        var paths = new List<DISPLAYCONFIG_PATH_INFO>(active.Paths);
        var modes = new List<DISPLAYCONFIG_MODE_INFO>(active.Modes);

        // The new path: the same source and target as the inactive
        // path, but pointed at a source mode we append.
        var newPath = vpath;
        newPath.sourceInfo.sourceModeInfoIdx = (uint)modes.Count;
        newPath.targetInfo.modeInfoIdx = (uint)modes.Count;
        newPath.targetInfo.targetAvailable = 1;
        newPath.targetInfo.statusFlags = 0;

        var newMode = new DISPLAYCONFIG_MODE_INFO
        {
            infoType = MODE_INFO_TYPE_SOURCE,
            id = vpath.sourceInfo.id,
            adapterId = vpath.sourceInfo.adapterId,
            sourceMode = new DISPLAYCONFIG_SOURCE_MODE
            {
                PositionX = x,
                PositionY = y,
                Width = width,
                Height = height,
                PixelFormat = 0,
                Rotation = 1
            }
        };

        paths.Add(newPath);
        modes.Add(newMode);

        var result = SetDisplayConfig(
            DISPLAYCONFIG_SET_DATABASE | DISPLAYCONFIG_SET_ACTIVE_PATHS,
            paths.ToArray(), (uint)paths.Count,
            modes.ToArray(), (uint)modes.Count);
        Console.WriteLine($"  SetDisplayConfig -> {result}");
        return result == 0;
    }

    private static bool TryRemove(uint targetId)
    {
        var active = Query(QDC_ONLY_ACTIVE_PATHS);
        if (active.Paths.Length == 0) return false;

        var kept = new List<DISPLAYCONFIG_PATH_INFO>();
        foreach (var p in active.Paths)
            if (p.targetInfo.id != targetId)
                kept.Add(p);

        Console.WriteLine($"  active paths: {active.Paths.Length} -> keeping {kept.Count}");

        var result = SetDisplayConfig(
            DISPLAYCONFIG_SET_DATABASE | DISPLAYCONFIG_SET_ACTIVE_PATHS,
            kept.ToArray(), (uint)kept.Count,
            active.Modes, (uint)active.Modes.Length);
        Console.WriteLine($"  SetDisplayConfig -> {result}");
        return result == 0;
    }

    // -----------------------------------------------------------------
    // Device info
    // -----------------------------------------------------------------

    private static DISPLAYCONFIG_TARGET_DEVICE_NAME TargetName(LUID adapterId, uint id)
    {
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = id
            }
        };
        return GetTargetDeviceInfo(ref request) == 0 ? request : default;
    }

    private static string TargetPath(LUID adapterId, uint id)
        => TargetName(adapterId, id).monitorDevicePath;

    private static string SourceName(LUID adapterId, uint id)
    {
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = adapterId,
                id = id
            }
        };
        return GetSourceDeviceInfo(ref request) == 0 ? request.viewGdiDeviceName : "";
    }

    // -----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr h, IntPtr dc, ref RECT r, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);

    private static void DumpMonitors()
    {
        var list = new List<MONITORINFOEX>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi)) list.Add(mi);
            return true;
        }, IntPtr.Zero);
        Console.WriteLine($"  monitors: {list.Count}");
        foreach (var m in list)
            Console.WriteLine($"    {m.szDevice} {m.rcMonitor.Right - m.rcMonitor.Left}x{m.rcMonitor.Bottom - m.rcMonitor.Top} @ ({m.rcMonitor.Left},{m.rcMonitor.Top})");
    }
}
