// Enumerate every display adapter and its monitors with the Win32
// API, so the virtual display's adapter can be seen and its
// StateFlags read.
//
// The driver is present and healthy (CM_PROB_NONE) after the
// reboot, but it publishes no monitor.  This shows whether the
// ADAPTER is enumerated at all, and what its state flags say.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

internal static class Program
{
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE display, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int mode, ref DEVMODE dm);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    private static int Main()
    {
        Console.WriteLine("=== adapters ===");
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            Console.WriteLine($"  adapter[{i}] {dd.DeviceName,-16} '{dd.DeviceString}'");
            Console.WriteLine($"      stateFlags=0x{dd.StateFlags:X}  id={dd.DeviceID}");

            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(dd.DeviceName, -1, ref dm))
                Console.WriteLine($"      current mode: {dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency} @({dm.dmPositionX},{dm.dmPositionY})");
            else
                Console.WriteLine("      current mode: (none)");

            int modeCount = 0;
            var dm2 = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            for (int m = 0; m < 64; m++)
            {
                var c = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettings(dd.DeviceName, m, ref c)) break;
                modeCount++;
            }
            Console.WriteLine($"      modes: {modeCount}");

            Console.WriteLine("      monitors:");
            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint j = 0; EnumDisplayDevices(dd.DeviceName, j, ref mon, 0); j++)
            {
                Console.WriteLine($"        {mon.DeviceName,-20} '{mon.DeviceString}' stateFlags=0x{mon.StateFlags:X} id={mon.DeviceID}");
            }
            Console.WriteLine();
        }

        return 0;
    }
}
