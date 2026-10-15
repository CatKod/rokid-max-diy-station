// SPDX-License-Identifier: MIT
//
// Concrete IHidCapture backed by HidSharp (cross-platform .NET HID library).
//
// Why a second implementation?
// On Windows 10/11 the HidLibrary-based implementation sometimes returns
// `false` from `OpenDevice` for composite devices whose IMU interface uses
// an unusual usage-page. The Windows HID class driver (`hidclass.sys`) claims
// the device path and HidLibrary gives up. HidSharp talks to the same kernel
// API but supports opening the device in shared mode, which is known to
// succeed where HidLibrary sometimes fails. See
// docs/diag-hid-open-failure.md on the diag/handle-investigation branch for
// the full investigation.

using HidSharp;

namespace RokidDiyStation.Core.HidCapture;

/// <summary>
/// Wraps a single open <see cref="HidSharp.HidDevice"/> and exposes it through <see cref="IHidCapture"/>.
/// </summary>
public sealed class HidSharpHidDevice : IHidCapture
{
    private readonly HidDevice _device;
    private HidStream? _stream;
    private bool _disposed;

    internal HidSharpHidDevice(HidDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        VendorId = device.VendorID;
        ProductId = device.ProductID;
        ProductName = string.IsNullOrEmpty(device.ProductName)
            ? $"Rokid (PID=0x{device.ProductID:X4})"
            : device.ProductName;
        SerialNumber = device.SerialNumber ?? string.Empty;
    }

    public int VendorId { get; }
    public int ProductId { get; }
    public string ProductName { get; }
    public string SerialNumber { get; }

    /// <inheritdoc />
    public bool TryRead(int timeoutMs, out byte[] report)
    {
        var stream = EnsureStream();
        if (stream is null)
        {
            report = Array.Empty<byte>();
            return false;
        }

        // HidSharp's HidStream uses a per-stream ReadTimeout property; the
        // 3-arg Read(byte[], offset, count) blocks until at least one byte
        // is available or the timeout expires. We set ReadTimeout right
        // before each call so callers can pass a fresh timeout.
        stream.ReadTimeout = timeoutMs;
        var buf = new byte[_device.GetMaxInputReportLength()];
        try
        {
            var read = stream.Read(buf, 0, buf.Length);
            if (read <= 0)
            {
                report = Array.Empty<byte>();
                return false;
            }
            if (read == buf.Length)
            {
                report = buf;
                return true;
            }
            var trimmed = new byte[read];
            Buffer.BlockCopy(buf, 0, trimmed, 0, read);
            report = trimmed;
            return true;
        }
        catch (IOException)
        {
            report = Array.Empty<byte>();
            return false;
        }
        catch (TimeoutException)
        {
            report = Array.Empty<byte>();
            return false;
        }
    }

    private HidStream? EnsureStream()
    {
        if (_stream is not null) return _stream;
        try
        {
            // Open() with no arguments opens in shared mode. That is the
            // key difference vs. HidLibrary, which seems to require
            // exclusive ownership of the device path.
            _stream = _device.Open();
        }
        catch
        {
            _stream = null;
        }
        return _stream;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream?.Dispose(); } catch { /* ignore */ }
        _stream = null;
    }
}
