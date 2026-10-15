// SPDX-License-Identifier: MIT
//
// USB / HID identifiers and protocol constants for the Rokid Max.
//
// Cross-validated against two upstream reverse-engineering efforts:
//   - xXJSONDeruloXx/rokid-spatial-macos (Python + IOKit, macOS)
//   - angkaworn-dotcom/rokid-spatial (Swift + IOKit, macOS, firmware 0.40)
//
// See docs/PROTOCOL-REFERENCES.md for details.

namespace RokidDiyStation.Core;

/// <summary>
/// USB / HID identifiers and protocol constants for Rokid AR glasses.
/// </summary>
public static class Constants
{
    /// <summary>USB vendor ID assigned to "Rokid Corporation Ltd."</summary>
    public const int RokidVendorId = 0x04D2;

    /// <summary>
    /// Map of known Rokid product IDs to product names.
    /// M0 implementation is intentionally narrow: only the Max (0x162F) is
    /// explicitly supported. Other IDs are kept for discovery / diagnostics.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> RokidProductIds =
        new Dictionary<int, string>
        {
            [0x162B] = "Rokid Air",
            [0x162C] = "Rokid Air Plus",
            [0x162D] = "Rokid Unknown (0x162D)",
            [0x162E] = "Rokid Unknown (0x162E)",
            [0x162F] = "Rokid Max",
            [0x2002] = "Rokid Max 2",
            [0x2180] = "Rokid Max Pro",
        };

    /// <summary>HID report ID for the combined IMU stream (report 17 = 0x11).</summary>
    public const byte ReportIdImuData = 0x11;

    /// <summary>HID report ID for the (currently unused) output report.</summary>
    public const byte ReportIdOutput = 0x01;

    /// <summary>Total packet size of every HID report from the glasses, in bytes.</summary>
    public const int ReportLength = 64;

    /// <summary>Raw interval between HID reports as declared by the descriptor, microseconds.</summary>
    public const int ImuRawIntervalUs = 2_000;

    /// <summary>Effective decimated IMU update rate, Hz.</summary>
    public const int ImuRateHz = 90;
}