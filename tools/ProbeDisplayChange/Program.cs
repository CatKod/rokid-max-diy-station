// SPDX-License-Identifier: MIT
//
// A tiny probe for ChangeDisplaySettingsEx on the virtual display.
//
// It reports the RAW return code of the CDS_TEST and the apply call,
// because the app's attach path treats "non-zero" as "rejected" - and
// that is wrong: ChangeDisplaySettingsEx returns DISP_CHANGE_RESTART
// (1) for a mode that is perfectly valid but needs a restart to
// take effect, and DISP_CHANGE_UPDATECONTENT (2) for a mode that
// works but needs the frame buffer refreshed.
//
// Usage:
//   dotnet run --project tools/ProbeDisplayChange
using System;
using System.Runtime.InteropServices;

internal static class Program
{
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int mode, ref DEVMODE dm);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int ChangeDisplaySettingsEx(
        string? deviceName, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);

    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_TEST = 0x00000002;

    private const int DM_POSITION = 0x00000020;
    private const int DM_BITSPERPEL = 0x00040000;
    private const int DM_PELSWIDTH = 0x00080000;
    private const int DM_PELSHEIGHT = 0x00100000;
    private const int DM_DISPLAYFREQUENCY = 0x00400000;

    private static int Main(string[] args)
    {
        var adapter = args.Length > 0 ? args[0] : @"\\.\DISPLAY14";
        Console.WriteLine($"adapter: {adapter}");

        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(adapter, -1, ref dm))
        {
            Console.WriteLine("EnumDisplaySettings failed");
            return 1;
        }

        Console.WriteLine($"current mode : {dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency} "
                        + $"at ({dm.dmPositionX},{dm.dmPositionY})");

        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL
                    | DM_DISPLAYFREQUENCY | DM_POSITION;
        dm.dmPelsWidth = 1920;
        dm.dmPelsHeight = 1080;
        dm.dmBitsPerPel = 32;
        dm.dmDisplayFrequency = 60;
        dm.dmPositionX = -1920;
        dm.dmPositionY = 0;

        var test = dm;
        var testResult = ChangeDisplaySettingsEx(adapter, ref test, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        Console.WriteLine($"CDS_TEST     : {testResult}  (0=OK 1=RESTART 2=UPDATECONTENT 3=FAILED)");

        var apply = dm;
        var applyResult = ChangeDisplaySettingsEx(adapter, ref apply, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        Console.WriteLine($"APPLY        : {applyResult}");

        var after = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(adapter, -1, ref after))
        {
            Console.WriteLine($"after        : {after.dmPelsWidth}x{after.dmPelsHeight}@{after.dmDisplayFrequency} "
                            + $"at ({after.dmPositionX},{after.dmPositionY})");
        }
        return 0;
    }
}
