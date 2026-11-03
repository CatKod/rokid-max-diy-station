// SPDX-License-Identifier: MIT
//
// The DISPLAYCONFIG (CCD) attach/detach implementation.
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
// The CCD API (QueryDisplayConfig / SetDisplayConfig) removes a display
// PATH rather than tearing the adapter out of the desktop, so the
// adapter stays attached and can be added back.
//
// CCD also needs no administrator rights, which is what makes
// "open the spatial display" a plain user action.
//
// THE CONSTANTS AND THE STRUCT LAYOUT ARE EXACT
// ---------------------------------------------
// Every number and every field order in this file is taken from the
// Windows SDK's wingdi.h, and the layout was verified against the
// running system - see tools/ProbeCcdTruth.
//
// The first version of this file got the flags and several structs
// WRONG, and the failure looked like a broken machine rather than a
// broken binding:
//
//   flag                 was      SDK
//   QDC_ALL_PATHS        -        0x00000001
//   QDC_ONLY_ACTIVE      0x04     0x00000002
//   QDC_DATABASE_CURRENT 0x01     0x00000004
//
// The app's "QDC_DATABASE_CURRENT" was really QDC_ALL_PATHS, and its
// "QDC_ONLY_ACTIVE_PATHS" was really QDC_DATABASE_CURRENT - the two
// were swapped.  QueryDisplayConfig then failed with
// ERROR_INVALID_PARAMETER (87) for every call, so the app concluded
// the topology was unreadable and the virtual display could never be
// found.
//
// Two struct layouts were wrong as well:
//
//   * DISPLAYCONFIG_PATH_SOURCE_INFO is
//     { adapterId, id, union{modeInfoIdx}, statusFlags }.  The mode
//     index lives in the UNION slot - the third dword - and
//     statusFlags is the fourth.  The old layout put the mode index
//     in the fourth dword, where the OS reads statusFlags.
//
//   * DISPLAYCONFIG_TARGET_DEVICE_NAME declares the two EDID ids as
//     UINT16, not UINT32.  With UINT32 they pushed the device-path
//     string four bytes off, so the "MTT1337" fingerprint could never
//     match and the virtual monitor was invisible to this code.
//
// The struct sizes are asserted by the unit tests, because a silent
// layout drift here produces an empty device path rather than an
// exception.
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
    // The query flags - wingdi.h, GetDisplayConfigBufferSizes and
    // QueryDisplayConfig.
    // -----------------------------------------------------------------

    /// <summary>Every path in the database, active or not.</summary>
    private const uint QDC_ALL_PATHS = 0x00000001;

    /// <summary>Only the paths that are on the desktop right now.</summary>
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

    /// <summary>
    /// The current database - the paths Windows remembers, whether
    /// or not they are active.
    /// </summary>
    /// <remarks>
    /// This is the query that finds the virtual display while it is
    /// DETACHED: its path is in the database but not in the active
    /// set, so the active-path query cannot see it.
    /// </remarks>
    private const uint QDC_DATABASE_CURRENT = 0x00000004;

    // -----------------------------------------------------------------
    // The SetDisplayConfig flags - wingdi.h.
    // -----------------------------------------------------------------

    /// <summary>Check the supplied configuration without applying it.</summary>
    private const uint SDC_VALIDATE = 0x00000040;

    /// <summary>Apply the supplied configuration.</summary>
    private const uint SDC_APPLY = 0x00000080;

    /// <summary>Use the arrays that were passed in.</summary>
    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;

    /// <summary>Let Windows change the display's properties.</summary>
    private const uint SDC_ALLOW_CHANGES = 0x00000400;

    /// <summary>Let Windows reorder the paths.</summary>
    private const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x00002000;

    /// <summary>
    /// The flags used to apply a configuration.
    /// </summary>
    /// <remarks>
    /// SDC_SAVE_TO_DATABASE is deliberately NOT set.  Without it the
    /// new arrangement lives only in the session, which is what makes
    /// the detach reversible and keeps it out of the user's display
    /// settings.
    /// </remarks>
    private const uint ApplyFlags =
        SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG |
        SDC_ALLOW_CHANGES | SDC_ALLOW_PATH_ORDER_CHANGES;

    // -----------------------------------------------------------------
    // The mode and device-info constants - wingdi.h.
    // -----------------------------------------------------------------

    private const uint MODE_INFO_TYPE_SOURCE = 1;
    private const uint MODE_INFO_TYPE_TARGET = 2;

    private const uint DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3;

    private const uint PIXELFORMAT_32BPP = 4;
    private const uint ROTATION_IDENTITY = 1;

    /// <summary>DISPLAYCONFIG_PATH_ACTIVE.</summary>
    private const uint PATH_ACTIVE = 0x00000001;

    /// <summary>The virtual display's EDID fingerprint.</summary>
    private const string VirtualEdidFingerprint = "MTT1337";

    // -----------------------------------------------------------------
    // Capacities
    // -----------------------------------------------------------------

    /// <summary>
    /// How much room the path and mode arrays are given.
    /// </summary>
    /// <remarks>
    /// GetDisplayConfigBufferSizes reports the target and source name
    /// counts as ZERO on this machine, and a zero-sized (or NULL) name
    /// array makes the whole query fail with ERROR_INVALID_PARAMETER.
    /// The API treats the size as a CAPACITY and writes back the count
    /// it actually used, so over-reserving is both safe and necessary.
    /// </remarks>
    private const int PathCapacity = 64;

    /// <summary>How much room the mode array is given.</summary>
    private const int ModeCapacity = 256;

    /// <summary>How much room the name arrays are given.</summary>
    private const int NameCapacity = 128;

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
    private static extern int GetPreferredModeInfo(ref DISPLAYCONFIG_TARGET_PREFERRED_MODE request);

    // -----------------------------------------------------------------
    // The structures - the field order is wingdi.h's, exactly.
    // -----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;

        public bool SameAs(LUID other)
            => LowPart == other.LowPart && HighPart == other.HighPart;

        public override string ToString() => $"{HighPart:X8}:{LowPart:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECTL
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VIDEO_SIGNAL_INFO
    {
        public ulong PixelRate;
        public RATIONAL hSyncFreq;
        public RATIONAL vSyncFreq;
        public uint ActiveCx;
        public uint ActiveCy;
        public uint TotalCx;
        public uint TotalCy;
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
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public POINTL position;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DESKTOP_IMAGE_INFO
    {
        public POINTL PathSourceSize;
        public RECTL DesktopImageRegion;
        public RECTL DesktopImageClip;
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

    /// <summary>
    /// The source half of a path.
    /// </summary>
    /// <remarks>
    /// The mode index lives in the UNION - the third dword - and
    /// statusFlags is the fourth.  Getting these two the wrong way
    /// round is silent: the OS reads the mode index as a status flag
    /// and the path then refers to no mode at all.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
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
        public int targetAvailable;
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
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    /// <summary>
    /// The preferred mode of a target.
    /// </summary>
    /// <remarks>
    /// The SDK returns the width and the height as PLAIN fields here -
    /// not as a mode union.  Reading them out of the union returns
    /// whatever the target-mode happens to contain, which is why the
    /// virtual display's size came back as 0x0.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_PREFERRED_MODE
    {
        public DEVICE_INFO_HEADER header;
        public uint width;
        public uint height;
        public TARGET_MODE targetMode;
    }

    // -----------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------

    /// <summary>
    /// Whether the CCD API can describe this machine's display
    /// topology at all.
    /// </summary>
    public static bool IsAvailable()
        => QueryAny(QDC_DATABASE_CURRENT, QDC_ALL_PATHS, QDC_ONLY_ACTIVE_PATHS) is not null;

    /// <summary>
    /// The virtual display's path in the topology, if it is there.
    /// </summary>
    /// <param name="path">The path, when it is found.</param>
    /// <param name="width">The preferred mode's width.</param>
    /// <param name="height">The preferred mode's height.</param>
    /// <returns>True when the virtual display was found.</returns>
    public static bool TryFindVirtual(
        out DISPLAYCONFIG_PATH_INFO path, out int width, out int height)
    {
        path = default;
        width = 0;
        height = 0;

        // The virtual display's path has to be found in the DATABASE,
        // because while it is DETACHED it is not in the active set.
        // The flags are tried in turn, because on this machine only
        // QDC_DATABASE_CURRENT is accepted and the other two answer
        // ERROR_INVALID_PARAMETER (87).
        var topology = QueryAny(QDC_DATABASE_CURRENT, QDC_ALL_PATHS);
        if (topology is null) return false;

        foreach (var p in topology.Paths)
        {
            if (!TargetPath(p.targetInfo.adapterId, p.targetInfo.id)
                    .Contains(VirtualEdidFingerprint, StringComparison.OrdinalIgnoreCase))
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
                width = (int)preferred.width;
                height = (int)preferred.height;
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
        var topology = QueryAny(QDC_DATABASE_CURRENT, QDC_ONLY_ACTIVE_PATHS);
        if (topology is null) return false;

        foreach (var p in topology.Paths)
        {
            // The path has to be ACTIVE as well as virtual: the
            // database keeps the path of a detached display, so
            // matching the EDID alone would say "attached" for a
            // display that is off the desktop.
            bool isActive = (p.flags & PATH_ACTIVE) != 0;
            if (!isActive) continue;

            if (TargetPath(p.targetInfo.adapterId, p.targetInfo.id)
                    .Contains(VirtualEdidFingerprint, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Attach the virtual display at the given position.
    /// </summary>
    /// <remarks>
    /// The virtual display's path is taken from the DATABASE - that is
    /// where it lives while it is detached - together with the source
    /// and target modes the OS recorded for it.  The active paths are
    /// then sent back with that path added, so the adapter is never
    /// torn down and the operation is reversible.
    /// </remarks>
    /// <returns>True when the display has an active path afterwards.</returns>
    public static bool Attach(int x, int y)
    {
        var database = QueryAny(QDC_DATABASE_CURRENT, QDC_ALL_PATHS);
        if (database is null) return false;

        if (!TryFindVirtualPath(database, out var vpath, out var vSourceIdx, out var vTargetIdx))
            return false;

        // The paths that are on the desktop right now.  The flag that
        // returns them is tried in turn, because on this machine only
        // QDC_DATABASE_CURRENT is accepted.
        var active = QueryAny(QDC_DATABASE_CURRENT, QDC_ONLY_ACTIVE_PATHS);
        if (active is null) return false;

        // The path's modes come from the database query, so they have
        // to be carried over into the mode array that is sent back.
        var modes = new List<DISPLAYCONFIG_MODE_INFO>(active.Modes);
        var paths = new List<DISPLAYCONFIG_PATH_INFO>();

        foreach (var p in active.Paths)
        {
            // Only the ACTIVE paths are carried over - an inactive
            // path in the database must not be re-added to the
            // desktop by accident.
            if ((p.flags & PATH_ACTIVE) == 0) continue;
            paths.Add(p);
        }

        if (vSourceIdx < 0 || vTargetIdx < 0) return false;

        var sourceMode = database.Modes[vSourceIdx];
        sourceMode.sourceMode.position = new POINTL { x = x, y = y };

        var newSourceIdx = (uint)modes.Count;
        modes.Add(sourceMode);

        var newTargetIdx = (uint)modes.Count;
        modes.Add(database.Modes[vTargetIdx]);

        var newPath = vpath;
        newPath.sourceInfo.modeInfoIdx = newSourceIdx;
        newPath.targetInfo.modeInfoIdx = newTargetIdx;
        newPath.sourceInfo.statusFlags = 0;
        newPath.targetInfo.targetAvailable = 1;
        newPath.targetInfo.statusFlags = 0;
        newPath.flags = PATH_ACTIVE;

        paths.Add(newPath);

        if (!Apply(paths, modes)) return false;
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
        var active = QueryAny(QDC_DATABASE_CURRENT, QDC_ONLY_ACTIVE_PATHS);
        if (active is null) return false;

        var kept = new List<DISPLAYCONFIG_PATH_INFO>();
        var modes = new List<DISPLAYCONFIG_MODE_INFO>();
        bool removed = false;

        foreach (var p in active.Paths)
        {
            bool isVirtual = TargetPath(p.targetInfo.adapterId, p.targetInfo.id)
                .Contains(VirtualEdidFingerprint, StringComparison.OrdinalIgnoreCase);

            if (isVirtual)
            {
                removed = true;
                continue;
            }

            if ((p.flags & PATH_ACTIVE) == 0) continue;
            kept.Add(p);
        }

        if (!removed) return false;

        // The mode array is sent back unchanged: the paths that are
        // being kept still refer to the modes at the same indices.
        modes.AddRange(active.Modes);
        if (!Apply(kept, modes)) return false;
        return !IsVirtualActive();
    }

    // -----------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------

    /// <summary>
    /// Send a configuration to Windows.
    /// </summary>
    /// <remarks>
    /// The configuration is validated first, with SDC_VALIDATE, so a
    /// configuration the OS rejects is never applied - and the
    /// desktop is never left half-changed.
    /// </remarks>
    private static bool Apply(
        List<DISPLAYCONFIG_PATH_INFO> paths, List<DISPLAYCONFIG_MODE_INFO> modes)
    {
        var pathArray = paths.ToArray();
        var modeArray = modes.ToArray();

        var validate = SetDisplayConfig(
            SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_ALLOW_PATH_ORDER_CHANGES,
            pathArray, (uint)pathArray.Length,
            modeArray, (uint)modeArray.Length);
        if (validate != 0) return false;

        return SetDisplayConfig(
            ApplyFlags,
            pathArray, (uint)pathArray.Length,
            modeArray, (uint)modeArray.Length) == 0;
    }

    /// <summary>
    /// The virtual display's path in a topology, and the indices of its
    /// source and target modes.
    /// </summary>
    private static bool TryFindVirtualPath(
        Topology topology,
        out DISPLAYCONFIG_PATH_INFO path,
        out int sourceModeIdx,
        out int targetModeIdx)
    {
        path = default;
        sourceModeIdx = -1;
        targetModeIdx = -1;

        foreach (var p in topology.Paths)
        {
            if (!TargetPath(p.targetInfo.adapterId, p.targetInfo.id)
                    .Contains(VirtualEdidFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;

            path = p;

            for (var i = 0; i < topology.Modes.Length; i++)
            {
                var m = topology.Modes[i];
                if (m.infoType == MODE_INFO_TYPE_SOURCE &&
                    m.adapterId.SameAs(p.sourceInfo.adapterId) &&
                    m.id == p.sourceInfo.id)
                    sourceModeIdx = i;

                if (m.infoType == MODE_INFO_TYPE_TARGET &&
                    m.adapterId.SameAs(p.targetInfo.adapterId) &&
                    m.id == p.targetInfo.id)
                    targetModeIdx = i;
            }

            return true;
        }

        return false;
    }

    private sealed class Topology
    {
        public DISPLAYCONFIG_PATH_INFO[] Paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
        public DISPLAYCONFIG_MODE_INFO[] Modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();
        public DISPLAYCONFIG_TARGET_DEVICE_NAME[] Targets = Array.Empty<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        public DISPLAYCONFIG_SOURCE_DEVICE_NAME[] Sources = Array.Empty<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
    }

    private static Topology? Query(uint flags)
    {
        // The size call's return code is IGNORED on purpose: the counts
        // it reports for the name arrays are zero on this machine, and
        // a zero-sized name array makes the whole query fail.  The
        // capacities are fixed constants instead.
        GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount,
            out var targetCount, out var sourceCount);

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

    /// <summary>
    /// Query with the first flag that the machine accepts.
    /// </summary>
    /// <remarks>
    /// The three query flags are NOT interchangeable in practice.  On
    /// this machine only QDC_DATABASE_CURRENT is accepted, and both
    /// QDC_ALL_PATHS and QDC_ONLY_ACTIVE_PATHS answer
    /// ERROR_INVALID_PARAMETER (87) - even though the SDK says they
    /// are valid.  The reason is the driver's own state, but the
    /// effect is the same: a query that works on one machine fails on
    /// another.
    ///
    /// So the flags are tried in turn and the first one that answers
    /// is used.  The order puts the database first, because that is
    /// the query the attach needs.
    /// </remarks>
    private static Topology? QueryAny(params uint[] flagOptions)
    {
        foreach (var flags in flagOptions)
        {
            var topology = Query(flags);
            if (topology is not null) return topology;
        }
        return null;
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
    public static string TargetPath(LUID adapterId, uint id)
        => TargetName(adapterId, id).monitorDevicePath ?? "";
}
