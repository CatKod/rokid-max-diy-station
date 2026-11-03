// Creates the MttVDD virtual display device through SetupAPI.
//
// WHY THIS EXISTS
// ---------------
// The virtual display driver is a ROOT-ENUMERATED device: its
// hardware id is "Root\MttVDD", and it has to be installed with a
// devnode, not just a driver package.  The two ways to do that are
//
//   * pnputil /add-device "Root\MttVDD"   - not available on this
//     Windows build (the command does not exist), and
//   * devcon install MttVDD.inf Root\MttVDD - the Windows Driver
//     Kit tool, which is not installed here.
//
// So this program does what devcon does: it calls SetupDiCreateDeviceInfo
// with the driver's class GUID, and then SetupDiSetDeviceRegistryProperty
// with the SPDRP_HARDWAREID property.  The PnP manager then matches
// the new devnode against the installed driver package and starts it.
//
// It must run ELEVATED: creating a root devnode is a system operation.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    // The Display class GUID.
    private static readonly Guid DevClassDisplay = new("4d36e968-e325-11ce-bfc1-08002be10318");

    private const int DICD_GENERIC_ID = 1;
    private const int DIOD_INHERIT_CLASSDRVS = 2;
    private const int SPDRP_HARDWAREID = 1;
    private const int SPDRP_FRIENDLYNAME = 12;
    private const int SPDRP_MFG = 11;
    private const int SPDRP_DRIVER = 4;

    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int ERROR_NO_MORE_ITEMS = 259;
    private const int ERROR_SUCCESS = 0;
    private const uint INVALID_HANDLE_VALUE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DEVINFO_DATA_DETAIL_DATA
    {
        public int cbSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1)] public string DevicePath;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiCreateDeviceInfo(
        IntPtr DeviceInfoSet, string DeviceName, ref Guid ClassGuid, string DeviceDescription,
        IntPtr hwndParent, int CreationFlags, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceRegistryProperty(
        IntPtr DeviceInfoSet, SP_DEVINFO_DATA DeviceInfoData, int Property, uint PropertyRegDataType,
        byte[] PropertyBuffer, int PropertyBufferSize, out int RequiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, ref SP_DEVINSTALL_PARAMS DeviceInstallParams);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(
        IntPtr DeviceInfoSet, int InstallFunction, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, int Property,
        out uint PropertyRegDataType, byte[] PropertyBuffer, int PropertyBufferSize, out int RequiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, StringBuilder DeviceInstanceId,
        int DeviceInstanceIdSize, out int RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetClassDevs(
        ref Guid classGuid, string enumerator, IntPtr hwndParent, int flags,
        out IntPtr deviceInfoSet, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, int MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid,
        int MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, int DeviceInterfaceDetailDataSize,
        out int RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, int Property,
        out uint PropertyType, byte[] PropertyBuffer, int PropertyBufferSize, out int RequiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetClassDevsEx(
        ref Guid classGuid, string enumerator, IntPtr hwndParent, int flags,
        out IntPtr deviceInfoSet, IntPtr deviceInfoData, IntPtr classFilterData, int classFilterSize);

    /// <remarks>
    /// The real layout.  The DriverPath is a MAX_PATH buffer (260
    /// characters, NOT 64), and DriverExtraSize is a POINTER to a
    /// DWORD - not a DWORD.  Getting either of those wrong makes
    /// SetupDiCallClassInstaller fail with
    /// ERROR_INVALID_PARAMETER (87) or with the unhelpful
    /// 0xE0000205.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DEVINSTALL_PARAMS
    {
        public int cbSize;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
        public IntPtr ClassDrvInstallParams;
        public IntPtr ClassDrvExtra;
        public IntPtr DriverExtra;
        public IntPtr DriverExtraSize;
    }

    // DICS_FLAG_GLOBAL: the property value is a REG_MULTI_SZ.
    private const uint DICS_FLAG_GLOBAL = 0x1;

    private const int SPDP_DEVICE_DESCRIPTION = 2;

    private static int Main(string[] args)
    {
        var hardwareId = args.Length > 0 ? args[0] : "Root\\MttVDD";
        var description = args.Length > 1 ? args[1] : "Virtual Display Driver";
        // The devnode's name.  For a root-enumerated device this is
        // the part after "Root\" in the hardware id, so the instance
        // id comes out as ROOT\MttVDD\0000 - which is what the
        // driver's INF matches against.
        var deviceName = args.Length > 2 ? args[2] : "MttVDD";

        var isAdmin = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        Console.WriteLine($"elevated: {isAdmin}");
        if (!isAdmin)
        {
            Console.WriteLine("FAIL: this must run elevated - creating a root devnode is a system operation.");
            return 1;
        }

        Console.WriteLine("=== 1. the existing devices in the Display class ===");
        foreach (var (id, name) in EnumerateDisplayClass())
            Console.WriteLine($"  {id}  {name}");

        Console.WriteLine();
        Console.WriteLine("=== 2. creating the devnode ===");
        var instanceId = CreateDevice(hardwareId, description, deviceName);
        if (instanceId is null)
        {
            Console.WriteLine("FAIL: the devnode could not be created.");
            return 1;
        }
        Console.WriteLine($"  created: {instanceId}");

        Console.WriteLine();
        Console.WriteLine("=== 3. the devices in the Display class now ===");
        foreach (var (id, name) in EnumerateDisplayClass())
            Console.WriteLine($"  {id}  {name}");

        return 0;
    }

    private static IEnumerable<(string Id, string Name)> EnumerateDisplayClass()
    {
        // cfgmgr32's CM_Get_Device_ID_List is used rather than
        // SetupAPI's class enumerator, because the latter returns an
        // EMPTY list on this machine even with devices present - it
        // filters on devices the current user can see, and the root
        // display adapter is not one of them.
        const uint CM_GETIDLIST_FILTER_CLASS = 0x800;
        const uint CM_GETIDLIST_FILTER_CLASSLOGICALNAME = 0x400;

        var filter = $"Class={{4d36e968-e325-11ce-bfc1-08002be10318}}";

        var needed = 0;
        var rc = CM_Get_Device_ID_List_Size(ref needed, filter,
            CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_CLASSLOGICALNAME);
        if (rc != 0 || needed == 0)
        {
            Console.WriteLine($"  CM_Get_Device_ID_List_Size failed: 0x{rc:X8}");
            yield break;
        }

        // The buffer is UNICODE, and the API is told the size in
        // CHARACTERS (not bytes).
        var buffer = Marshal.AllocHGlobal(needed * 2);
        try
        {
            rc = CM_Get_Device_ID_List(buffer, needed, filter,
                CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_CLASSLOGICALNAME);
            if (rc != 0)
            {
                Console.WriteLine($"  CM_Get_Device_ID_List failed: 0x{rc:X8}");
                yield break;
            }

            var text = Marshal.PtrToStringUni(buffer) ?? "";
            var results = new List<(string, string)>();
            foreach (var id in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var idTrimmed = id.Trim();
                if (idTrimmed.Length == 0) continue;
                results.Add((idTrimmed, ReadDescription(idTrimmed)));
            }

            foreach (var r in results) yield return r;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_Size(ref int bufferLen, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List(IntPtr buffer, int bufferLen, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(uint devInst, StringBuilder deviceIdBuf, uint bufferLen, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(uint devInst, ref uint parentDevInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    private const uint CM_LOCATE_DEVICE = 0x00000001;

    private static string ReadDescription(string instanceId)
    {
        if (CM_Locate_DevNode(out var devInst, instanceId, CM_LOCATE_DEVICE) != 0)
            return "";

        var sb = new StringBuilder(256);
        var rc = CM_Get_DevNode_Registry_Property(devInst, CM_DRP_DEVICE_DESCRIPTION, sb, (uint)sb.Capacity, 0);
        return rc == 0 ? sb.ToString() : "";
    }

    private const uint CM_DRP_DEVICE_DESCRIPTION = 0x2;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_Property(
        uint devInst, uint property, StringBuilder buffer, uint bufferLen, uint flags);

    private static string? CreateDevice(string hardwareId, string description, string deviceName)
    {
        var classGuid = DevClassDisplay;

        var set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == INVALID_HANDLE_VALUE)
        {
            Console.WriteLine($"  SetupDiCreateDeviceInfoList failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return null;
        }

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };

            // CreationFlags must be DICD_GENERIC_ID.
            //
            // This is the flag devcon uses for a ROOT-ENUMERATED
            // device, and it is what makes SetupDiCreateDeviceInfo
            // accept the "Root\MttVDD" hardware id.  Passing 0 makes
            // the call fail with 0xE0000205 (SPAPI_E_NO_SUCH_DEVINST).
            //
            // The device is created under the name "MttVDD", so the
            // instance id comes out as ROOT\MttVDD\0000 - which is
            // what the driver's INF matches against.
            if (!SetupDiCreateDeviceInfo(set, deviceName, ref classGuid, description,
                    IntPtr.Zero, DICD_GENERIC_ID, ref data))
            {
                Console.WriteLine($"  SetupDiCreateDeviceInfo failed: 0x{Marshal.GetLastWin32Error():X8}");
                return null;
            }

            // The hardware id is written with DICS_FLAG_GLOBAL, which
            // tells SetupAPI that the value is a REG_MULTI_SZ (a list
            // of null-terminated strings).  Passing REG_MULTI_SZ (7)
            // as the type instead is wrong: the type field for this
            // property is the DICS_FLAG_xxx mask, not a registry type,
            // and the call fails with ERROR_INVALID_DATA (13).
            var multiSz = Encoding.Unicode.GetBytes(hardwareId + "\0");
            if (!SetupDiSetDeviceRegistryProperty(set, data, SPDRP_HARDWAREID, DICS_FLAG_GLOBAL,
                    multiSz, multiSz.Length, out _))
            {
                Console.WriteLine($"  setting the hardware id failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }
            else
            {
                Console.WriteLine($"  hardware id set: {hardwareId}");
            }

            // The class installer for the Display class is what installs
            // the driver onto the new devnode.
            var installParams = new SP_DEVINSTALL_PARAMS
            {
                cbSize = Marshal.SizeOf<SP_DEVINSTALL_PARAMS>(),
                Flags = 0,
                DriverPath = "",
            };

            if (!SetupDiSetClassInstallParams(set, ref data, ref installParams))
                Console.WriteLine($"  SetupDiSetClassInstallParams failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

            // SP_INSTALL_CLASS_DRIVER = 3
            if (!SetupDiCallClassInstaller(set, 3, ref data))
                Console.WriteLine($"  SetupDiCallClassInstaller failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

            var sb = new StringBuilder(512);
            if (SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, out _))
                return sb.ToString();

            return null;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }
}
