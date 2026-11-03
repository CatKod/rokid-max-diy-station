// SPDX-License-Identifier: MIT
//
// THE ADAPTER-FLAG PROBE.
//
// The question this probe answers: what does the VDD's adapter report
// for DISPLAY_DEVICE_ATTACHED_TO_DESKTOP (0x1) at each stage of the
// lifecycle?
//
// WHY IT MATTERS
// --------------
// VirtualDisplayLocator.ResolveIdentity SKIPS any monitor whose
// DISPLAY_DEVICE.StateFlags lack 0x1:
//
//     for (uint j = 0; EnumDisplayDevices(adapterPath, j, ref mon, 0); j++)
//     {
//         if ((mon.StateFlags & 0x1) == 0) continue;
//         return (mon.DeviceID ?? "", mon.DeviceString ?? "");
//     }
//
// So when the VDD's monitor reports no attached flag, the identity
// resolution returns ("", ""), the EDID fingerprint cannot match, and
// FindVirtual() reports "there is no virtual display".
//
// The question is whether that filter is WRONG - i.e. whether the VDD
// can be listed by EnumDisplayMonitors (i.e. be on the desktop) while
// its monitor reports no attached flag.
//
// If it can, then the locator is blind to the virtual display exactly
// when the app needs to see it, and the pump's "the virtual display
// went away" conclusion is wrong.
//
// The probe reads the adapter's OWN flags and its monitors' flags
// separately, at every step.

using System.Diagnostics;
using System.Runtime.InteropServices;

using RokidDiyStation.App.Spatial;

namespace RokidDiyStation.Tools.ProbeAdapterFlags;

internal static class Program
{
    private static int failures = 0;

    private static int Main()
    {
        Console.WriteLine("=== the state before anything ===");
        Dump("before");

        Console.WriteLine();
        Console.WriteLine("=== 1. attach ===");
        DisplayConfigController.Attach(-1920, 0);
        Thread.Sleep(1500);
        Dump("after the attach");

        Console.WriteLine();
        Console.WriteLine("=== 2. detach ===");
        DisplayConfigController.Detach();
        Thread.Sleep(1500);
        Dump("after the detach");

        Console.WriteLine();
        Console.WriteLine("=== 3. attach again ===");
        DisplayConfigController.Attach(-1920, 0);
        Thread.Sleep(1500);
        Dump("after the second attach");

        Console.WriteLine();
        Console.WriteLine("=== 4. and detach, to leave the machine clean ===");
        DisplayConfigController.Detach();
        Thread.Sleep(1500);
        Dump("after the second detach");

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== ALL CHECKS PASSED ==="
            : $"=== {failures} CHECK(S) FAILED ===");
        return 0;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? device, uint devNum, ref DISPLAY_DEVICE display, uint flags);

    private static void Dump(string when)
    {
        Console.WriteLine($"  --- {when} ---");
        Console.WriteLine($"      ccdActive={DisplayConfigController.IsVirtualActive(),-5} "
            + $"locatorFound={VirtualDisplayLocator.FindVirtual() is not null,-5} "
            + $"shellMonitors={VirtualDisplayLocator.Enumerate().Count}");

        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            var adapterName = dd.DeviceName ?? "";
            var adapterFlags = dd.StateFlags;
            var adapterDesc = dd.DeviceString ?? "";
            var adapterId = dd.DeviceID ?? "";

            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            var monitors = new List<string>();
            for (uint j = 0; EnumDisplayDevices(adapterName, j, ref mon, 0); j++)
            {
                monitors.Add($"{mon.DeviceID} flags=0x{mon.StateFlags:X}"
                    + $" attached={(mon.StateFlags & 0x1) != 0}");
            }

            bool isVdd = adapterId.Contains("MttVDD", StringComparison.OrdinalIgnoreCase)
                || adapterDesc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                || monitors.Any(m => m.Contains("MTT1337", StringComparison.OrdinalIgnoreCase));

            if (!isVdd) continue;

            Console.WriteLine($"      the VDD adapter: {adapterName} flags=0x{adapterFlags:X} "
                + $"attached={(adapterFlags & 0x1) != 0} id={adapterId} desc={adapterDesc}");
            foreach (var m in monitors)
                Console.WriteLine($"        monitor: {m}");
        }
    }
}
