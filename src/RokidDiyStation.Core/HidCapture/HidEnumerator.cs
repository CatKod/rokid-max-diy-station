// SPDX-License-Identifier: MIT
//
// Discovers Rokid Max (and other Rokid) USB HID devices attached to the host.
//
// On non-Windows platforms the underlying HidLibrary call will throw — the
// M0 release is Windows-only by design (matches the project's README).

using HidLibrary;

namespace RokidDiyStation.Core.HidCapture;

/// <summary>
/// Metadata for a discovered Rokid device that has not yet been opened.
/// </summary>
/// <param name="VendorId">USB vendor ID.</param>
/// <param name="ProductId">USB product ID.</param>
/// <param name="ProductName">Product name reported by the device, or empty string.</param>
/// <param name="SerialNumber">Serial number reported by the device, or empty string.</param>
/// <param name="DevicePath">Windows device path; used internally by <see cref="HidEnumerator.Open"/>.</param>
public sealed record RokidDeviceInfo(
    int VendorId,
    int ProductId,
    string ProductName,
    string SerialNumber,
    string DevicePath);

/// <summary>
/// Enumerates connected HID devices and exposes the ones that match known Rokid VID/PID pairs.
/// </summary>
public static class HidEnumerator
{
    /// <summary>
    /// Return every attached device that matches <see cref="Constants.RokidVendorId"/> and a known product ID.
    /// </summary>
    public static IReadOnlyList<RokidDeviceInfo> Discover()
    {
        var matched = new List<RokidDeviceInfo>();
        foreach (var device in HidDevices.Enumerate(Constants.RokidVendorId))
        {
            var pid = device.Attributes.ProductId;
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

    /// <summary>
    /// Diagnostic overload that surfaces every HID interface for the Rokid vendor,
    /// even when the product ID is not in <see cref="Constants.RokidProductIds"/>.
    /// Useful for figuring out which `mi_NN` carries the IMU stream.
    /// </summary>
    public static IReadOnlyList<RokidDeviceInfo> DiscoverAllInterfaces()
    {
        var all = new List<RokidDeviceInfo>();
        foreach (var device in HidDevices.Enumerate(Constants.RokidVendorId))
        {
            var pid = device.Attributes.ProductId;
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

    /// <summary>
    /// Open the first connected Rokid Max (or compatible handle).
    /// </summary>
    /// <exception cref="InvalidOperationException">If no supported device is connected.</exception>
    public static IHidCapture OpenFirst()
    {
        var opened = TryOpenFirst();
        if (opened is null)
        {
            throw new InvalidOperationException(
                "No supported Rokid device found, or every interface is held by another process. " +
                "Uninstall the official Rokid Station runtime / driver if it is running, " +
                "or check `roksid --diagnose` for the per-interface failure log.");
        }
        return opened;
    }

    /// <summary>
    /// Open a specific previously-discovered device.
    /// </summary>
    public static IHidCapture Open(RokidDeviceInfo info)
    {
        foreach (var device in HidDevices.Enumerate(Constants.RokidVendorId))
        {
            if (!string.Equals(device.DevicePath, info.DevicePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryOpen(device);
            return new RokidHidDevice(device);
        }

        throw new InvalidOperationException(
            $"Rokid device {info.ProductName} (PID=0x{info.ProductId:X4}) is no longer attached.");
    }

    private static RokidHidDevice? TryOpenFirst()
    {
        Exception? lastError = null;
        foreach (var device in HidDevices.Enumerate(Constants.RokidVendorId))
        {
            var pid = device.Attributes.ProductId;
            if (!Constants.RokidProductIds.ContainsKey(pid))
            {
                continue;
            }

            try
            {
                TryOpen(device);
                return new RokidHidDevice(device);
            }
            catch (Exception ex)
            {
                // Keep enumerating in case there is another interface on the same glasses.
                lastError = ex;
            }
        }
        if (lastError is not null)
        {
            Console.Error.WriteLine($"# last open attempt failed: {lastError.Message}");
            Console.Error.WriteLine("# the Rokid Max exposes several HID interfaces; this often means the IMU");
            Console.Error.WriteLine("# interface (mi_02) is held by the Windows HID stack — re-run elevated.");
        }
        return null;
    }

    /// <summary>
    /// HidLibrary 3.2.x's <see cref="HidDevice.OpenDevice"/> actually returns
    /// <c>bool</c>, but the assembly's metadata reports <c>void</c> because of
    /// how the COM-style interop attribute is authored. We use the bool-overload
    /// through reflection so the call compiles against any minor revision of
    /// the package without flipping our build green then failing at runtime.
    /// </summary>
    private static void TryOpen(HidDevice device)
    {
        var method = typeof(HidDevice).GetMethod(
            "OpenDevice",
            new[] { typeof(HidDevice.DeviceMode), typeof(HidDevice.DeviceMode) });
        if (method is null)
        {
            // Fall back to the documented signature; on this revision we expect it to throw.
            device.OpenDevice(HidDevice.DeviceMode.NonOverlapped, HidDevice.DeviceMode.NonOverlapped);
            return;
        }

        var ok = (bool)(method.Invoke(device, new object[]
        {
            HidDevice.DeviceMode.NonOverlapped,
            HidDevice.DeviceMode.NonOverlapped,
        }) ?? false);
        if (!ok)
        {
            throw new InvalidOperationException(
                "HidLibrary.OpenDevice returned false — another process may already hold the HID handle.");
        }
    }
}