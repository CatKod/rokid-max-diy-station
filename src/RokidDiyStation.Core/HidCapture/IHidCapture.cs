// SPDX-License-Identifier: MIT
//
// Abstraction over a single open HID device so the rest of the pipeline can
// be unit-tested without real hardware.

namespace RokidDiyStation.Core.HidCapture;

/// <summary>
/// Low-level abstraction over an open USB HID device.
/// Implementations are expected to be single-threaded — callers serialise access.
/// </summary>
public interface IHidCapture : IDisposable
{
    /// <summary>USB vendor ID of the open device.</summary>
    int VendorId { get; }

    /// <summary>USB product ID of the open device.</summary>
    int ProductId { get; }

    /// <summary>Human-readable product name, if known.</summary>
    string ProductName { get; }

    /// <summary>Serial number reported by the device, if any.</summary>
    string SerialNumber { get; }

    /// <summary>
    /// Try to read one report from the device within <paramref name="timeoutMs"/>.
    /// Returns <c>false</c> on timeout; <c>true</c> with the report payload when
    /// at least one byte was returned.
    /// </summary>
    bool TryRead(int timeoutMs, out byte[] report);
}