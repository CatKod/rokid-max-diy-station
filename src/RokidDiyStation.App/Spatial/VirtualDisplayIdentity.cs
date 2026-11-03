// SPDX-License-Identifier: MIT
//
// Identifying the IddCx virtual display, by HARDWARE.
//
// WHY THIS IS A SEPARATE CLASS
// -----------------------------
// The identification is a POLICY decision - "is this display the
// virtual one?" - and it has to be testable without a virtual
// display driver, a display topology, or an administrator.  So the
// decision lives here, as pure functions over plain strings and
// numbers, and the Win32 wrapper in DisplayConfigController calls it.
//
// THE RULE
// --------
// The virtual display is identified by HARDWARE IDENTITY, and never by
// anything that changes with the machine's display topology:
//
//   * the EDID fingerprint in the target's device path,
//   * the target's friendly name,
//   * the raw EDID manufacturer and product code.
//
// NOT by a coordinate, NOT by an enumeration index, NOT by a source or
// target id.
//
// WHY THE COORDINATE MATTERS SO MUCH
// ----------------------------------
// The user's report was that the virtual display could not be
// disabled once the number of physical monitors changed, and that it
// stayed on the desktop in Extended mode, usually repositioned above
// the other displays.
//
// That "usually repositioned above" is the clue.  The old locator
// fell back to "the monitor at negative coordinates" when the EDID
// did not match, on the assumption that the virtual display is always
// parked to the left of the primary.  But a negative coordinate is a
// property of WHERE THE USER PUT IT, not of WHAT IT IS - and when
// Windows re-lays-out the desktop after a hot-plug, the virtual
// display can end up anywhere at all, including above the others.
//
// So a position-based rule is not just imprecise, it is exactly
// backwards for the case that matters.
//
// WHAT THE THREE FINGERPRINTS ARE
// -------------------------------
// The device path:
//
//     \\?\DISPLAY#MTT1337#1&28a6823a&9&UID256#{e6f07b5f-...}
//
// "MTT1337" is the EDID's manufacturer-and-product field, and the
// driver puts it in the path.  The rest of the path - the port, the
// instance, the UID - CHANGES when the topology changes, so only the
// fingerprint is matched, and it is matched ANYWHERE in the path.
//
// The friendly name - "VDD by MTT" - is an independent second
// fingerprint, for a driver version that spells the EDID differently.
//
// The raw EDID ids - the manufacturer code and the product code -
// are the most hardware-like identity there is: they describe the
// MONITOR, not the port it happens to be plugged into.
//
// Any one of the three is enough.  That is what makes the
// identification survive a driver update.
namespace RokidDiyStation.App.Spatial;

/// <summary>
/// Decides whether a display is the IddCx virtual display.
/// </summary>
public static class VirtualDisplayIdentity
{
    /// <summary>
    /// The virtual display's EDID fingerprint.
    /// </summary>
    /// <remarks>
    /// The driver publishes a hardcoded EDID whose manufacturer and
    /// product id are "MTT1337", and puts that text in the target's
    /// device path.
    /// </remarks>
    public const string EdidFingerprint = "MTT1337";

    /// <summary>
    /// The virtual display's friendly name, as the driver reports it.
    /// </summary>
    public const string FriendlyName = "VDD by MTT";

    /// <summary>
    /// The EDID manufacturer code, as the raw 15-bit number.
    /// </summary>
    /// <remarks>
    /// "MTT" packed into 15 bits, big-endian, which is how the EDID
    /// stores a three-letter manufacturer code.
    /// </remarks>
    public const ushort EdidManufacturerId = 0x9436;

    /// <summary>The EDID product code, as the raw number.</summary>
    public const ushort EdidProductCodeId = 0x1337;

    /// <summary>
    /// Whether a target's device path carries the virtual display's
    /// fingerprint.
    /// </summary>
    /// <param name="devicePath">The target's device path.</param>
    public static bool MatchesDevicePath(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return false;
        return devicePath.Contains(EdidFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a target's friendly name is the virtual display's.
    /// </summary>
    /// <param name="friendlyName">The target's friendly name.</param>
    public static bool MatchesFriendlyName(string? friendlyName)
    {
        if (string.IsNullOrEmpty(friendlyName)) return false;
        return friendlyName.Contains(FriendlyName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a pair of EDID ids is the virtual display's.
    /// </summary>
    /// <param name="manufacturerId">The EDID manufacturer code.</param>
    /// <param name="productCodeId">The EDID product code.</param>
    public static bool MatchesEdid(ushort manufacturerId, ushort productCodeId)
        => manufacturerId == EdidManufacturerId && productCodeId == EdidProductCodeId;

    /// <summary>
    /// Whether a display is the virtual display.
    /// </summary>
    /// <remarks>
    /// ANY ONE of the three fingerprints is enough.  Requiring all
    /// three would be stricter, and it would be worse: a driver
    /// update that changes one of them would make the virtual
    /// display unfindable, and the spatial display would simply stop
    /// opening.
    /// </remarks>
    /// <param name="devicePath">The target's device path.</param>
    /// <param name="friendlyName">The target's friendly name.</param>
    /// <param name="manufacturerId">The EDID manufacturer code.</param>
    /// <param name="productCodeId">The EDID product code.</param>
    public static bool IsVirtual(
        string? devicePath,
        string? friendlyName,
        ushort manufacturerId,
        ushort productCodeId)
        => MatchesDevicePath(devicePath)
           || MatchesFriendlyName(friendlyName)
           || MatchesEdid(manufacturerId, productCodeId);
}
