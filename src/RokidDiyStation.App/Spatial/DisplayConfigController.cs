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
// THE BINDINGS WERE WRONG - AND THAT WAS THE WHOLE BUG
// -----------------------------------------------------
// This file used to declare the three CCD functions with signatures
// that do not exist.  Read from the Windows SDK (10.0.26100.0,
// um/WinUser.h), they are:
//
//   LONG GetDisplayConfigBufferSizes(
//       _In_  UINT32 flags,
//       _Out_ UINT32* numPathArrayElements,
//       _Out_ UINT32* numModeInfoArrayElements);
//
//   LONG SetDisplayConfig(
//       _In_ UINT32 numPathArrayElements,
//       _In_ DISPLAYCONFIG_PATH_INFO* pathArray,
//       _In_ UINT32 numModeInfoArrayElements,
//       _In_ DISPLAYCONFIG_MODE_INFO* modeInfoArray,
//       _In_ UINT32 flags);
//
//   LONG QueryDisplayConfig(
//       _In_    UINT32 flags,
//       _Inout_ UINT32* numPathArrayElements,
//       _Out_   DISPLAYCONFIG_PATH_INFO* pathArray,
//       _Inout_ UINT32* numModeInfoArrayElements,
//       _Out_   DISPLAYCONFIG_MODE_INFO* modeInfoArray,
//       _Inout_ DISPLAYCONFIG_TOPOLOGY_ID* currentTopologyId);
//
// What this file used to declare:
//
//   GetDisplayConfigBufferSizes(flags, *path, *mode, *target, *source)
//   QueryDisplayConfig(flags, *path, paths, *mode, modes, *target,
//                      targets, *source, sources)
//   SetDisplayConfig(flags, paths, pathCount, modes, modeCount)
//
// There are NO target-name or source-name arrays in QueryDisplayConfig.
// The names come from DisplayConfigGetDeviceInfo, one target at a
// time.  And SetDisplayConfig takes the FLAGS LAST.
//
// The failure looked like a broken machine rather than a broken
// binding:
//
//   * QueryDisplayConfig answered 87 (ERROR_INVALID_PARAMETER) for
//     QDC_ALL_PATHS and QDC_ONLY_ACTIVE_PATHS, and worked for
//     QDC_DATABASE_CURRENT - a difference that had nothing to do with
//     the flags;
//   * SetDisplayConfig answered 87 for a topology that was already
//     valid, and sometimes crashed with an AccessViolationException;
//   * and the virtual display could never be found.
//
// The reason for the 87 is in the SDK's own annotation of the last
// QueryDisplayConfig parameter:
//
//   _When_(!(flags & QDC_DATABASE_CURRENT), _Pre_null_)
//   _When_(flags & QDC_DATABASE_CURRENT, _Out_)
//       DISPLAYCONFIG_TOPOLOGY_ID* currentTopologyId);
//
// The old binding passed a POINTER where NULL was required, so the
// API rejected every query that did not want a topology id - which is
// every query except the one that was least useful.  With the
// topology id passed as NULL, QDC_ALL_PATHS works and returns the
// database's 57 paths, among them the virtual display's INACTIVE path.
//
// The other two struct fixes are still in place, because they were
// wrong too:
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
//
// THE FLAGS THAT ARE ACCEPTED
// ---------------------------
// SDC_ALLOW_PATH_ORDER_CHANGES is REJECTED with 87 when a path is
// being added that was not in the previous topology - which is
// exactly what the attach does.  The flag sets are therefore tried in
// turn, and the one that validates is the one that is applied.
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
    // The query flags - wingdi.h.
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
    /// The flag sets the apply is tried with, in order.
    /// </summary>
    /// <remarks>
    /// The first set - the one with SDC_ALLOW_PATH_ORDER_CHANGES - is
    /// rejected with 87 when a path is added that was not in the
    /// previous topology.  The set that VALIDATES is the set that is
    /// applied, so the two can never disagree.
    ///
    /// SDC_SAVE_TO_DATABASE is deliberately NOT used.  Without it the
    /// new arrangement lives only in the session, which is what makes
    /// the detach reversible and keeps the virtual display out of the
    /// user's display settings.
    /// </remarks>
    private static readonly uint[] ApplyFlagSets =
    {
        SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_ALLOW_PATH_ORDER_CHANGES,
        SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES,
        SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG,
        SDC_APPLY,
    };

    // -----------------------------------------------------------------
    // The mode and device-info constants - wingdi.h.
    // -----------------------------------------------------------------

    private const uint MODE_INFO_TYPE_SOURCE = 1;
    private const uint MODE_INFO_TYPE_TARGET = 2;

    private const uint DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3;

    private const uint PIXELFORMAT_32BPP = 4;

    /// <summary>DISPLAYCONFIG_PATH_ACTIVE.</summary>
    private const uint PATH_ACTIVE = 0x00000001;

    /// <summary>The virtual display's EDID fingerprint.</summary>
    private const string VirtualEdidFingerprint = "MTT1337";

    // -----------------------------------------------------------------
    // The API - with the SDK's signatures, exactly.
    // -----------------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathInfoArraySize,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint modeInfoArraySize,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static extern int QueryDisplayConfigWithTopology(
        uint flags,
        ref uint pathInfoArraySize,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint modeInfoArraySize,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        ref uint currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "SetDisplayConfig")]
    private static extern int SetDisplayConfig(
        uint pathInfoArraySize,
        [In] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        uint modeInfoArraySize,
        [In] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        uint flags);

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
        => QueryAny(QDC_ALL_PATHS, QDC_ONLY_ACTIVE_PATHS, QDC_DATABASE_CURRENT) is not null;

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
        var topology = QueryAny(QDC_ALL_PATHS, QDC_DATABASE_CURRENT);
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
        var topology = QueryAny(QDC_ONLY_ACTIVE_PATHS, QDC_ALL_PATHS);
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
    /// where it lives while it is detached - and its modes are BUILT,
    /// because Windows has never recorded any: a display that has
    /// never been on the desktop has no source or target mode, and its
    /// modeInfoIdx fields are 0xFFFFFFFF.
    ///
    /// The target mode comes from the target's preferred mode, which
    /// carries the full VIDEO_SIGNAL_INFO.  The source mode is the
    /// same size in 32bpp, at the requested position.
    ///
    /// The active paths are then sent back with that path added, so the
    /// adapter is never torn down and the operation is reversible.
    /// </remarks>
    /// <returns>True when the display has an active path afterwards.</returns>
    public static bool Attach(int x, int y)
    {
        var database = QueryAny(QDC_ALL_PATHS, QDC_DATABASE_CURRENT);
        if (database is null) return false;

        if (!TryFindVirtualPath(database, out var vpath, out var vSourceIdx, out var vTargetIdx))
            return false;

        // The paths that are on the desktop right now.
        var active = QueryAny(QDC_ONLY_ACTIVE_PATHS, QDC_DATABASE_CURRENT);
        if (active is null) return false;

        var modes = new List<DISPLAYCONFIG_MODE_INFO>(database.Modes);
        var paths = new List<DISPLAYCONFIG_PATH_INFO>();

        foreach (var p in active.Paths)
        {
            // Only the ACTIVE paths are carried over - an inactive
            // path in the database must not be re-added to the
            // desktop by accident.
            if ((p.flags & PATH_ACTIVE) == 0) continue;
            paths.Add(p);
        }

        uint newSourceIdx;
        uint newTargetIdx;

        if (vSourceIdx < 0 || vTargetIdx < 0 ||
            vpath.sourceInfo.modeInfoIdx == uint.MaxValue ||
            vpath.targetInfo.modeInfoIdx == uint.MaxValue)
        {
            // No recorded modes - build them from the preferred mode.
            var preferred = new DISPLAYCONFIG_TARGET_PREFERRED_MODE
            {
                header = new DEVICE_INFO_HEADER
                {
                    type = DEVICE_INFO_GET_TARGET_PREFERRED_MODE,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_PREFERRED_MODE>(),
                    adapterId = vpath.targetInfo.adapterId,
                    id = vpath.targetInfo.id
                }
            };
            if (GetPreferredModeInfo(ref preferred) != 0) return false;

            newTargetIdx = (uint)modes.Count;
            modes.Add(new DISPLAYCONFIG_MODE_INFO
            {
                infoType = MODE_INFO_TYPE_TARGET,
                id = vpath.targetInfo.id,
                adapterId = vpath.targetInfo.adapterId,
                targetMode = preferred.targetMode
            });

            newSourceIdx = (uint)modes.Count;
            modes.Add(new DISPLAYCONFIG_MODE_INFO
            {
                infoType = MODE_INFO_TYPE_SOURCE,
                id = vpath.sourceInfo.id,
                adapterId = vpath.sourceInfo.adapterId,
                sourceMode = new SOURCE_MODE
                {
                    Width = preferred.width,
                    Height = preferred.height,
                    PixelFormat = PIXELFORMAT_32BPP,
                    position = new POINTL { x = x, y = y }
                }
            });
        }
        else
        {
            // The path's modes come from the database query, so they
            // have to be carried over into the mode array that is sent
            // back.
            var sourceMode = database.Modes[vSourceIdx];
            sourceMode.sourceMode.position = new POINTL { x = x, y = y };

            newSourceIdx = (uint)modes.Count;
            modes.Add(sourceMode);

            newTargetIdx = (uint)modes.Count;
            modes.Add(database.Modes[vTargetIdx]);
        }

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
        var active = QueryAny(QDC_ONLY_ACTIVE_PATHS, QDC_DATABASE_CURRENT);
        if (active is null) return false;

        var kept = new List<DISPLAYCONFIG_PATH_INFO>();
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
        var modes = new List<DISPLAYCONFIG_MODE_INFO>(active.Modes);
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
    /// The flag sets are tried in turn.  Each set is validated first,
    /// with SDC_VALIDATE, and the set that validates is the set that
    /// is applied - so a configuration the OS rejects is never
    /// applied, and the desktop is never left half-changed.
    /// </remarks>
    private static bool Apply(
        List<DISPLAYCONFIG_PATH_INFO> paths, List<DISPLAYCONFIG_MODE_INFO> modes)
    {
        var pathArray = paths.ToArray();
        var modeArray = modes.ToArray();

        foreach (var flags in ApplyFlagSets)
        {
            var validate = SetDisplayConfig(
                (uint)pathArray.Length, pathArray,
                (uint)modeArray.Length, modeArray,
                flags & ~SDC_APPLY | SDC_VALIDATE);
            if (validate != 0) continue;

            return SetDisplayConfig(
                (uint)pathArray.Length, pathArray,
                (uint)modeArray.Length, modeArray,
                flags) == 0;
        }

        return false;
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
    }

    /// <summary>
    /// Query the display topology.
    /// </summary>
    /// <remarks>
    /// The currentTopologyId parameter is NULL unless
    /// QDC_DATABASE_CURRENT is set - the SDK requires it, and passing
    /// a pointer where NULL is required is what made every other
    /// query answer 87.
    /// </remarks>
    private static Topology? Query(uint flags)
    {
        var rc = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
        if (rc != 0) return null;

        if (pathCount == 0 || modeCount == 0) return null;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        int result;
        if ((flags & QDC_DATABASE_CURRENT) != 0)
        {
            uint topologyId = 0;
            result = QueryDisplayConfigWithTopology(
                flags, ref pathCount, paths, ref modeCount, modes, ref topologyId);
        }
        else
        {
            result = QueryDisplayConfig(
                flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        }

        if (result != 0) return null;

        Array.Resize(ref paths, (int)pathCount);
        Array.Resize(ref modes, (int)modeCount);
        return new Topology { Paths = paths, Modes = modes };
    }

    /// <summary>
    /// Query with the first flag that the machine accepts.
    /// </summary>
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
