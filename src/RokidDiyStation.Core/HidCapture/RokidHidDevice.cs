// SPDX-License-Identifier: MIT
//
// Concrete IHidCapture backed by Mike O'Brien's HidLibrary on Windows.
//
// API surface lifted from runtime inspection of HidLibrary 3.2.20:
//   * `HidDevices.Enumerate(vendorId)` is the only enumerate entry point.
//   * `HidDevice.OpenDevice(DeviceMode.NonOverlapped, DeviceMode.NonOverlapped)`
//     returns `bool` (no `out` parameter).
//   * `HidDevice.Attributes` exposes VendorId / ProductId as `int`.
//   * `HidDeviceData` only exposes `Data` (byte[]) and `Status`
//     (ReadStatus enum, nested on HidDeviceData).
//   * `HidDevice.Read(int timeoutMs)` returns `HidDeviceData`; on timeout
//     `Status != ReadStatus.Success`.

using HidLibrary;

namespace RokidDiyStation.Core.HidCapture;

/// <summary>
/// Wraps a single open <see cref="HidLibrary.HidDevice"/> and exposes it through <see cref="IHidCapture"/>.
/// </summary>
public sealed class RokidHidDevice : IHidCapture
{
    private readonly HidDevice _device;
    private bool _disposed;

    internal RokidHidDevice(HidDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        var attrs = device.Attributes;
        VendorId = attrs.VendorId;
        ProductId = attrs.ProductId;
        // HidLibrary 3.2.x does not surface product name / serial number through
        // a strongly typed property on `HidDevice`. Fall back to the known
        // product-name table maintained in `Constants`.
        ProductName = Constants.RokidProductIds.TryGetValue(ProductId, out var name)
            ? name
            : $"Unknown Rokid (PID=0x{ProductId:X4})";
        SerialNumber = string.Empty;
    }

    public int VendorId { get; }
    public int ProductId { get; }
    public string ProductName { get; }
    public string SerialNumber { get; }

    /// <inheritdoc />
    public bool TryRead(int timeoutMs, out byte[] report)
    {
        var data = _device.Read(timeoutMs);
        var bytes = data?.Data;
        if (bytes is null || bytes.Length == 0 || data.Status != HidDeviceData.ReadStatus.Success)
        {
            report = Array.Empty<byte>();
            return false;
        }

        report = bytes;
        return report.Length > 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _device.CloseDevice();
        }
        catch
        {
            // Closing an already-closed device is benign.
        }
        _device.Dispose();
    }
}