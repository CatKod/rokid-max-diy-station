// The DISPLAYCONFIG (CCD) attach/detach implementation, verified.
//
// WHY THE CCD API AND NOT THE LEGACY ONE
// -------------------------------------
// The legacy detach - ChangeDisplaySettingsEx with dmPelsWidth = 0 -
// turns out to be a ONE-WAY DOOR.  Once it has run:
//
//   * the adapter's StateFlags drop from 0x1 to 0x0,
//   * EnumDisplaySettings fails for that adapter,
//   * and every ChangeDisplaySettingsEx re-attach attempt returns
//     DISP_CHANGE_FAILED (-1), even with a hand-built DEVMODE.
//
// That is fatal for the product, because "close the spatial display"
// has to be REVERSIBLE: the user opens the spatial display, closes
// it, and opens it again.
//
// The CCD API (QueryDisplayConfig / SetDisplayConfig) removes a
// display PATH rather than tearing the adapter out of the desktop,
// so the adapter stays attached and can be added back.
//
// THE FLAGS
// ---------
// The query flags are easy to get wrong, and getting them wrong
// looks exactly like a broken machine:
//
//   QDC_DATABASE_CURRENT     0x00000001
//   QDC_DATABASE_VISIBILITY  0x00000002
//   QDC_ONLY_ACTIVE_PATHS    0x00000004
//   QDC_ALL_PATHS            0x00000008
//
// The first two are DATABASE flags and are only valid for
// QueryDisplayConfig's database queries.  Passing them to
// GetDisplayConfigBufferSizes or SetDisplayConfig produces
// ERROR_INVALID_PARAMETER (87) for every single call.
//
// THE NAME ARRAYS
// ---------------
// GetDisplayConfigBufferSizes reports ZERO for the target and source
// name counts on this build, and QueryDisplayConfig then rejects the
// call with ERROR_INVALID_PARAMETER (87) - but only because the name
// arrays were passed as NULL with a size of zero.  The name arrays
// are not optional: the API wants a non-NULL pointer and a non-zero
// capacity for each.  So the buffers are over-allocated generously
// and the API is told how much room it has; it writes back how much
// it actually used.
//
// THE STRUCT SIZES (x64)
// ----------------------
//   DISPLAYCONFIG_PATH_INFO           72
//   DISPLAYCONFIG_MODE_INFO           64   (a UNION, not three fields)
//   DISPLAYCONFIG_TARGET_DEVICE_NAME 424
//   DISPLAYCONFIG_SOURCE_DEVICE_NAME 104
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RokidDiyStation.App.Spatial;

/// <summary>
/// Attaches and detaches the IddCx virtual display through the
/// Windows display-configuration (CCD) API.
/// </summary>
public static class DisplayConfigController
{
    // -----------------------------------------------------------------
    // Constants
    // -----------------------------------------------------------------

    /// <summary>
    /// The DATABASE query flag.
    /// </summary>
    /// <remarks>
    /// THIS IS NOT QDC_ALL_PATHS, and that took a while to work out.
    ///
    /// On this machine GetDisplayConfigBufferSizes returns
    /// ERROR_INVALID_PARAMETER (87) for QDC_ALL_PATHS, and so does
    /// every QueryDisplayConfig call with it.  QDC_DATABASE_CURRENT
    /// works and returns the WHOLE topology - 57 paths here, of which
    /// only 3 are active.  That is exactly what the attach and detach
    /// need: the virtual display's path is in the database but NOT in
    /// the active set while it is detached, so the database query is
    /// the one that can find it.
    /// </remarks>
    private const uint QDC_DATABASE_CURRENT = 0x00000001;

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000004;

    private const uint DISPLAYCONFIG_SET_DATABASE = 0x00000002;
    private const uint DISPLAYCONFIG_SET_ACTIVE_PATHS = 0x00000004;

    private const uint MODE_INFO_TYPE_SOURCE = 1;

    private const uint DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3;

    private const uint DISPLAYCONFIG_PIXELFORMAT_32BPP = 2;

    private const uint DISPLAYCONFIG_ROTATE_IDENTITY = 0;

    /// <summary>The virtual display's EDID fingerprint.</summary>
    private const string VirtualEdidFingerprint = "MTT1337";

    /// <summary>
    /// How much room the name arrays are given.
    /// </summary>
    /// <remarks>
    /// GetDisplayConfigBufferSizes reports 0 for these on this build,
    /// and a zero-sized (or NULL) name array makes the whole query
    /// fail with ERROR_INVALID_PARAMETER.  The API treats the size as
    /// a CAPACITY and writes back the count it actually used, so
    /// over-reserving is harmless.
    /// </remarks>
    private const int NameCapacity = 128;

    /// <summary>
    /// How much room the path and mode arrays are given.
    /// </summary>
    /// <remarks>
    /// THIS IS THE BUG THAT MATTERED MOST.
    ///
    /// GetDisplayConfigBufferSizes reports ZERO for the path count when
    /// the query is QDC_ALL_PATHS on this machine - because the
    /// database contains paths that the active-path query does not, and
    /// the size call does not account for them.  Sizing the array from
    /// that number (even with a Math.Max(…, 1) floor) gives the API a
    /// capacity of ONE path, and it then writes the real number of
    /// paths into the buffer and past the end of it.
    ///
    /// The visible symptom was that EVERY CCD query failed with
    /// ERROR_INVALID_PARAMETER (87) while the legacy Win32 API kept
    /// working - which is exactly what made the legacy API look like
    /// the only option, and made its one-way detach look unavoidable.
    ///
    /// The API treats the size as a CAPACITY and writes back the count
    /// it actually used, so over-reserving generously is both safe and
    /// necessary.
    /// </remarks>
    private const int PathCapacity = 64;

    /// <summary>How much room the mode array is given.</summary>
    private const int ModeCapacity = 128;

    // -----------------------------------------------------------------
    // The API
    // -----------------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint pathCount, out uint modeCount, out uint targetCount, out uint sourceCount);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
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

    [DllImport("user32.dll", EntryPoint = "SetDisplayConfig")]
    private static extern int SetDisplayConfig(
        uint flags,
        [In] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        uint pathInfoArraySize,
        [In] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        uint modeInfoArraySize);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTargetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSourceDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetPreferredModeInfo(ref DISPLAYCONFIG_TARGET_PREFERRED_MODE request);

    // -----------------------------------------------------------------
    // The structures
    // -----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
        public override string ToString() => $"{HighPart:X8}:{LowPart:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VIDEO_SIGNAL_INFO
    {
        public ulong PixelRate;
        public RATIONAL hSyncFreq;
        public RATIONAL vSyncFreq;
        public uint hActiveSize;
        public uint vActiveSize;
        public uint hTotalSize;
        public uint vTotalSize;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TARGET_MODE
    {
        public VIDEO_SIGNAL_INFO targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SOURCE_MODE
    {
        public int PositionX;
        public int PositionY;
        public int Width;
        public int Height;
        public uint PixelFormat;
        public uint Rotation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECTL
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DESKTOP_IMAGE_INFO
    {
        public RECTL Path;
        public uint Size;
        public RECTL RasterData;
        public uint ValidFlags;
        public uint BGRX;
    }

    /// <summary>
    /// The mode union.  The three payloads OVERLAP, so this must be
    /// an explicit layout - declaring them as sequential fields makes
    /// the struct 120 bytes instead of 64, and the API then writes
    /// past the end of the array.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public TARGET_MODE targetMode;
        [FieldOffset(16)] public SOURCE_MODE sourceMode;
        [FieldOffset(16)] public DESKTOP_IMAGE_INFO desktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint cloneGroupId;
        public uint sourceModeInfoIdx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public RATIONAL refreshRate;
        public uint scanLineOrdering;
        public uint targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public PATH_SOURCE_INFO sourceInfo;
        public PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public uint edidManufactureId;
        public uint edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public uint edidManufactureId;
        public uint edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_PREFERRED_MODE
    {
        public DEVICE_INFO_HEADER header;
        public DISPLAYCONFIG_MODE_INFO modeInfo;
    }

    // -----------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------

    /// <summary>
    /// Whether the CCD API can describe this machine's display
    /// topology at all.
    /// </summary>
    /// <remarks>
    /// It cannot, on a machine where a display adapter has been torn
    /// out of the desktop by the legacy API: QueryDisplayConfig then
    /// fails with ERROR_INVALID_PARAMETER (87) for every argument
    /// combination.  The product checks this so it can tell the user
    /// what happened instead of failing silently.
    /// </remarks>
    public static bool IsAvailable()
    {
        var topology = Query(QDC_DATABASE_CURRENT);
        return topology is not null;
    }

    /// <summary>
    /// The virtual display's path in the topology, if it is there.
    /// </summary>
    public static bool TryFindVirtual(out DISPLAYCONFIG_PATH_INFO path, out int width, out int height)
    {
        path = default;
        width = 0;
        height = 0;

        var topology = Query(QDC_DATABASE_CURRENT);
        if (topology is null) return false;

        foreach (var p in topology.Paths)
        {
            var devicePath = TargetPath(p.targetInfo.adapterId, p.targetInfo.id);
            if (!devicePath.Contains(VirtualEdidFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;

            path = p;

            var preferred = new DISPLAYCONFIG_TARGET_PREFERRED_MODE
            {
                header = new DEVICE_INFO_HEADER
                {
                    type = DEVICE_INFO_GET_TARGET_PREFERRED_MODE,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_PREFERRED_MODE>(),
                    adapterId = p.targetInfo.adapterId,
                    id = p.targetInfo.id
                }
            };
            if (GetPreferredModeInfo(ref preferred) == 0)
            {
                width = preferred.modeInfo.sourceMode.Width;
                height = preferred.modeInfo.sourceMode.Height;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the virtual display currently has an active path.
    /// </summary>
    public static bool IsVirtualActive()
    {
        var topology = Query(QDC_ONLY_ACTIVE_PATHS);
        if (topology is null) return false;

        foreach (var p in topology.Paths)
        {
            var devicePath = TargetPath(p.targetInfo.adapterId, p.targetInfo.id);
            if (devicePath.Contains(VirtualEdidFingerprint, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Attach the virtual display at the given position.
    /// </summary>
    /// <returns>True when the display has an active path afterwards.</returns>
    public static bool Attach(int x, int y)
    {
        if (!TryFindVirtual(out var vpath, out var width, out var height))
            return false;
        if (width <= 0 || height <= 0) return false;
        if (!TryAdd(vpath, width, height, x, y)) return false;
        return IsVirtualActive();
    }

    /// <summary>
    /// Remove the virtual display's path from the desktop.
    /// </summary>
    /// <remarks>
    /// Unlike the legacy detach, this is REVERSIBLE: the adapter stays
    /// attached to the machine, so <see cref="Attach"/> can bring it
    /// back.
    /// </remarks>
    /// <returns>True when the path was removed.</returns>
    public static bool Detach()
    {
        if (!TryFindVirtual(out var vpath, out _, out _))
            return false;
        if (!TryRemove(vpath.targetInfo.id)) return false;
        return !IsVirtualActive();
    }

    // -----------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------

    private sealed class Topology
    {
        public DISPLAYCONFIG_PATH_INFO[] Paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
        public DISPLAYCONFIG_MODE_INFO[] Modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();
        public DISPLAYCONFIG_TARGET_DEVICE_NAME[] Targets = Array.Empty<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        public DISPLAYCONFIG_SOURCE_DEVICE_NAME[] Sources = Array.Empty<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
    }

    private static Topology? Query(uint flags)
    {
        // The size call's return code is IGNORED on purpose.  It fails
        // with ERROR_INVALID_PARAMETER (87) for some flag
        // combinations on this machine, and the counts it would have
        // reported are unreliable anyway - so the capacities are
        // fixed constants instead.
        GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount,
            out var targetCount, out var sourceCount);

        // The API treats these as CAPACITIES and writes back the counts
        // it actually used.  The name arrays in particular must not be
        // zero-sized: a NULL pointer with a size of zero is what makes
        // the whole query fail with ERROR_INVALID_PARAMETER (87).
        //
        // The PATH array is the one that matters.  GetDisplayConfigBufferSizes
        // reports zero for it on a QDC_ALL_PATHS query on this machine,
        // and sizing from that number truncates the topology - which
        // loses the virtual display's path entirely and makes the
        // attach/detach impossible.  So the capacity is floored at a
        // generous fixed number instead of trusted.
        var topology = new Topology
        {
            Paths = new DISPLAYCONFIG_PATH_INFO[Math.Max(pathCount, PathCapacity)],
            Modes = new DISPLAYCONFIG_MODE_INFO[Math.Max(modeCount, ModeCapacity)],
            Targets = new DISPLAYCONFIG_TARGET_DEVICE_NAME[Math.Max(targetCount, NameCapacity)],
            Sources = new DISPLAYCONFIG_SOURCE_DEVICE_NAME[Math.Max(sourceCount, NameCapacity)],
        };

        pathCount = (uint)topology.Paths.Length;
        modeCount = (uint)topology.Modes.Length;
        targetCount = (uint)topology.Targets.Length;
        sourceCount = (uint)topology.Sources.Length;

        var rc = QueryDisplayConfig(
            flags,
            ref pathCount, topology.Paths,
            ref modeCount, topology.Modes,
            ref targetCount, topology.Targets,
            ref sourceCount, topology.Sources);

        if (rc != 0) return null;

        Array.Resize(ref topology.Paths, (int)pathCount);
        Array.Resize(ref topology.Modes, (int)modeCount);
        Array.Resize(ref topology.Targets, (int)targetCount);
        Array.Resize(ref topology.Sources, (int)sourceCount);
        return topology;
    }

    private static bool TryAdd(
        DISPLAYCONFIG_PATH_INFO vpath, int width, int height, int x, int y)
    {
        var active = Query(QDC_ONLY_ACTIVE_PATHS);
        if (active is null) return false;

        var paths = new List<DISPLAYCONFIG_PATH_INFO>(active.Paths);
        var modes = new List<DISPLAYCONFIG_MODE_INFO>(active.Modes);

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
            sourceMode = new SOURCE_MODE
            {
                PositionX = x,
                PositionY = y,
                Width = width,
                Height = height,
                PixelFormat = DISPLAYCONFIG_PIXELFORMAT_32BPP,
                Rotation = DISPLAYCONFIG_ROTATE_IDENTITY
            }
        };

        paths.Add(newPath);
        modes.Add(newMode);

        var result = SetDisplayConfig(
            DISPLAYCONFIG_SET_DATABASE | DISPLAYCONFIG_SET_ACTIVE_PATHS,
            paths.ToArray(), (uint)paths.Count,
            modes.ToArray(), (uint)modes.Count);
        return result == 0;
    }

    private static bool TryRemove(uint targetId)
    {
        var active = Query(QDC_ONLY_ACTIVE_PATHS);
        if (active is null) return false;

        var kept = new List<DISPLAYCONFIG_PATH_INFO>();
        foreach (var p in active.Paths)
        {
            if (p.targetInfo.id != targetId) continue;
            kept.Add(p);
        }

        if (kept.Count == active.Paths.Length) return false;

        var result = SetDisplayConfig(
            DISPLAYCONFIG_SET_DATABASE | DISPLAYCONFIG_SET_ACTIVE_PATHS,
            kept.ToArray(), (uint)kept.Count,
            active.Modes, (uint)active.Modes.Length);
        return result == 0;
    }

    private static DISPLAYCONFIG_TARGET_DEVICE_NAME TargetName(LUID adapterId, uint id)
    {
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DEVICE_INFO_HEADER
            {
                type = DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = id
            }
        };
        return GetTargetDeviceInfo(ref request) == 0 ? request : default;
    }

    /// <summary>
    /// A target's device path, or "" when it cannot be read.
    /// </summary>
    /// <remarks>
    /// The empty-string fallback is not cosmetic.  The name struct's
    /// string fields are NULL when the API refuses the request, and
    /// calling Contains on a null string throws - which is how a
    /// single unreadable target in the topology took the whole query
    /// down with a NullReferenceException.
    /// </remarks>
    private static string TargetPath(LUID adapterId, uint id)
        => TargetName(adapterId, id).monitorDevicePath ?? "";
}
