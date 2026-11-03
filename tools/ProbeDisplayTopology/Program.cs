// SPDX-License-Identifier: MIT
//
// A diagnostic dump of the display topology.
//
// The problem this investigates: the virtual display cannot be
// disabled when the number of physical monitors changes.
//
// The suspicion is that the VDD is found by something that changes
// with the topology - a coordinate, an enumeration index - so that
// when Windows re-lays-out the desktop after a hot-plug, the finder
// misses.
//
// This tool prints EVERYTHING that could be used to identify the
// virtual display, for every path in the display configuration
// database:
//
//   * the CCD target's device path and friendly name,
//   * the EDID manufacturer and product code,
//   * the Win32 device ID from EnumDisplayDevices,
//   * the monitor's bounds in virtual-desktop pixels.
//
// So the question "what is stable about the virtual display?" can be
// answered from the output rather than from a guess.
//
// It is a diagnostic, not part of the test suite, because it reports
// the machine's actual state.

using System.Runtime.InteropServices;
using System.Text;

namespace RokidDiyStation.Tools.ProbeDisplayTopology;

internal static class Program
{
    private static int Main()
    {
        var output = new StringBuilder();
        void Line(string s) => output.AppendLine(s);

        Line("=== MONITORS (EnumDisplayMonitors) ===");
        foreach (var m in EnumerateMonitors())
        {
            Line($"  dev={m.DeviceName,-22} bounds=({m.Left},{m.Top})-({m.Right},{m.Bottom}) "
               + $"{m.Right - m.Left}x{m.Bottom - m.Top} id='{m.DeviceId}' name='{m.Description}'");
        }

        Line("");
        Line("=== ADAPTERS (EnumDisplayDevices, top level) ===");
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            Line($"  [{i}] {dd.DeviceName,-18} '{dd.DeviceString}' state=0x{dd.StateFlags:X} id='{dd.DeviceID}'");
            Line($"       key='{dd.DeviceKey}'");

            for (uint j = 0; ; j++)
            {
                var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(dd.DeviceName, j, ref mon, 0)) break;
                Line($"    [{i}.{j}] {mon.DeviceName,-24} '{mon.DeviceString}' state=0x{mon.StateFlags:X} id='{mon.DeviceID}'");
                Line($"           key='{mon.DeviceKey}'");
            }
        }

        Line("");
        Line("=== CCD PATHS (QDC_ALL_PATHS) ===");
        DumpPaths(0x00000001, Line);

        Line("");
        Line("=== CCD PATHS (QDC_ONLY_ACTIVE_PATHS) ===");
        DumpPaths(0x00000002, Line);

        Line("");
        Line("=== CCD PATHS (QDC_DATABASE_CURRENT) ===");
        DumpPaths(0x00000004, Line);

        Console.Write(output.ToString());
        return 0;
    }

    private static void DumpPaths(uint flags, Action<string> Line)
    {
        var rc = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
        if (rc != 0)
        {
            Line($"  GetDisplayConfigBufferSizes(0x{flags:X}) failed: {rc}");
            return;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        uint topId = 0;
        int result = (flags & 0x00000004) != 0
            ? QueryDisplayConfigWithTopology(flags, ref pathCount, paths, ref modeCount, modes, ref topId)
            : QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

        if (result != 0)
        {
            Line($"  QueryDisplayConfig(0x{flags:X}) failed: {result}");
            return;
        }

        Line($"  {pathCount} paths, {modeCount} modes");

        for (int m = 0; m < modeCount; m++)
        {
            var mode = modes[m];
            if (mode.infoType != MODE_INFO_TYPE_SOURCE) continue;
            Line($"  mode[{m}] SOURCE adapter={mode.adapterId} id={mode.id} "
               + $"{mode.sourceMode.Width}x{mode.sourceMode.Height} "
               + $"at ({mode.sourceMode.position.x},{mode.sourceMode.position.y})");
        }
        for (int m = 0; m < modeCount; m++)
        {
            var mode = modes[m];
            if (mode.infoType == MODE_INFO_TYPE_SOURCE) continue;
            Line($"  mode[{m}] TARGET adapter={mode.adapterId} id={mode.id}");
        }

        for (int p = 0; p < pathCount; p++)
        {
            var path = paths[p];
            var name = GetTargetName(path.targetInfo.adapterId, path.targetInfo.id);

            Line($"  path[{p}] active={((path.flags & 0x1) != 0 ? "Y" : "N")} "
               + $"adapter={path.targetInfo.adapterId} targetId={path.targetInfo.id} "
               + $"sourceId={path.sourceInfo.id} "
               + $"srcModeIdx={path.sourceInfo.modeInfoIdx} tgtModeIdx={path.targetInfo.modeInfoIdx} "
               + $"srcStatus=0x{path.sourceInfo.statusFlags:X}");
            Line($"      targetDevicePath = '{name.DevicePath}'");
            Line($"      friendlyName    = '{name.FriendlyName}'");
            Line($"      edid            = Mfr={name.Mfg:X4} Prod={name.Prod:X4} "
               + $"(ascii='{MfgText(name.Mfg)}'/'{ProdText(name.Prod)}')");
            Line($"      outputTech={path.targetInfo.outputTechnology} "
               + $"targetAvail={path.targetInfo.targetAvailable} "
               + $"targetStatus=0x{path.targetInfo.statusFlags:X} "
               + $"srcStatus=0x{path.sourceInfo.statusFlags:X}");

            // The source mode carries the POSITION, when the path has one.
            if (path.sourceInfo.modeInfoIdx != uint.MaxValue
                && path.sourceInfo.modeInfoIdx < modeCount)
            {
                var sm = modes[path.sourceInfo.modeInfoIdx];
                if (sm.infoType == MODE_INFO_TYPE_SOURCE)
                {
                    Line($"      sourceMode      = {sm.sourceMode.Width}x{sm.sourceMode.Height} "
                       + $"at ({sm.sourceMode.position.x},{sm.sourceMode.position.y})");
                }
            }
            else
            {
                Line($"      sourceMode      = (none recorded, idx=0x{path.sourceInfo.modeInfoIdx:X8})");
            }
        }
    }

    // -----------------------------------------------------------------
    // The interop.
    // -----------------------------------------------------------------

    private const int MODE_INFO_TYPE_SOURCE = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
        public override string ToString() => $"{HighPart:X8}:{LowPart:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RATIONAL { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECTL { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct VIDEO_SIGNAL_INFO
    {
        public ulong PixelRate;
        public RATIONAL hSyncFreq, vSyncFreq;
        public uint ActiveCx, ActiveCy, TotalCx, TotalCy, VideoStandard, ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TARGET_MODE { public VIDEO_SIGNAL_INFO targetVideoSignalInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SOURCE_MODE { public uint Width, Height, PixelFormat; public POINTL position; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DESKTOP_IMAGE_INFO { public POINTL PathSourceSize; public RECTL DesktopImageRegion, DesktopImageClip; }

    [StructLayout(LayoutKind.Explicit)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public TARGET_MODE targetMode;
        [FieldOffset(16)] public SOURCE_MODE sourceMode;
        [FieldOffset(16)] public DESKTOP_IMAGE_INFO desktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
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
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray, ref uint modeInfoArraySize,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    private static extern int QueryDisplayConfigWithTopology(uint flags, ref uint pathInfoArraySize,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray, ref uint modeInfoArraySize,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, ref uint currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTargetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);

    private static (string DevicePath, string FriendlyName, ushort Mfg, ushort Prod) GetTargetName(
        LUID adapterId, uint id)
    {
        var req = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DEVICE_INFO_HEADER
            {
                type = 2,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = id,
            }
        };
        if (GetTargetDeviceInfo(ref req) != 0) return ("", "", 0, 0);
        return (req.monitorDevicePath ?? "", req.monitorFriendlyDeviceName ?? "",
                req.edidManufactureId, req.edidProductCodeId);
    }

    /// <summary>Decode the 3-letter EDID manufacturer code.</summary>
    private static string MfgText(ushort mfg)
    {
        // The EDID manufacturer id is 3 letters, each in 5 bits, big-endian.
        var bytes = new[] { (byte)(mfg >> 8), (byte)(mfg & 0xFF) };
        var letters = new char[3];
        letters[0] = (char)('A' + ((bytes[0] >> 5) & 0x1F));
        letters[1] = (char)('A' + ((bytes[0] & 0x1F)) + ((bytes[1] >> 3) & 0x06));
        letters[2] = (char)('A' + (bytes[1] & 0x1F));
        return new string(letters);
    }

    private static string ProdText(ushort prod) => prod.ToString("X4");

    // -----------------------------------------------------------------
    // The monitor enumeration.
    // -----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private delegate bool MonitorEnumProc(IntPtr h, IntPtr dc, ref RECT r, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE display, uint flags);

    private static List<(string DeviceName, int Left, int Top, int Right, int Bottom, string DeviceId, string Description)> EnumerateMonitors()
    {
        var results = new List<(string, int, int, int, int, string, string)>();

        bool Callback(IntPtr h, IntPtr dc, ref RECT r, IntPtr data)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(h, ref mi)) return true;

            var adapter = mi.szDevice;
            var idx = mi.szDevice.LastIndexOf("\\Monitor", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) adapter = mi.szDevice[..idx];

            var deviceId = "";
            var description = "";
            for (uint j = 0; ; j++)
            {
                var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(adapter, j, ref mon, 0)) break;
                if ((mon.StateFlags & 0x1) == 0) continue;
                deviceId = mon.DeviceID ?? "";
                description = mon.DeviceString ?? "";
                break;
            }

            results.Add((mi.szDevice, mi.rcMonitor.Left, mi.rcMonitor.Top,
                         mi.rcMonitor.Right, mi.rcMonitor.Bottom, deviceId, description));
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return results;
    }
}
