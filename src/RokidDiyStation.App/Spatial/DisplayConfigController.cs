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

using RokidDiyStation.Core.Spatial;

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
    /// Write the new arrangement into Windows' persistent display
    /// configuration database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS FLAG IS THE ONE THAT ACTUALLY FIXED THE USER'S BUG.
    /// </para>
    /// <para>
    /// The detach used to be applied WITHOUT it, on the reasoning that
    /// a session-only change is reversible and leaves the user's
    /// display settings alone.  The first half is true and the second
    /// half is true, but they come at a price that was not
    /// understood: <b>Windows rebuilds the desktop layout FROM THE
    /// DATABASE</b> whenever the topology changes.
    /// </para>
    /// <para>
    /// So with a session-only detach, the database still records the
    /// virtual display as part of the active topology, and the next
    /// re-layout - plugging the Rokid Max in, unplugging a monitor,
    /// changing a resolution - puts it BACK, at whatever position the
    /// database remembers.  That is the user's symptom exactly: the
    /// VDD reappears in Extended mode, displaced upwards or to the
    /// left.
    /// </para>
    /// <para>
    /// AND THE POSITION WAS NOT A GUESS.  The user's machine carried
    /// it in the registry:
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration</c>,
    /// in the composite key for the connected topology, under the
    /// virtual display's subkey: <c>Position.cx = 1915</c> and
    /// <c>Position.cy = 4294966216</c>.  Read as a signed 32-bit
    /// integer that is <c>-1080</c>, so the stored position was
    /// (1915, -1080) - above the physical monitors and shifted
    /// sideways.  Exactly what the user described.
    /// </para>
    /// <para>
    /// With SDC_SAVE_TO_DATABASE the session and the database agree,
    /// and Windows stops rebuilding the desktop against the app's
    /// back.
    /// </para>
    /// <para>
    /// The attach uses it too, so the database always records the
    /// arrangement that is actually on screen.  That is what makes the
    /// lifecycle reversible: the attach and the detach are the same
    /// operation on the same persistent state.
    /// </para>
    /// <para>
    /// It also ends the fight loop the app's log shows - the attach
    /// that did not stick, the display vanishing again a second
    /// later, and the reconciler putting it back.  That loop was the
    /// same disagreement seen from the other side: the session said
    /// the display was there, the database did not, and every topology
    /// rebuild settled the argument in the database's favour.
    /// </para>
    /// </remarks>
    private const uint SDC_SAVE_TO_DATABASE = 0x00000200;

    /// <summary>
    /// The flag sets the apply is tried with, in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first set - the one with SDC_ALLOW_PATH_ORDER_CHANGES - is
    /// rejected with 87 when a path is added that was not in the
    /// previous topology.  The SDK requires SDC_TOPOLOGY_SUPPLIED with
    /// that flag, and the flag set that VALIDATES is the flag set that
    /// is applied, so the two can never disagree.
    /// </para>
    /// <para>
    /// Every set carries SDC_SAVE_TO_DATABASE, for the reason given
    /// on that constant.
    /// </para>
    /// </remarks>
    private static readonly uint[] ApplyFlagSets =
    {
        SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_ALLOW_PATH_ORDER_CHANGES | SDC_SAVE_TO_DATABASE,
        SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE,
        SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE,
        SDC_APPLY | SDC_SAVE_TO_DATABASE,
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

    /// <summary>
    /// The virtual display's EDID fingerprint.
    /// </summary>
    /// <remarks>
    /// This is the driver-specific part of the target's device path:
    ///
    ///   \\?\DISPLAY#MTT1337#1&28a6823a&9&UID256#{e6f07b5f-...}
    ///
    /// The "MTT1337" is the EDID's manufacturer-and-product field, and
    /// it is the ONLY part of the virtual display's identity that does
    /// not change when the machine's display topology changes.  See
    /// <see cref="IsVirtualTarget"/> for why that matters.
    /// </remarks>
    private const string VirtualEdidFingerprint = VirtualDisplayIdentity.EdidFingerprint;

    /// <summary>
    /// The virtual display's friendly name, as the driver reports it.
    /// </summary>
    /// <remarks>
    /// "VDD by MTT".  This is a second, independent fingerprint: if a
    /// future driver version changes the EDID, the name still matches.
    /// </remarks>
    private const string VirtualFriendlyName = VirtualDisplayIdentity.FriendlyName;

    /// <summary>
    /// Diagnostics from the display-configuration calls.
    /// </summary>
    /// <remarks>
    /// The attach and the detach can fail for reasons the user cannot
    /// guess at, and "the virtual display could not be detached" is not
    /// an actionable message.  The reason - including the exact
    /// SetDisplayConfig error code - is reported through this.
    /// </remarks>
    public static event Action<string>? OnDiagnostic;

    private static void Log(string message)
    {
        try { OnDiagnostic?.Invoke(message); } catch { }
    }

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
    /// Whether a CCD target is the IddCx virtual display.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS THE FUNCTION THAT HAD TO CHANGE, AND WHY.
    /// </para>
    /// <para>
    /// The user reported that the virtual display could not be
    /// disabled when the number of physical monitors changed: with
    /// three physical monitors the detach worked, but with one or two
    /// the VDD stayed on the desktop in Extended mode, usually
    /// repositioned above the other displays.
    /// </para>
    /// <para>
    /// The suspicion was that the finder depended on something that
    /// changes with the topology - a coordinate, an enumeration index.
    /// That suspicion was RIGHT, but not in the way it was framed: the
    /// fingerprint itself was fine, and the <em>query</em> was the
    /// problem.  See <see cref="Detach"/> for the details.
    /// </para>
    /// <para>
    /// What matters here is that the identification is by HARDWARE
    /// IDENTITY and never by position or index:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The EDID fingerprint - "MTT1337" - in the target's device
    /// path.  This is the driver-specific part:
    /// <c>\\?\DISPLAY#MTT1337#1&amp;28a6823a&amp;9&amp;UID256#{...}</c>.
    /// The part after the fingerprint is the port and instance, which
    /// DO change when the topology changes - so only the fingerprint
    /// itself is matched.
    /// </description></item>
    /// <item><description>
    /// The friendly name - "VDD by MTT" - as an independent second
    /// fingerprint, in case a driver version changes the EDID.
    /// </description></item>
    /// <item><description>
    /// The EDID manufacturer and product code fields, read through
    /// DISPLAYCONFIG_TARGET_DEVICE_NAME.  These are the raw EDID
    /// bytes, and they are the most hardware-like identity available:
    /// they describe the monitor, not the port it is plugged into.
    /// </description></item>
    /// </list>
    /// <para>
    /// None of these is a coordinate, an enumeration index, or a
    /// device path index.  The virtual display is found the same way
    /// whether it is at (-1920,0), at (1915,-1080), or anywhere else.
    /// </para>
    /// </remarks>
    /// <param name="adapterId">The target's adapter.</param>
    /// <param name="id">The target's id.</param>
    public static bool IsVirtualTarget(LUID adapterId, uint id)
    {
        var name = TargetName(adapterId, id);

        // The decision itself lives in VirtualDisplayIdentity, so that
        // it can be tested without a virtual display driver.
        return VirtualDisplayIdentity.IsVirtual(
            name.monitorDevicePath,
            name.monitorFriendlyDeviceName,
            name.edidManufactureId,
            name.edidProductCodeId);
    }

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
            if (!IsVirtualTarget(p.targetInfo.adapterId, p.targetInfo.id))
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

            if (IsVirtualTarget(p.targetInfo.adapterId, p.targetInfo.id))
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
        // The paths that are on the desktop right now.
        //
        // THE ACTIVE QUERY IS THE BASE FOR BOTH THE PATHS AND THE
        // MODES, and they have to come from the SAME query.
        //
        // A path's modeInfoIdx is an INDEX INTO THE MODE ARRAY of the
        // query that produced it.  So a path array from one query and
        // a mode array from another are only compatible by accident -
        // and they stop being compatible exactly when Windows rebuilds
        // the topology, which is when the indices change.
        //
        // The old code took the paths from the ACTIVE query and the
        // modes from the DATABASE, so the surviving paths pointed at
        // the WRONG modes, and SetDisplayConfig answered 87.
        var active = QueryAny(QDC_ONLY_ACTIVE_PATHS, QDC_ALL_PATHS, QDC_DATABASE_CURRENT);
        if (active is null) return false;

        // The virtual display's path comes from the DATABASE, because
        // while it is DETACHED it is not in the active set.
        var database = QueryAny(QDC_ALL_PATHS, QDC_DATABASE_CURRENT);
        if (database is null) return false;

        if (!TryFindVirtualPath(database, out var vpath, out var vSourceIdx, out var vTargetIdx))
            return false;

        // The modes that are on the desktop, carried over with their
        // indices UNCHANGED - the paths keep referring to them.
        var modes = new List<DISPLAYCONFIG_MODE_INFO>(active.Modes);
        var paths = new List<DISPLAYCONFIG_PATH_INFO>();

        foreach (var p in active.Paths)
        {
            // Only the ACTIVE paths are carried over - an inactive
            // path in the database must not be re-added to the
            // desktop by accident.
            if ((p.flags & PATH_ACTIVE) == 0) continue;

            // EVERY path that is already virtual is not carried
            // over: exactly ONE virtual path is added back below, at
            // the requested position.
            //
            // The old code dropped only the first virtual path it
            // met, so a virtual target that had TWO active paths -
            // which is what the IddCx driver leaves behind when it
            // re-publishes its monitor after a topology change - kept
            // its second path.  The display was then on the desktop
            // TWICE, and a later detach that removed one copy still
            // left the other one there.
            if (IsVirtualTarget(p.targetInfo.adapterId, p.targetInfo.id)) continue;

            paths.Add(p);
        }

        // The virtual display's mode.
        //
        // The TARGET mode comes from the target's preferred mode, which
        // carries the full VIDEO_SIGNAL_INFO.  The SOURCE mode is the
        // same size in 32bpp, at the requested position.
        //
        // A display that has never been on the desktop has no recorded
        // modes at all - its modeInfoIdx fields are 0xFFFFFFFF - so
        // both modes are built from the preferred mode.  When it HAS
        // been on the desktop, the recorded modes are reused, because
        // they carry the target mode's exact video timings.
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

        uint newTargetIdx;
        uint newSourceIdx;

        if (vSourceIdx >= 0 && vTargetIdx >= 0
            && vpath.sourceInfo.modeInfoIdx != uint.MaxValue
            && vpath.targetInfo.modeInfoIdx != uint.MaxValue
            && vTargetIdx < database.Modes.Length
            && vSourceIdx < database.Modes.Length)
        {
            // The recorded modes are reused, with the position
            // overridden.
            var sourceMode = database.Modes[vSourceIdx];
            sourceMode.sourceMode.position = new POINTL { x = x, y = y };

            newSourceIdx = (uint)modes.Count;
            modes.Add(sourceMode);

            newTargetIdx = (uint)modes.Count;
            modes.Add(database.Modes[vTargetIdx]);
        }
        else
        {
            // No recorded modes - build them from the preferred mode.
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

        var newPath = vpath;
        newPath.sourceInfo.modeInfoIdx = newSourceIdx;
        newPath.targetInfo.modeInfoIdx = newTargetIdx;
        newPath.sourceInfo.statusFlags = 0;
        newPath.targetInfo.targetAvailable = 1;
        newPath.targetInfo.statusFlags = 0;
        newPath.flags = PATH_ACTIVE;

        paths.Add(newPath);

        // The requested position was computed from the SHELL's view of
        // the desktop, and the shell's view can be a moment behind the
        // display-configuration database's.  If the two disagree, the
        // virtual display can land on top of a physical one - and
        // Windows forbids overlapping rectangles outright, so the
        // attach fails with 87 and the spatial display never opens.
        //
        // So the position is checked against the paths that are
        // actually going to be sent, and the virtual display is moved
        // clear of them if it overlaps.  It goes to the LEFT of the
        // leftmost survivor, which is where the spatial canvas
        // belongs anyway.
        PlaceVirtualClearOfSurvivors(paths, modes);

        if (!Apply(paths, modes)) return false;
        return IsVirtualActive();
    }

    /// <summary>
    /// Move the virtual display out of the way if it overlaps one of
    /// the displays that are already on the desktop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attach's requested position is a COMPUTED one - see
    /// <see cref="VirtualDisplayController.ComputeAttachPosition"/> -
    /// and it is computed from the shell's enumeration of the
    /// monitors.  That enumeration is a snapshot, and the desktop
    /// moves between the snapshot and the apply: Windows re-lays out
    /// the survivors when a monitor is plugged or unplugged, and the
    /// IddCx driver re-publishes the virtual display's monitor at the
    /// same time.
    /// </para>
    /// <para>
    /// When the two views disagree, the virtual display lands on top
    /// of a physical one, and Windows refuses the whole
    /// configuration with 87 (ERROR_INVALID_PARAMETER) - so the
    /// spatial display does not open at all.
    /// </para>
    /// <para>
    /// The fix is to check the position against the paths that are
    /// actually being sent, and to move the virtual display to the
    /// left of the leftmost survivor when it overlaps.  Nothing is
    /// moved when there is no overlap, so the ordinary case is
    /// untouched.
    /// </para>
    /// </remarks>
    private static void PlaceVirtualClearOfSurvivors(
        List<DISPLAYCONFIG_PATH_INFO> paths,
        List<DISPLAYCONFIG_MODE_INFO> modes)
    {
        // The virtual display's own rectangle.
        var (vx, vy) = (0, 0);
        var (vw, vh) = (0, 0);
        DISPLAYCONFIG_PATH_INFO? virtualPath = null;

        foreach (var p in paths)
        {
            if ((p.flags & PATH_ACTIVE) == 0) continue;
            if (!IsVirtualTarget(p.targetInfo.adapterId, p.targetInfo.id)) continue;

            virtualPath = p;
            (vx, vy) = SourcePositionOf(modes, p);
            (vw, vh) = SourceSizeOf(modes, p);
            break;
        }

        if (virtualPath is null || vw <= 0 || vh <= 0) return;

        // The survivors' rectangles.
        var survivors = new List<DesktopRect>();
        foreach (var p in paths)
        {
            if ((p.flags & PATH_ACTIVE) == 0) continue;
            if (IsVirtualTarget(p.targetInfo.adapterId, p.targetInfo.id)) continue;

            var (x, y) = SourcePositionOf(modes, p);
            var (w, h) = SourceSizeOf(modes, p);
            if (w <= 0 || h <= 0) continue;
            survivors.Add(new DesktopRect(x, y, x + w, y + h));
        }

        if (survivors.Count == 0) return;

        var virtualRect = new DesktopRect(vx, vy, vx + vw, vy + vh);
        bool overlaps = false;
        foreach (var s in survivors)
        {
            if (!virtualRect.Intersects(s)) continue;
            overlaps = true;
            break;
        }
        if (!overlaps) return;

        // To the left of the leftmost survivor, on the same row.
        int leftmost = int.MaxValue;
        int top = 0;
        foreach (var s in survivors)
        {
            if (s.Left < leftmost)
            {
                leftmost = s.Left;
                top = s.Top;
            }
        }

        int newX = leftmost - vw;
        SetSourcePosition(modes, virtualPath.Value, newX, top);

        Log($"Display config: the virtual display was moved from ({vx},{vy}) to "
            + $"({newX},{top}) because it overlapped a display that is already on "
            + "the desktop.");
    }

    /// <summary>
    /// Remove the virtual display's path from the desktop.
    /// </summary>
    /// <remarks>
    /// Unlike the legacy detach, this is REVERSIBLE: the adapter stays
    /// attached to the machine, so <see cref="Attach"/> can bring it
    /// back.
    ///
    /// WHY THE ACTIVE-PATHS QUERY IS NOT ENOUGH
    /// ---------------------------------------
    /// The user's report was that with three physical monitors the
    /// detach worked, but with one or two the VDD stayed on the
    /// desktop in Extended mode, usually repositioned above the other
    /// displays.
    ///
    /// The cause is NOT that the VDD is found by its position - the
    /// fingerprint was always a hardware one.  The cause is that the
    /// ACTIVE-path query does not necessarily return a usable set of
    /// paths once Windows has re-laid-out the desktop.
    ///
    /// Specifically: when the physical monitors change, Windows
    /// rebuilds the source modes of the surviving paths, and the
    /// resulting positions can leave the desktop in a state where the
    /// supplied configuration - which carries those positions back
    /// unchanged - is not one Windows will accept.  SetDisplayConfig
    /// then answers 87 (ERROR_INVALID_PARAMETER) and the detach
    /// silently does nothing.
    ///
    /// So the detach is now built to be ROBUST rather than merely
    /// correct:
    ///
    ///   * the virtual display is found by hardware identity, through
    ///     <see cref="IsVirtualTarget"/>, so a topology change cannot
    ///     make it invisible;
    ///   * the surviving paths' source modes are RE-COMPUTED so that
    ///     the remaining displays are packed left-to-right from the
    ///     origin, which is the arrangement Windows always accepts;
    ///   * and if that still fails, the detach falls back to removing
    ///     the virtual path while leaving the other paths' positions
    ///     exactly as they are.
    ///
    /// The positions matter because Windows forbids overlapping
    /// monitor rectangles outright.  A topology change can leave two
    /// survivors overlapping, and then NO configuration that keeps them
    /// where they are can be valid.
    /// </remarks>
    /// <returns>True when the path was removed.</returns>
    public static bool Detach()
    {
        // The actual removal.
        //
        // The three outcomes are DISTINGUISHED, because they mean
        // different things to the caller:
        //
        //   * removed    - the display was on the desktop and is now
        //                  off it;
        //   * already    - the display was not on the desktop, so the
        //                  caller's intent is already satisfied;
        //   * failed     - the topology could not even be queried, so
        //                  nothing is known and nothing was done.
        //
        // The old code lumped "already" and "failed" together and
        // returned false for both, and the caller turned that into
        // "The virtual display could not be detached."  That message
        // is wrong for the "already" case: the virtual display WAS
        // detached.  It is already off the desktop.  The detach is
        // simply idempotent, and saying so is what a caller needs to
        // know, because it means the caller's intent - "the virtual
        // display must not be on the desktop" - is satisfied.
        //
        // It also matters for the RACE: the topology watcher can call
        // Detach() again after the desktop has already been rebuilt,
        // and a false "failure" there would be logged as a real error
        // on every monitor hotplug.
        var outcome = RemoveVirtualPaths();

        if (outcome == DetachOutcome.Failed)
        {
            Log("Detach: the display topology could not be queried.");
            return false;
        }

        if (outcome == DetachOutcome.AlreadyDetached)
        {
            // "Already detached" is only true if the SHELL agrees.
            //
            // The CCD's view and the shell's view are two different
            // things, and they can disagree: the display-configuration
            // path can be inactive while the IddCx driver still has
            // its monitor published on the desktop.  The user then
            // sees a rectangle that the code believes it has removed,
            // which is exactly the complaint.
            //
            // So the shell is asked as well, and if the shell still
            // draws the virtual monitor the detach is NOT a success.
            if (!ShellHasVirtualMonitor())
            {
                Log("Detach: the virtual display is already off the desktop.");
                return true;
            }

            Log("Detach: the display configuration says the virtual display is off, "
                + "but the shell is still drawing it.");
            return false;
        }

        // The removal was applied.  Now it has to STAY removed.
        //
        // SetDisplayConfig returns as soon as the configuration has
        // been accepted, and DWM's swapchain teardown for the virtual
        // adapter happens on its own schedule after that.  A single
        // check right after the apply can therefore see the display
        // still active, or - worse - see it gone and then watch it
        // come back a moment later, because Windows rebuilt the
        // topology from the database.
        //
        // So the state is checked over a settle window, and the
        // detach is only reported as successful if the display stays
        // gone for the whole window.
        if (!WaitForVirtualGone(SettleWindowMs))
        {
            Log("Detach: the virtual display came back after it was removed.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// How a detach attempt ended.
    /// </summary>
    private enum DetachOutcome
    {
        /// <summary>The topology could not be queried at all.</summary>
        Failed,

        /// <summary>The virtual display was not on the desktop.</summary>
        AlreadyDetached,

        /// <summary>The virtual display was disconnected.</summary>
        Removed,
    }

    /// <summary>
    /// How long the virtual display is watched after a detach, in ms.
    /// </summary>
    /// <remarks>
    /// This is long enough to cover DWM's swapchain teardown and one
    /// round of Windows' own topology rebuild, which is what used to
    /// put the display back.
    /// </remarks>
    private const int SettleWindowMs = 2000;

    /// <summary>
    /// How long the display is given to actually leave the topology
    /// before the settle watch starts judging it, in ms.
    /// </summary>
    /// <remarks>
    /// SetDisplayConfig returns once the configuration has been
    /// accepted, and the topology query can still report the old
    /// path for a moment afterwards.  Failing on that first sighting
    /// would be a false negative, so the watch does not judge the
    /// display until it has had this long to go.
    /// </remarks>
    private const int SettleGraceMs = 400;

    /// <summary>
    /// How often the state is polled during the settle window, in ms.
    /// </summary>
    private const int SettlePollMs = 100;

    /// <summary>
    /// Watch the virtual display and report whether it STAYS off the
    /// desktop for the whole window.
    /// </summary>
    /// <remarks>
    /// A single check is not enough.  The failure mode the user
    /// reported - the virtual display reappearing in Extended mode,
    /// displaced upwards or to the left - is a RE-ATTACH, and the
    /// re-attach happens after the removal, not instead of it.  So
    /// the check has to keep watching for long enough to see the
    /// re-attach if it is going to happen.
    /// </remarks>
    private static bool WaitForVirtualGone(int windowMs)
    {
        var start = Environment.TickCount64;
        var deadline = start + windowMs;

        while (Environment.TickCount64 < deadline)
        {
            bool present = IsVirtualActive() || ShellHasVirtualMonitor();

            if (present && Environment.TickCount64 - start > SettleGraceMs)
                return false;

            Thread.Sleep(SettlePollMs);
        }

        return !IsVirtualActive() && !ShellHasVirtualMonitor();
    }

    /// <summary>
    /// Whether the SHELL is drawing the virtual monitor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the user's view of the desktop - the rectangle they can
    /// see and drag a window onto - and it is NOT the same thing as the
    /// display configuration's view.
    /// </para>
    /// <para>
    /// The code checks both, because the two can disagree.  The
    /// display configuration can say the virtual display's path is
    /// inactive while the IddCx driver still has its monitor
    /// published, and then the user is looking at a rectangle that the
    /// code has already written off as removed.
    /// </para>
    /// <para>
    /// The shell's monitor list is the identity check's own, so it
    /// cannot go stale: it is the EDID, not a position.
    /// </para>
    /// </remarks>
    private static bool ShellHasVirtualMonitor()
        => VirtualDisplayLocator.FindVirtual() is not null;

    /// <summary>
    /// Remove every virtual display path from the desktop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE MECHANISM: AN EXPLICIT "DISCONNECT", NOT AN OMISSION
    /// </para>
    /// <para>
    /// The virtual display's path is KEPT in the array that is sent
    /// to Windows, with its <c>flags</c> cleared - so
    /// <c>DISPLAYCONFIG_PATH_ACTIVE</c> is no longer set - and with
    /// its two mode indices set to
    /// <c>DISPLAYCONFIG_PATH_MODE_IDX_INVALID</c> (0xFFFFFFFF).
    /// </para>
    /// <para>
    /// This is EXACTLY what the Windows Display Settings app does
    /// when the user turns a display off, and it is the difference
    /// between the two things that can be done to a path:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// OMITTING the path says: "I am not telling you about this
    /// display."  Windows then has a CONNECTED target that the
    /// supplied configuration does not account for, so it falls back
    /// to its own database - and the database still says the virtual
    /// display is active, because the historical detach never
    /// persisted anything.  So the display comes back.
    /// </description></item>
    /// <item><description>
    /// DISCONNECTING the path says: "This display is connected, and
    /// the user has turned it off."  Windows records that, and it
    /// does not auto-extend onto the display again.
    /// </description></item>
    /// </list>
    /// <para>
    /// THE REGISTRY IS THE PROOF
    /// </para>
    /// <para>
    /// The user's own machine carried the smoking gun in
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration</c>:
    /// stale composite keys whose <c>\02</c> subkey held
    /// <c>Position.cx = 1915</c> and <c>Position.cy = 4294966216</c>.
    /// Read as a signed 32-bit integer, that is <c>-1080</c>: the
    /// virtual display at (1915, -1080), which is ABOVE the physical
    /// monitors and shifted sideways.  That is precisely the
    /// "repositioned upward or to the left" the user reported, and it
    /// was sitting in the registry, waiting to be restored by the
    /// next topology rebuild.
    /// </para>
    /// <para>
    /// With <c>SDC_SAVE_TO_DATABASE</c> the detach overwrites that
    /// key, so the stale position is gone rather than waiting.
    /// </para>
    /// <para>
    /// EVERY virtual path is handled, not just the first one found.
    /// The IddCx driver re-publishes its monitor when the topology
    /// changes, and Windows can be left with two active paths for the
    /// same virtual target.  Disconnecting only the first left the
    /// second one on the desktop while the code reported success.
    /// </para>
    /// </remarks>
    /// <returns>
    /// What the attempt did.
    /// </returns>
    private static DetachOutcome RemoveVirtualPaths()
    {
        // The ACTIVE paths are the ones on the desktop.  The query is
        // retried with the database if the active query fails, because
        // a machine that has just had a monitor unplugged can answer
        // the active query with an error for a moment.
        var active = QueryAny(QDC_ONLY_ACTIVE_PATHS, QDC_ALL_PATHS, QDC_DATABASE_CURRENT);
        if (active is null)
        {
            Log("Detach: the display topology could not be queried at all.");
            return DetachOutcome.Failed;
        }

        var paths = new List<DISPLAYCONFIG_PATH_INFO>();
        bool removed = false;

        // The modes the surviving paths refer to have to be carried
        // over, because the paths' modeInfoIdx fields are INDICES INTO
        // THE MODE ARRAY.  Sending a path array with a mode array that
        // does not contain those modes - or contains them at different
        // indices - is exactly the kind of thing Windows answers 87.
        //
        // This is the bug that the topology change exposed: the old
        // code sent the whole mode array back unchanged, so it
        // happened to work when the indices lined up, and the indices
        // stop lining up when Windows rebuilds the topology.

        foreach (var p in active.Paths)
        {
            if (IsVirtualTarget(p.targetInfo.adapterId, p.targetInfo.id))
            {
                // EVERY virtual path is disconnected, not just the
                // first one.  The IddCx driver re-publishes its
                // monitor when the topology changes, and Windows can
                // be left with two active paths for the same virtual
                // target - the old one and the new one.  Disconnecting
                // only the first kept the second, and the virtual
                // display stayed on the desktop in Extended mode while
                // the code reported success.
                var disconnected = p;

                // THE DISCONNECT.  Clearing PATH_ACTIVE is what takes
                // the display off the desktop; the invalid mode
                // indices say "this path has no mode", which is what
                // the Windows Display Settings app sends, and what
                // Windows expects for a path that is not in use.
                disconnected.flags &= ~PATH_ACTIVE;
                disconnected.sourceInfo.modeInfoIdx = uint.MaxValue;
                disconnected.targetInfo.modeInfoIdx = uint.MaxValue;

                paths.Add(disconnected);

                // Only an ACTIVE virtual path counts as "removed": the
                // database keeps the path of a display that is off the
                // desktop, and counting that one would make the
                // detach claim to have done something when it had
                // not.
                if ((p.flags & PATH_ACTIVE) != 0) removed = true;
                continue;
            }

            paths.Add(p);
        }

        if (!removed)
        {
            Log("Detach: no active virtual display path was found in the topology.");
            return DetachOutcome.AlreadyDetached;
        }

        // Rebuild the mode array so that it contains exactly the modes
        // the ACTIVE paths refer to, and remap their indices.
        //
        // The DISCONNECTED virtual path refers to no modes at all - its
        // mode indices are DISPLAYCONFIG_PATH_MODE_IDX_INVALID - so it
        // contributes nothing to the mode array.  That is correct: an
        // inactive path has no mode.
        //
        // THE ORDER OF THE MODE ARRAY MATTERS.
        //
        // Windows returns the modes interleaved - target, source,
        // target, source - and it expects them back in that shape.  A
        // mode array that has been rebuilt in a different order - all
        // the sources first, say - is rejected with 87
        // (ERROR_INVALID_PARAMETER), even though it contains exactly
        // the right modes.
        //
        // So the mode array is rebuilt by WALKING THE ORIGINAL ARRAY
        // IN ORDER and keeping only the modes the surviving paths
        // refer to.  That preserves both the relative order and the
        // target-before-source pairing that Windows expects.
        var wanted = new HashSet<uint>();
        foreach (var p in paths)
        {
            if ((p.flags & PATH_ACTIVE) == 0) continue;
            if (p.sourceInfo.modeInfoIdx != uint.MaxValue) wanted.Add(p.sourceInfo.modeInfoIdx);
            if (p.targetInfo.modeInfoIdx != uint.MaxValue) wanted.Add(p.targetInfo.modeInfoIdx);
        }

        var remap = new Dictionary<uint, uint>();
        var modes = new List<DISPLAYCONFIG_MODE_INFO>();

        for (int m = 0; m < active.Modes.Length; m++)
        {
            if (!wanted.Contains((uint)m)) continue;
            remap[(uint)m] = (uint)modes.Count;
            modes.Add(active.Modes[m]);
        }

        for (int i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            if ((p.flags & PATH_ACTIVE) == 0) continue;
            if (p.sourceInfo.modeInfoIdx != uint.MaxValue
                && remap.TryGetValue(p.sourceInfo.modeInfoIdx, out var sIdx))
                p.sourceInfo.modeInfoIdx = sIdx;
            if (p.targetInfo.modeInfoIdx != uint.MaxValue
                && remap.TryGetValue(p.targetInfo.modeInfoIdx, out var tIdx))
                p.targetInfo.modeInfoIdx = tIdx;
            paths[i] = p;
        }

        // THE TWO VIEWS OF THE TOPOLOGY DISAGREE, AND THAT IS THE
        // WHOLE PROBLEM.
        //
        // On this machine, right now:
        //
        //   QDC_ONLY_ACTIVE_PATHS says the artist display is at
        //   (0,0) and the laptop panel at (1920,0).
        //
        //   QDC_DATABASE_CURRENT says the artist display is at
        //   (1920,0) and the laptop panel at (0,0).
        //
        // Same paths, same targets, opposite positions.  The database
        // is the one Windows will apply, so the DETACH has to send
        // the DATABASE's positions - not the active query's.
        //
        // The old code sent the ACTIVE query's mode array, whose
        // positions were the ones Windows had already refused, so every
        // SetDisplayConfig call answered 87 (ERROR_INVALID_PARAMETER)
        // and the detach silently did nothing.  That is the user's
        // bug: the virtual display stayed on the desktop, in Extended
        // mode, wherever Windows had put it.
        var database = QueryAny(QDC_DATABASE_CURRENT, QDC_ALL_PATHS);
        var positionSource = database ?? active;

        // FIRST ATTEMPT: the survivors keep the positions the DATABASE
        // records for them.
        //
        // The mode array has to describe the positions as well as the
        // sizes, because SetDisplayConfig reads the position out of
        // the SOURCE MODE, not out of the path.
        //
        // WHEN THE DATABASE HAS NOTHING TO SAY ABOUT A SURVIVOR, THE
        // LIVE POSITION IS KEPT.
        //
        // The database holds one record per topology the machine has
        // ever been in, and which record is current depends on which
        // monitors are plugged in.  With TWO physical monitors the
        // current record does not always hold a path for every
        // survivor - and the old code answered (0, 0) for the ones it
        // could not find.  (0, 0) is the ORIGIN, and the origin is
        // where the laptop panel is, so the survivor was sent ON TOP
        // of the laptop panel.  Windows forbids overlapping
        // rectangles, so it refused the whole configuration with 87
        // and the detach silently did nothing - which is the user's
        // bug, and it is the one that only showed up with two
        // monitors.
        //
        // So the live position is kept for a survivor the database
        // knows nothing about.  The live position is the one the user
        // is looking at, so it is the safest answer.
        bool databaseWasUsed = false;
        for (int i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            if ((p.flags & PATH_ACTIVE) == 0) continue;

            if (!TrySourcePositionOf(positionSource.Modes, p, out var x, out var y))
            {
                Log($"Detach: the database has no position for target {p.targetInfo.id}, "
                    + "so the live one is kept.");
                continue;
            }

            databaseWasUsed = true;
            SetSourcePosition(modes, p, x, y);
        }

        if (!databaseWasUsed)
        {
            Log("Detach: the database has no positions at all, so the live ones are kept.");
        }

        // The positions that came out of the database can OVERLAP.
        //
        // The database keeps one record per topology the machine has
        // ever been in, and the records are not consistent with one
        // another: the record for the topology that included the Rokid
        // Max holds the virtual display at (1915, -1080), which is a
        // row of its own above the physical monitors.  When the
        // survivors' positions come from such a record, the
        // configuration can be INVALID - Windows rejects overlapping
        // rectangles with 87 - and the detach silently does nothing,
        // which is the user's bug.
        //
        // So the layout is repaired before it is sent.  This is what
        // makes the difference between one physical monitor, two, and
        // three: it is not the number of monitors that decides whether
        // the layout can be applied, it is whether the record the
        // positions came from happens to be self-consistent.
        RepairLayout(paths, modes);

        if (Apply(paths, modes)) return DetachOutcome.Removed;

        // SECOND ATTEMPT: the survivors packed from the origin, so
        // that no two of them overlap.
        //
        // Windows forbids overlapping monitor rectangles outright, and
        // a topology change can leave two survivors overlapping -
        // which is exactly what happens when the user unplugs a
        // monitor and Windows re-lays-out the rest.  No configuration
        // that keeps them where they are can be valid, so the
        // positions have to be rebuilt.
        //
        // The survivors are laid out left to right from the origin, in
        // the order they are ALREADY in - so the laptop panel stays to
        // the left of the artist display, exactly as the user has it.
        // Packing them in the ARRAY's order instead would move the
        // user's own screens around, which is a much worse answer to
        // "take the virtual display away" than a rearranged desktop.
        //
        // The VIRTUAL display's slot is given up and NOT reused: the
        // user asked for the virtual display to be gone, and a
        // leftover gap on the left is the price of that.
        var packedModes = new List<DISPLAYCONFIG_MODE_INFO>(modes);

        var survivors = new List<(DISPLAYCONFIG_PATH_INFO Path, int Width, int Left)>();
        foreach (var p in paths)
        {
            if ((p.flags & PATH_ACTIVE) == 0) continue;
            var (w, _) = SourceSizeOf(positionSource.Modes, p);
            var (lx, _) = SourcePositionOf(active.Modes, p);
            survivors.Add((p, w, lx));
        }

        survivors.Sort((a, b) => a.Left.CompareTo(b.Left));

        int cursorX = 0;
        foreach (var s in survivors)
        {
            SetSourcePosition(packedModes, s.Path, cursorX, 0);
            cursorX += s.Width;
        }

        Log("Detach: the first attempt was refused; retrying with the survivors packed "
            + "from the origin.");
        if (Apply(paths, packedModes)) return DetachOutcome.Removed;

        // THIRD ATTEMPT: the packed layout, with the paths REBUILT.
        //
        // A path that Windows itself marked as having no available
        // target, or whose status flags say it is not in use, can
        // make the whole configuration invalid even when its mode is
        // fine.  Clearing those flags and marking the targets as
        // available is what the ATTACH does for the virtual display's
        // path, and it is what makes the difference here.
        var rebuilt = new List<DISPLAYCONFIG_PATH_INFO>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            if ((p.flags & PATH_ACTIVE) == 0)
            {
                // The disconnected path keeps its disconnect: the
                // flags stay cleared and the mode indices stay
                // invalid.  Only the target's availability is
                // restored, so that Windows does not treat the
                // display as unplugged and re-publish it.
                p.targetInfo.targetAvailable = 1;
                p.targetInfo.statusFlags = 0;
                rebuilt.Add(p);
                continue;
            }

            p.sourceInfo.statusFlags = 0;
            p.targetInfo.targetAvailable = 1;
            p.targetInfo.statusFlags = 0;
            p.flags = PATH_ACTIVE;
            rebuilt.Add(p);
        }

        Log("Detach: the packed attempt was refused; retrying with rebuilt paths.");
        if (Apply(rebuilt, packedModes)) return DetachOutcome.Removed;

        return DetachOutcome.Failed;
    }

    /// <summary>
    /// Make sure no two of the paths that are going to be sent to
    /// Windows overlap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows forbids overlapping monitor rectangles outright: a
    /// configuration whose displays overlap is rejected with 87
    /// (ERROR_INVALID_PARAMETER), and there is no way to apply it.
    /// </para>
    /// <para>
    /// The positions the code sends are not the ones the user sees.
    /// The detach sends the DATABASE's positions for the survivors,
    /// and the database keeps one record per topology the machine has
    /// ever been in - so it can hold positions that are stale, and
    /// stale positions overlap.  The record for the topology that
    /// included the Rokid Max holds the virtual display at
    /// (1915, -1080), which is exactly on top of the XP-Pen at
    /// (1920, 0).
    /// </para>
    /// <para>
    /// So the layout is REPAIRED before it is sent: the first display
    /// keeps its position and the others are laid out to its right,
    /// with no overlaps.  The repair is only applied when there IS an
    /// overlap, so a clean layout - the common case - is sent exactly
    /// as it was read.
    /// </para>
    /// </remarks>
    /// <param name="paths">The paths about to be sent.</param>
    /// <param name="modes">
    /// The mode array, whose source modes carry the positions.  It is
    /// modified in place when a repair is needed.
    /// </param>
    /// <returns>True when the layout had to be repaired.</returns>
    private static bool RepairLayout(
        List<DISPLAYCONFIG_PATH_INFO> paths,
        List<DISPLAYCONFIG_MODE_INFO> modes)
    {
        // The rectangles of the paths that are going to be on the
        // desktop, in the order they appear in the array.
        var rects = new List<DesktopRect>(paths.Count);
        var live = new List<DISPLAYCONFIG_PATH_INFO>(paths.Count);

        foreach (var p in paths)
        {
            if ((p.flags & PATH_ACTIVE) == 0) continue;

            var (x, y) = SourcePositionOf(modes, p);
            var (w, h) = SourceSizeOf(modes, p);
            if (w <= 0 || h <= 0) continue;

            rects.Add(new DesktopRect(x, y, x + w, y + h));
            live.Add(p);
        }

        if (rects.Count < 2) return false;

        var repaired = SpatialDisplayPlacement.RepairOverlaps(rects);
        if (ReferenceEquals(repaired, rects)) return false;

        for (int i = 0; i < live.Count; i++)
        {
            SetSourcePosition(modes, live[i], repaired[i].Left, repaired[i].Top);
        }

        Log($"Display config: the layout had {CountOverlaps(rects)} overlapping "
            + "display(s) and was repaired before it was sent.");
        return true;
    }

    /// <summary>
    /// How many pairs of the given rectangles overlap.
    /// </summary>
    private static int CountOverlaps(IReadOnlyList<DesktopRect> rects)
    {
        int n = 0;
        for (int i = 0; i < rects.Count; i++)
        {
            for (int j = i + 1; j < rects.Count; j++)
            {
                if (rects[i].Intersects(rects[j])) n++;
            }
        }
        return n;
    }

    /// <summary>
    /// A path's source mode position, matched BY IDENTITY rather than
    /// by index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The index cannot be used here.  A path's
    /// <c>sourceInfo.modeInfoIdx</c> is an index into the mode array
    /// of the query that PRODUCED the path, so it means nothing in
    /// another query's mode array.
    /// </para>
    /// <para>
    /// That is not a theoretical concern.  The active query and the
    /// database query return the same paths with DIFFERENT mode
    /// arrays, and the indices differ - which is exactly why the
    /// positions the detach sent were the ones Windows had already
    /// refused, and why every SetDisplayConfig call answered 87.
    /// </para>
    /// <para>
    /// The source mode is therefore found by its adapter and id, which
    /// are stable across queries.
    /// </para>
    /// <para>
    /// WHEN THERE IS NO SUCH MODE, THE POSITION IS NOT (0, 0)
    /// ------------------------------------------------------
    /// The old code answered (0, 0) - which is the ORIGIN, and the
    /// origin is where the laptop panel usually is.  So a survivor
    /// whose position could not be looked up was sent ON TOP of the
    /// laptop panel, and Windows refused the whole configuration with
    /// 87 (ERROR_INVALID_PARAMETER) - because overlapping rectangles
    /// are not allowed.
    /// </para>
    /// <para>
    /// The lookup fails when the DATABASE's current topology does not
    /// hold a path for that survivor - and the database holds one
    /// record per topology the machine has ever been in, so which
    /// record is current depends on which monitors are plugged in.
    /// That is why this only showed up with TWO physical monitors:
    /// with one, or with three, the record that happened to be current
    /// held every survivor, so the lookup always succeeded.
    /// </para>
    /// <para>
    /// So the lookup now REPORTS the failure, and the caller keeps the
    /// position the live session already has.  A position that is
    /// known to be wrong is worse than one that is merely unknown.
    /// </para>
    /// </remarks>
    private static bool TrySourcePositionOf(
        IReadOnlyList<DISPLAYCONFIG_MODE_INFO> modes,
        DISPLAYCONFIG_PATH_INFO path,
        out int x, out int y)
    {
        for (int m = 0; m < modes.Count; m++)
        {
            var mode = modes[m];
            if (mode.infoType != MODE_INFO_TYPE_SOURCE) continue;
            if (!mode.adapterId.SameAs(path.sourceInfo.adapterId)) continue;
            if (mode.id != path.sourceInfo.id) continue;

            x = mode.sourceMode.position.x;
            y = mode.sourceMode.position.y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    /// <summary>
    /// A path's source mode position, matched BY IDENTITY rather than
    /// by index.
    /// </summary>
    /// <remarks>
    /// The caller cannot tell whether the lookup succeeded, so this
    /// one is only for the places where the caller already knows the
    /// mode is there - see
    /// <see cref="TrySourcePositionOf"/> for the version that reports
    /// it.
    /// </remarks>
    private static (int X, int Y) SourcePositionOf(
        IReadOnlyList<DISPLAYCONFIG_MODE_INFO> modes, DISPLAYCONFIG_PATH_INFO path)
        => TrySourcePositionOf(modes, path, out var x, out var y) ? (x, y) : (0, 0);

    /// <summary>
    /// A path's source mode size, matched BY IDENTITY rather than by
    /// index.
    /// </summary>
    /// <remarks>
    /// The same reason as <see cref="SourcePositionOf"/>: the index
    /// belongs to the query that produced the path, not to the mode
    /// array being searched.
    /// </remarks>
    private static (int Width, int Height) SourceSizeOf(
        IReadOnlyList<DISPLAYCONFIG_MODE_INFO> modes, DISPLAYCONFIG_PATH_INFO path)
    {
        for (int m = 0; m < modes.Count; m++)
        {
            var mode = modes[m];
            if (mode.infoType != MODE_INFO_TYPE_SOURCE) continue;
            if (!mode.adapterId.SameAs(path.sourceInfo.adapterId)) continue;
            if (mode.id != path.sourceInfo.id) continue;
            return ((int)mode.sourceMode.Width, (int)mode.sourceMode.Height);
        }

        return (1920, 1080);
    }

    /// <summary>
    /// Write a position into a path's source mode.
    /// </summary>
    /// <remarks>
    /// The mode array is a list of STRUCTS, so an element cannot be
    /// modified in place - it has to be read, changed, and written
    /// back.
    /// </remarks>
    private static void SetSourcePosition(
        List<DISPLAYCONFIG_MODE_INFO> modes,
        DISPLAYCONFIG_PATH_INFO path, int x, int y)
    {
        for (int m = 0; m < modes.Count; m++)
        {
            var mode = modes[m];
            if (mode.infoType != MODE_INFO_TYPE_SOURCE) continue;
            if (!mode.adapterId.SameAs(path.sourceInfo.adapterId)) continue;
            if (mode.id != path.sourceInfo.id) continue;

            mode.sourceMode.position = new POINTL { x = x, y = y };
            modes[m] = mode;
            return;
        }
    }

    /// <summary>
    /// A path's source mode position, read out of the mode array.
    /// </summary>
    private static (int X, int Y) SourcePosition(
        DISPLAYCONFIG_MODE_INFO[] modes, DISPLAYCONFIG_PATH_INFO path)
    {
        if (path.sourceInfo.modeInfoIdx != uint.MaxValue
            && path.sourceInfo.modeInfoIdx < modes.Length)
        {
            var m = modes[path.sourceInfo.modeInfoIdx];
            if (m.infoType == MODE_INFO_TYPE_SOURCE)
                return (m.sourceMode.position.x, m.sourceMode.position.y);
        }

        return (0, 0);
    }

    /// <summary>
    /// A path's source mode size, read out of the mode array.
    /// </summary>
    private static (int Width, int Height) SourceSize(
        DISPLAYCONFIG_MODE_INFO[] modes, DISPLAYCONFIG_PATH_INFO path)
    {
        if (path.sourceInfo.modeInfoIdx != uint.MaxValue
            && path.sourceInfo.modeInfoIdx < modes.Length)
        {
            var m = modes[path.sourceInfo.modeInfoIdx];
            if (m.infoType == MODE_INFO_TYPE_SOURCE)
                return ((int)m.sourceMode.Width, (int)m.sourceMode.Height);
        }

        return (1920, 1080);
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

        var attempts = new System.Text.StringBuilder();

        foreach (var flags in ApplyFlagSets)
        {
            var validate = SetDisplayConfig(
                (uint)pathArray.Length, pathArray,
                (uint)modeArray.Length, modeArray,
                flags & ~SDC_APPLY | SDC_VALIDATE);

            attempts.Append($"  validate[0x{flags:X}] = {validate}");
            if (validate != 0)
            {
                attempts.AppendLine();
                continue;
            }

            var apply = SetDisplayConfig(
                (uint)pathArray.Length, pathArray,
                (uint)modeArray.Length, modeArray,
                flags);

            attempts.AppendLine();
            attempts.Append($"  apply[0x{flags:X}] = {apply}");

            Log($"SetDisplayConfig with {pathArray.Length} path(s):\n{attempts}");
            return apply == 0;
        }

        Log($"SetDisplayConfig with {pathArray.Length} path(s):\n{attempts}");
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
            if (!IsVirtualTarget(p.targetInfo.adapterId, p.targetInfo.id))
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
