// SPDX-License-Identifier: MIT
//
// Finding the virtual display, and driving it.
//
// The spatial display is an IddCx virtual monitor that lives OUTSIDE
// the visible physical desktop, at negative coordinates - typically
// (-1920, 0), just to the left of the primary display.
//
// It is found by its HARDWARE IDENTITY - the driver's own EDID
// ("MTT1337") - and never by its position.  The position is where it
// is PUT, which is a different question from what it IS; see
// FindVirtual() for why the distinction matters, and for what the
// position-based fallback used to get wrong.
//
// The legacy positioning API - ChangeDisplaySettingsEx - is still
// here, because it is how the attach and the detach were done before
// the CCD API took over.  It is no longer the mechanism for the
// spatial display's lifecycle.
//
// Why negative coordinates are deliberate
// ---------------------------------------
// Windows forbids overlapping monitor rectangles: the CCD rules require
// active displays to be disjoint or adjacent, and ChangeDisplaySettingsEx
// returns DISP_CHANGE_BADPARAM otherwise.  So the virtual monitor cannot
// share the Rokid Max's coordinates at (3840, 0).  That is fortunate,
// because if it could, our own WS_EX_TOPMOST opaque AR panel would sit
// on top of it and occlude the capture - a hall-of-mirrors feedback loop.
//
// Windows has no concept of an "off-screen" monitor.  A monitor at
// (-1920, 0) is part of the virtual desktop, the mouse reaches it, and
// the OS delivers clicks, focus and z-order there exactly as it would
// anywhere else.  The user simply sees that rectangle in the glasses
// instead of on the physical screen.
using System.Runtime.InteropServices;

namespace RokidDiyStation.App.Spatial;

/// <summary>
/// A monitor in the virtual desktop, with everything the cursor mapping
/// and the WGC capture need.
/// </summary>
/// <param name="Handle">The HMONITOR.</param>
/// <param name="DeviceName">The Win32 device name, e.g. "\\.\DISPLAY14".</param>
/// <param name="DeviceId">The EDID-derived device ID, e.g. "MONITOR\MTT1337\...".</param>
/// <param name="Description">The monitor's human-readable name.</param>
/// <param name="Bounds">The monitor's rectangle in virtual-desktop pixels.</param>
/// <param name="Width">The monitor's width in pixels.</param>
/// <param name="Height">The monitor's height in pixels.</param>
/// <param name="IsVirtual">Whether this is the IddCx virtual display.</param>
public sealed record MonitorInfo(
    IntPtr Handle,
    string DeviceName,
    string DeviceId,
    string Description,
    System.Drawing.Rectangle Bounds,
    int Width,
    int Height,
    bool IsVirtual)
{
    /// <summary>
    /// Map a point in virtual-desktop pixels onto this monitor's UV
    /// space.
    /// </summary>
    /// <remarks>
    /// This is the whole cursor-feedback mapping, and it is exact: the
    /// process is PerMonitorV2 DPI aware (see app.manifest), so
    /// GetCursorPos and GetMonitorInfo share one coordinate space, and
    /// the monitor's pixel rectangle IS the capture texture's size.
    /// </remarks>
    /// <returns>
    /// True when the point lies inside the monitor's rectangle.
    /// </returns>
    public bool TryToUv(int x, int y, out float u, out float v)
    {
        u = 0f;
        v = 0f;
        if (Width <= 0 || Height <= 0) return false;

        if (x < Bounds.Left || x >= Bounds.Right) return false;
        if (y < Bounds.Top || y >= Bounds.Bottom) return false;

        u = (x - Bounds.Left) / (float)Width;
        v = (y - Bounds.Top) / (float)Height;
        return true;
    }
}

/// <summary>
/// Finds the virtual display and positions it.
/// </summary>
public static class VirtualDisplayLocator
{
    /// <summary>
    /// The virtual desktop's left edge.
    /// </summary>
    /// <remarks>
    /// The virtual monitor is placed immediately to the LEFT of the
    /// primary display, so this is -1920 on a 1920-wide primary.
    /// </remarks>
    public const int DefaultOffsetX = -1920;

    /// <summary>
    /// The virtual display's pixel width.  It matches the Rokid Max's
    /// per-eye viewport exactly, so the cursor mapping and the captured
    /// texture line up one-to-one with no letterboxing.
    /// </summary>
    public const int DefaultWidth = 1920;

    /// <summary>
    /// The virtual display's pixel height.
    /// </summary>
    public const int DefaultHeight = 1080;

    // -----------------------------------------------------------------
    // Win32
    // -----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr h, IntPtr dc, ref RECT r, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);

    // NOTE the charset: DisplayNative.DISPLAY_DEVICE marshals its
    // strings with the struct's own CharSet, which is Unicode.  So
    // these have to be the W variants.  Calling the A variants
    // instead makes the API write ANSI bytes into a UTF-16 buffer,
    // and the device IDs come back garbled - which is why the
    // virtual monitor's "MTT1337" fingerprint stopped matching and
    // the display could not be found.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? device, uint devNum, ref DisplayNative.DISPLAY_DEVICE display, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(
        string? deviceName, int mode, ref DisplayNative.DEVMODE dm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string? deviceName, ref DisplayNative.DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);

    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_TEST = 0x00000002;

    // The codes ChangeDisplaySettingsEx returns.
    //
    // The important one is DISP_CHANGE_RESTART: the mode is perfectly
    // acceptable, but the system has to be restarted for it to take
    // effect.  Treating it as "rejected" would make the attach fail
    // on machines where the virtual display's driver asks for one.
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISP_CHANGE_RESTART = 1;
    private const int DISP_CHANGE_UPDATECONTENT = 2;
    private const int DISP_CHANGE_FAILED = -1;

    private const int DM_POSITION = 0x00000020;
    private const int DM_BITSPERPEL = 0x00040000;
    private const int DM_PELSWIDTH = 0x00080000;
    private const int DM_PELSHEIGHT = 0x00100000;
    private const int DM_DISPLAYFREQUENCY = 0x00400000;

    /// <summary>
    /// The adapter device name of the virtual display, e.g.
    /// "\\.\DISPLAY14".
    /// </summary>
    /// <remarks>
    /// Found by asking each adapter for its monitors and looking for
    /// the IddCx one.  The virtual monitor's EDID is the driver's own
    /// ("MTT1337"), which is how it is told apart from the Rokid Max
    /// ("LBT4753").
    ///
    /// THE ATTACHED-TO-DESKTOP FLAG IS DELIBERATELY NOT CHECKED
    /// -------------------------------------------------------------
    /// A detached virtual display has an adapter whose StateFlags are
    /// 0x0 - it is NOT attached to the desktop.  If that flag were
    /// required here, the locator would find nothing exactly when it
    /// needs to: when the display is closed and has to be opened
    /// again.  The adapter is still enumerated, and its monitor still
    /// carries the "MTT1337" EDID, so the display can be found and
    /// re-attached.
    ///
    /// This was a real bug: the virtual display could be closed but
    /// never opened again, because the locator skipped the very
    /// adapter it needed.
    /// </remarks>
    public static string? FindVirtualAdapter()
    {
        var dd = new DisplayNative.DISPLAY_DEVICE
        {
            cb = Marshal.SizeOf<DisplayNative.DISPLAY_DEVICE>()
        };

        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            var adapterName = dd.DeviceName;

            var mon = new DisplayNative.DISPLAY_DEVICE
            {
                cb = Marshal.SizeOf<DisplayNative.DISPLAY_DEVICE>()
            };
            for (uint j = 0; EnumDisplayDevices(adapterName, j, ref mon, 0); j++)
            {
                if (IsVirtualMonitorId(mon.DeviceID))
                    return adapterName;
            }
        }

        // Second pass: an adapter whose own description says it is a
        // virtual display.  This catches a driver that has not
        // published its monitor yet.
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if (dd.DeviceString.Contains("Virtual", System.StringComparison.OrdinalIgnoreCase))
                return dd.DeviceName;
        }

        return null;
    }

    /// <summary>
    /// Whether a monitor's device ID belongs to the IddCx virtual
    /// display.
    /// </summary>
    /// <remarks>
    /// The driver publishes a hardcoded EDID whose manufacturer and
    /// product id are "MTT1337".  That is stable across reboots and
    /// re-enumeration, unlike the "\\.\DISPLAYn" index.
    ///
    /// The decision is shared with the CCD controller, so the two
    /// always agree on which display is the virtual one.
    /// </remarks>
    public static bool IsVirtualMonitorId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;

        // The Win32 device id is "MONITOR\MTT1337\{...}", so the same
        // fingerprint works on it.
        if (VirtualDisplayIdentity.MatchesDevicePath(deviceId)) return true;

        return deviceId.Contains("MttVDD", System.StringComparison.OrdinalIgnoreCase)
            || deviceId.StartsWith("ROOT\\DISPLAY", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enumerate every monitor in the virtual desktop.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var results = new System.Collections.Generic.List<MonitorInfo>();
        var collector = new Collector();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collector.Callback, IntPtr.Zero);
        results.AddRange(collector.Monitors);
        return results;
    }

    /// <summary>
    /// Enumerate every adapter, with its state and description.
    /// </summary>
    /// <remarks>
    /// The adapter's StateFlags are the clearest signal of whether the
    /// virtual display is attached to the desktop: a detached adapter
    /// reports 0x0, an attached one reports 0x1 (DISPLAY_DEVICE_ATTACHED_TO_DESKTOP).
    ///
    /// The adapter's description is what identifies it as the virtual
    /// one: "Virtual Display Driver", with the device id "Root\MttVDD".
    /// </remarks>
    public static IReadOnlyList<(string DeviceName, uint StateFlags, string Description)> EnumerateAdapters()
    {
        var results = new System.Collections.Generic.List<(string, uint, string)>();

        var dd = new DisplayNative.DISPLAY_DEVICE
        {
            cb = Marshal.SizeOf<DisplayNative.DISPLAY_DEVICE>()
        };

        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            results.Add((dd.DeviceName ?? "", (uint)dd.StateFlags, dd.DeviceString ?? ""));
        }

        return results;
    }

    /// <summary>
    /// Find the virtual display.
    /// </summary>
    /// <remarks>
    /// The EDID is the criterion, always.  The rectangle is NOT used
    /// as a fallback, and that used to be a real bug.
    ///
    /// The fallback was: "if no monitor has the virtual EDID, take the
    /// one at negative coordinates".  The idea was that the spatial
    /// display is deliberately placed at negative coordinates.
    ///
    /// But a NEGATIVE coordinate is not a property of the virtual
    /// display - it is a property of WHERE THE USER PUT IT.  When the
    /// user rearranges the desktop - which is exactly what happens
    /// when they unplug a monitor - Windows re-lays-out the desktop
    /// and the virtual display can end up anywhere, INCLUDING at
    /// positive coordinates.  The user's own report says so: with one
    /// or two physical monitors the VDD sat ABOVE the others, which
    /// is a positive Y and possibly a positive X.
    ///
    /// Worse, the fallback would pick up the WRONG display: a
    /// physical monitor that happens to sit to the left of the origin
    /// would be reported as the virtual one.  Then the capture would
    /// target the wrong screen, the cursor mapping would be wrong, and
    /// the panel would show the laptop's desktop instead of the
    /// spatial canvas.
    ///
    /// So the fallback is gone.  If the EDID does not identify the
    /// virtual display, it is not found - and the caller treats that
    /// as "the virtual display is not attached", which is the truth.
    /// </remarks>
    public static MonitorInfo? FindVirtual()
    {
        var all = Enumerate();
        if (all.Count == 0) return null;

        return all.FirstOrDefault(m => m.IsVirtual);
    }

    /// <summary>
    /// Move the virtual display to a resolution and an absolute position.
    /// </summary>
    /// <remarks>
    /// This is how the spatial display is opened and closed.  Setting the
    /// size to zero detaches the monitor from the desktop, and restoring
    /// it reattaches - neither needs administrator rights, which is what
    /// makes "Open spatial display" a plain user action.
    /// </remarks>
    /// <param name="adapterDeviceName">
    /// The adapter's device name, e.g. "\\.\DISPLAY14".  When null, the
    /// virtual display is looked up.
    /// </param>
    /// <param name="width">The width in pixels, or 0 to detach.</param>
    /// <param name="height">The height in pixels, or 0 to detach.</param>
    /// <param name="x">The left edge in virtual-desktop pixels.</param>
    /// <param name="y">The top edge in virtual-desktop pixels.</param>
    /// <returns>True when Windows accepted the change.</returns>
    public static bool Position(
        string? adapterDeviceName, int width, int height, int x, int y)
    {
        var adapter = adapterDeviceName ?? FindVirtualAdapter();
        if (adapter is null) return false;

        var dm = new DisplayNative.DEVMODE
        {
            dmSize = (short)Marshal.SizeOf<DisplayNative.DEVMODE>()
        };
        if (!EnumDisplaySettings(adapter, -1, ref dm)) return false;

        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL
                    | DM_DISPLAYFREQUENCY | DM_POSITION;

        if (width <= 0 || height <= 0)
        {
            // Detach: zero dimensions take the monitor off the desktop.
            dm.dmPelsWidth = 0;
            dm.dmPelsHeight = 0;
            dm.dmDisplayFrequency = 0;
            dm.dmPositionX = 0;
            dm.dmPositionY = 0;
        }
        else
        {
            dm.dmPelsWidth = width;
            dm.dmPelsHeight = height;
            dm.dmBitsPerPel = 32;
            dm.dmDisplayFrequency = 60;
            dm.dmPositionX = x;
            dm.dmPositionY = y;
        }

        // Ask Windows whether the mode is acceptable before applying it,
        // so a rejected mode does not tear the desktop down.
        //
        // DISP_CHANGE_RESTART means "acceptable, but it needs a
        // restart" - it is NOT a rejection, so it must not abort the
        // attach.
        var test = dm;
        var testResult = ChangeDisplaySettingsEx(adapter, ref test, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (testResult == DISP_CHANGE_FAILED)
            return false;

        // THE REGISTRY IS NOT UPDATED ON THE DETACH.
        //
        // This is the difference between a reversible detach and a
        // one-way door.  With CDS_UPDATEREGISTRY the zero-width mode
        // is PERSISTED under the display device's key, and a
        // zero-width mode is not a mode: EnumDisplaySettings on that
        // adapter fails from then on, and every later attempt to put
        // the monitor back returns DISP_CHANGE_FAILED (-1).
        //
        // Without it the change lives only in the session, the
        // adapter keeps a valid current mode, and the monitor can be
        // re-attached - either through this API or through the CCD
        // API.
        //
        // The attach does update the registry, because a mode the
        // user wants to keep should survive a logon.
        var flags = width <= 0 || height <= 0 ? 0u : CDS_UPDATEREGISTRY;

        var result = ChangeDisplaySettingsEx(adapter, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
        return result != DISP_CHANGE_FAILED;
    }

    /// <summary>
    /// Place the virtual display where the spatial panel expects it.
    /// </summary>
    public static bool AttachDefault()
        => Position(null, DefaultWidth, DefaultHeight, DefaultOffsetX, 0);

    /// <summary>
    /// Take the virtual display off the desktop.
    /// </summary>
    public static bool Detach()
        => Position(null, 0, 0, 0, 0);

    private sealed class Collector
    {
        public readonly List<MonitorInfo> Monitors = new();

        public bool Callback(IntPtr h, IntPtr dc, ref RECT r, IntPtr data)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(h, ref mi)) return true;

            var bounds = new System.Drawing.Rectangle(
                mi.rcMonitor.Left, mi.rcMonitor.Top,
                mi.rcMonitor.Right - mi.rcMonitor.Left,
                mi.rcMonitor.Bottom - mi.rcMonitor.Top);

            var (deviceId, description) = ResolveIdentity(mi.szDevice);

            Monitors.Add(new MonitorInfo(
                h,
                mi.szDevice,
                deviceId,
                description,
                bounds,
                bounds.Width,
                bounds.Height,
                IsVirtualMonitorId(deviceId)));

            return true;
        }
    }

    /// <summary>
    /// Resolve a monitor's EDID-derived device ID and its name.
    /// </summary>
    /// <remarks>
    /// The HMONITOR's szDevice is "\\.\DISPLAYn\Monitor0", which is the
    /// MONITOR's path under the ADAPTER's path.  EnumDisplayDevices
    /// wants the adapter's path, so we trim the "\Monitor0" suffix.
    /// </remarks>
    private static (string DeviceId, string Description) ResolveIdentity(string monitorDeviceName)
    {
        var adapterPath = monitorDeviceName;
        var idx = monitorDeviceName.LastIndexOf("\\Monitor", System.StringComparison.OrdinalIgnoreCase);
        if (idx > 0) adapterPath = monitorDeviceName[..idx];

        var mon = new DisplayNative.DISPLAY_DEVICE
        {
            cb = Marshal.SizeOf<DisplayNative.DISPLAY_DEVICE>()
        };
        for (uint j = 0; EnumDisplayDevices(adapterPath, j, ref mon, 0); j++)
        {
            if ((mon.StateFlags & 0x1) == 0) continue;
            return (mon.DeviceID ?? "", mon.DeviceString ?? "");
        }

        return ("", "");
    }
}
