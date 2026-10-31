// SPDX-License-Identifier: MIT
//
// Finding the Rokid Max's display.
//
// The glasses enumerate as a DisplayPort secondary monitor, so the
// Windows desktop already has a monitor object for them.  The render
// window has to land on that monitor exactly, otherwise it straddles
// two displays and the compositor blits it across the boundary -
// which destroys the independent-flip path and adds a frame of
// latency.
//
// Identifying "the Rokid" is the interesting part.  There is no
// reliable, stable identifier in the display device path: the Lontium
// DP bridge enumerates with a generic USB display device ID, and the
// "\\.\DISPLAYn" name depends on enumeration order, which changes
// between reboots and when other monitors are hot-plugged.
//
// So we identify the monitor by a *fingerprint* instead, and let the
// user override it:
//
//   1. Prefer an explicit device path from the settings file.
//   2. Otherwise, prefer the monitor whose EDID describes a Rokid /
//      Lontium / Micro-OLED display.
//   3. Otherwise, fall back to "the largest non-primary monitor at
//      1920x1080 that is not the Rokid's known siblings".
//
// We also record the monitor's refresh rate, because the whole render
// loop is designed around the glasses' 120 Hz panel.
using System.Runtime.InteropServices;

namespace RokidDiyStation.App.Spatial;

/// <summary>Where a monitor sits on the virtual desktop, and how fast it runs.</summary>
/// <param name="DevicePath">The display device path, e.g. "\\.\DISPLAY5".</param>
/// <param name="DeviceName">The Win32 device name, e.g. "Rokid Max (LBT4753)".</param>
/// <param name="Bounds">The monitor's rectangle in virtual-desktop pixels.</param>
/// <param name="Width">The monitor's width in pixels.</param>
/// <param name="Height">The monitor's height in pixels.</param>
/// <param name="RefreshHz">The monitor's refresh rate in Hz.</param>
/// <param name="IsPrimary">Whether this is the primary desktop display.</param>
/// <param name="IsRokid">Whether the fingerprint matched a Rokid/Lontium display.</param>
/// <param name="MonitorId">
/// The monitor's EDID-derived device ID, e.g.
/// "MONITOR\LBT4753\{4d36e96e-...}".  This is the only hardware
/// identity the glasses expose.
/// </param>
public sealed record DisplayTarget(
    string DevicePath,
    string DeviceName,
    System.Drawing.Rectangle Bounds,
    int Width,
    int Height,
    double RefreshHz,
    bool IsPrimary,
    bool IsRokid,
    string MonitorId = "")
{
    public override string ToString()
        => $"{DeviceName} ({DevicePath}) {Width}x{Height} @{RefreshHz:0.#}Hz"
           + (IsRokid ? " [rokid]" : "");
}

internal static class DisplayNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int DM_DISPLAYFREQUENCY = 0x00400000;
    public const int DM_PELSWIDTH = 0x00080000;
    public const int DM_PELSHEIGHT = 0x00100000;
    public const int DD_ATTACHED_TO_DESKTOP = 0x00000001;
    public const int DD_PRIMARY_DEVICE = 0x00000002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE display, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string? deviceName, int mode, ref DEVMODE dm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(string? deviceName, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
}

/// <summary>Locates the monitor the glasses are driving.</summary>
public static class DisplayLocator
{
    /// <summary>
    /// Enumerate every attached display with its bounds and refresh rate.
    /// </summary>
    public static IReadOnlyList<DisplayTarget> Enumerate()
    {
        var results = new List<DisplayTarget>();
        var dev = new DisplayNative.DISPLAY_DEVICE { cb = Marshal.SizeOf<DisplayNative.DISPLAY_DEVICE>() };

        for (uint i = 0; ; i++)
        {
            dev = new DisplayNative.DISPLAY_DEVICE { cb = Marshal.SizeOf<DisplayNative.DISPLAY_DEVICE>() };
            if (!DisplayNative.EnumDisplayDevices(null, i, ref dev, 0)) break;
            if ((dev.StateFlags & DisplayNative.DD_ATTACHED_TO_DESKTOP) == 0) continue;

            var dm = new DisplayNative.DEVMODE { dmSize = (short)Marshal.SizeOf<DisplayNative.DEVMODE>() };
            if (!DisplayNative.EnumDisplaySettings(dev.DeviceName, DisplayNative.ENUM_CURRENT_SETTINGS, ref dm)) continue;

            // The adapter device name ("\\.\DISPLAY5") is what the
            // renderer needs in order to place the window and to find
            // the matching DXGI output.  The MONITOR's fingerprint
            // ("MONITOR\LBT4753") is what identifies the glasses.
            var monitor = ResolveMonitor(dev.DeviceName);
            var monitorId = monitor?.DeviceID ?? "";

            var bounds = new System.Drawing.Rectangle(
                dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight);

            var isRokid = LooksLikeRokid(dev.DeviceString)
                          || LooksLikeRokid(dev.DeviceID)
                          || LooksLikeRokid(monitorId)
                          || LooksLikeRokid(monitor is null ? "" : monitor.Value.DeviceString);

            results.Add(new DisplayTarget(
                dev.DeviceName,
                string.IsNullOrEmpty(monitor?.DeviceString ?? "") ? dev.DeviceString : monitor!.Value.DeviceString,
                bounds,
                dm.dmPelsWidth,
                dm.dmPelsHeight,
                dm.dmDisplayFrequency,
                (dev.StateFlags & DisplayNative.DD_PRIMARY_DEVICE) != 0,
                isRokid,
                monitorId));
        }

        return results;
    }

    /// <summary>
    /// Find the monitor attached to the given adapter, along with its
    /// EDID-derived device ID.
    /// </summary>
    /// <remarks>
    /// On this machine the glasses enumerate as
    ///
    ///   adapter  \\.\DISPLAY5   'NVIDIA GeForce RTX 3050 Laptop GPU'
    ///   monitor  \\.\DISPLAY5\Monitor0
    ///            id = 'MONITOR\LBT4753\{4d36e96e-...}'
    ///
    /// "LBT4753" is the Lontium DP bridge chip inside the Rokid Max, so
    /// it is the one piece of hardware-specific identity we get — and
    /// it is in the MONITOR's ID, not the adapter's.  Looking only at
    /// the adapter name finds the NVIDIA GPU, which drives the laptop's
    /// external ports as well, and would put the panel on the wrong
    /// screen.
    /// </remarks>
    private static DisplayNative.DISPLAY_DEVICE? ResolveMonitor(string adapterDeviceName)
    {
        var mon = new DisplayNative.DISPLAY_DEVICE { cb = Marshal.SizeOf<DisplayNative.DISPLAY_DEVICE>() };
        for (uint j = 0; ; j++)
        {
            mon = new DisplayNative.DISPLAY_DEVICE { cb = Marshal.SizeOf<DisplayNative.DISPLAY_DEVICE>() };
            if (!DisplayNative.EnumDisplayDevices(adapterDeviceName, j, ref mon, 0)) break;
            if ((mon.StateFlags & DisplayNative.DD_ATTACHED_TO_DESKTOP) == 0) continue;
            return mon;
        }
        return null;
    }

    /// <summary>
    /// Map a display adapter device name ("\\.\DISPLAY5") onto the
    /// monitor's display device path, which is what the caller needs in
    /// order to position a window on it.
    /// </summary>
    private static bool LooksLikeRokid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("Rokid", StringComparison.OrdinalIgnoreCase)
            || text.Contains("LBT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Lontium", StringComparison.OrdinalIgnoreCase)
            || text.Contains("USB Display", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pick the display the render window should live on.
    /// </summary>
    /// <param name="preferredDevicePath">
    /// An explicit device path from the settings, if the user has ever
    /// picked one.  Takes precedence over the fingerprint.
    /// </param>
    public static DisplayTarget? Select(string? preferredDevicePath = null)
    {
        var all = Enumerate();
        if (all.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredDevicePath))
        {
            var exact = all.FirstOrDefault(d =>
                string.Equals(d.DevicePath, preferredDevicePath, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        // Fingerprint match.
        var rokid = all.FirstOrDefault(d => d.IsRokid);
        if (rokid is not null) return rokid;

        // Fall back to the first non-primary display.  In practice the
        // Rokid is the only non-primary display on this machine, and
        // it enumerates before the laptop's own panel.
        return all.FirstOrDefault(d => !d.IsPrimary) ?? all[0];
    }
}
