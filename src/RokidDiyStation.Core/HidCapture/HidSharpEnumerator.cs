// SPDX-License-Identifier: MIT
//
// Alternate enumerator backed by HidSharp, used when the HidLibrary-based
// implementation cannot open the IMU interface (see
// docs/diag-hid-open-failure.md for the failure mode this addresses).
//
// Public surface mirrors HidEnumerator so call sites (CLI, App) can pick an
// implementation without rewriting their open logic.

using HidSharp;

namespace RokidDiyStation.Core.HidCapture;

/// <summary>
/// HidSharp-backed counterpart of <see cref="HidEnumerator"/>. The static
/// surface is intentionally identical so call sites can switch by
/// configuration (env var <c>ROKID_HID_BACKEND=hidsharp</c>) without code
/// changes.
/// </summary>
public static class HidSharpEnumerator
{
    public static IReadOnlyList<RokidDeviceInfo> Discover()
    {
        var matched = new List<RokidDeviceInfo>();
        foreach (var device in DeviceList.Local.GetHidDevices(Constants.RokidVendorId))
        {
            var pid = device.ProductID;
            if (!Constants.RokidProductIds.ContainsKey(pid))
            {
                continue;
            }
            var name = Constants.RokidProductIds[pid];
            matched.Add(new RokidDeviceInfo(
                VendorId: Constants.RokidVendorId,
                ProductId: pid,
                ProductName: name,
                SerialNumber: string.Empty,
                DevicePath: device.DevicePath ?? string.Empty));
        }
        return matched;
    }

    public static IReadOnlyList<RokidDeviceInfo> DiscoverAllInterfaces()
    {
        var all = new List<RokidDeviceInfo>();
        foreach (var device in DeviceList.Local.GetHidDevices(Constants.RokidVendorId))
        {
            var pid = device.ProductID;
            var name = Constants.RokidProductIds.TryGetValue(pid, out var prettyName)
                ? prettyName
                : $"Rokid device 0x{pid:X4}";
            all.Add(new RokidDeviceInfo(
                VendorId: Constants.RokidVendorId,
                ProductId: pid,
                ProductName: name,
                SerialNumber: string.Empty,
                DevicePath: device.DevicePath ?? string.Empty));
        }
        return all;
    }

    public static IHidCapture OpenFirst()
    {
        var opened = TryOpenFirst();
        if (opened is null)
        {
            throw new InvalidOperationException(
                "No supported Rokid device found via HidSharp, or every interface is held by another process. " +
                "Uninstall the official Rokid Station runtime / driver if it is running, " +
                "or check `roksid --diagnose` for the per-interface failure log.");
        }
        return opened;
    }

    public static IHidCapture Open(RokidDeviceInfo info)
    {
        HidDevice? match = null;
        foreach (var device in DeviceList.Local.GetHidDevices(Constants.RokidVendorId))
        {
            if (string.Equals(device.DevicePath, info.DevicePath, StringComparison.OrdinalIgnoreCase))
            {
                match = device;
                break;
            }
        }
        if (match is null)
        {
            throw new InvalidOperationException(
                $"Rokid device {info.ProductName} (PID=0x{info.ProductId:X4}) is no longer attached.");
        }
        return new HidSharpHidDevice(match);
    }

    private static HidSharpHidDevice? TryOpenFirst()
    {
        Exception? lastError = null;
        foreach (var device in DeviceList.Local.GetHidDevices(Constants.RokidVendorId))
        {
            var pid = device.ProductID;
            if (!Constants.RokidProductIds.ContainsKey(pid))
            {
                continue;
            }
            try
            {
                return new HidSharpHidDevice(device);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        if (lastError is not null)
        {
            Console.Error.WriteLine($"# last HidSharp open attempt failed: {lastError.Message}");
        }
        return null;
    }
}
