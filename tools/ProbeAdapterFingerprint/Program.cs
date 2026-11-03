// SPDX-License-Identifier: MIT
//
// THE ADAPTER-FINGERPRINT PROBE.
//
// THE QUESTION
// ------------
// The virtual display is identified by the TARGET's identity: the
// EDID fingerprint in the target's device path, the target's friendly
// name, and the raw EDID ids.
//
// All three are read through GetDisplayConfigTargetInfo, which needs
// the target's adapter id AND its target id.
//
// But the IddCx driver RE-PUBLISHES its monitor when the topology
// changes.  The re-published monitor has a NEW target id - and the
// old target id may no longer resolve, so the target's name cannot be
// read and the display stops being recognisable as the virtual one.
//
// When that happens:
//
//   * RemoveVirtualPaths() finds no virtual path, says "the virtual
//     display is already off the desktop", and returns SUCCESS -
//     while the rectangle is still on the user's screen.
//
//   * Attach() cannot find the virtual path either, so the spatial
//     display cannot be opened.
//
// The ADAPTER is the stable thing: the IddCx virtual adapter's
// device path carries the same "MTT1337" fingerprint, and the adapter
// does not change when the driver re-publishes its monitor.
//
// So this probe reads BOLD identities for every path - the target's
// device path, the target's friendly name, the EDID ids, and the
// ADAPTER's device path - and reports which of them identify the
// virtual display.
//
// If the adapter's device path carries the fingerprint on every path
// that belongs to the virtual display, then identifying the display
// by its adapter is the robust rule.

using System.Runtime.InteropServices;

using RokidDiyStation.App.Spatial;
using RokidDiyStation.Tools.ProbeVirtualDisplay;

namespace RokidDiyStation.Tools.ProbeAdapterFingerprint;

internal static class Program
{
    private const uint DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DEVICE_INFO_GET_ADAPTER_NAME = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInfoHeader
    {
        public uint type, size;
        public Ccd.LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDeviceName
    {
        public DeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string adapterDevicePath;
    }

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTargetDeviceInfo(ref TargetDeviceName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetAdapterDeviceInfo(ref AdapterDeviceName request);

    private static int Main()
    {
        foreach (uint flags in new[] { Ccd.QDC_ONLY_ACTIVE_PATHS, Ccd.QDC_ALL_PATHS })
        {
            Console.WriteLine($"=== {(flags == Ccd.QDC_ONLY_ACTIVE_PATHS ? "ONLY_ACTIVE" : "ALL_PATHS")} ===");
            var paths = Ccd.Query(flags, out _);
            if (paths is null) { Console.WriteLine("    the query failed"); continue; }

            foreach (var p in paths)
            {
                bool virtualByTarget = Ccd.IsVirtual(p);
                bool virtualByAdapter = IsVirtualAdapter(p.targetInfo.adapterId);

                // Only the interesting ones: the virtual display, and
                // anything that could be mistaken for it.
                if (!virtualByTarget && !virtualByAdapter) continue;

                var t = TargetName(p.targetInfo.adapterId, p.targetInfo.id);
                var a = AdapterName(p.targetInfo.adapterId);

                Console.WriteLine($"    target={p.targetInfo.id} source={p.sourceInfo.id} "
                    + $"adapter={p.targetInfo.adapterId} active={((p.flags & 0x1) != 0 ? "Y" : "N")}");
                Console.WriteLine($"        the target says: path='{t.monitorDevicePath}' "
                    + $"name='{t.monitorFriendlyDeviceName}' "
                    + $"edid=({t.edidManufactureId:X4},{t.edidProductCodeId:X4})");
                Console.WriteLine($"        the ADAPTER says: path='{a.adapterDevicePath}'");
                Console.WriteLine($"        virtual by target = {virtualByTarget}, "
                    + $"virtual by adapter = {virtualByAdapter}");
            }
        }

        return 0;
    }

    private static bool IsVirtualAdapter(Ccd.LUID adapterId)
    {
        var path = AdapterName(adapterId).adapterDevicePath ?? "";
        return VirtualDisplayIdentity.MatchesDevicePath(path)
            || path.Contains("MttVDD", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("ROOT\\DISPLAY", StringComparison.OrdinalIgnoreCase);
    }

    private static TargetDeviceName TargetName(Ccd.LUID adapterId, uint id)
    {
        var req = new TargetDeviceName
        {
            header = new DeviceInfoHeader
            {
                type = DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<TargetDeviceName>(),
                adapterId = adapterId,
                id = id,
            }
        };
        if (GetTargetDeviceInfo(ref req) != 0) return default;
        return req;
    }

    private static AdapterDeviceName AdapterName(Ccd.LUID adapterId)
    {
        var req = new AdapterDeviceName
        {
            header = new DeviceInfoHeader
            {
                type = DEVICE_INFO_GET_ADAPTER_NAME,
                size = (uint)Marshal.SizeOf<AdapterDeviceName>(),
                adapterId = adapterId,
            }
        };
        if (GetAdapterDeviceInfo(ref req) != 0) return default;
        return req;
    }
}
