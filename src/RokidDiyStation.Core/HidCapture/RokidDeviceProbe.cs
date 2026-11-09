// SPDX-License-Identifier: MIT
//
// Is a Rokid pair of glasses attached right now?
//
// WHY THIS NEEDS ITS OWN FILE
// ---------------------------
// The vendor ID alone is not enough.
//
// 0x04D2 is a GENERIC USB vendor ID - it is used by all sorts of
// HID devices, and it is the ID the Rokid glasses happen to have.
// Enumerating "every device with VID 0x04D2" therefore finds a lot
// of things that are not glasses: docking stations, keyboards, mice,
// and whatever else the user has plugged in.
//
// That is harmless for the tracking service, because it OPENS the
// device and parses its reports - a non-Rokid device simply never
// produces a valid IMU packet.  But it is NOT harmless for the
// startup gate, whose whole job is to answer "are the glasses
// there?".  A false positive there means the app starts, finds no
// glasses, and sits in "searching for device…" forever - which is
// exactly the state the gate exists to prevent.
//
// So the probe identifies the glasses by their PRODUCT ID or by their
// real product name, and not by the vendor ID alone.
//
// THE PRODUCT NAME IS THE STRONGEST SIGNAL
// ----------------------------------------
// The Rokid Max reports "Rokid Max" over USB, and the name comes
// from the device itself.  A third-party HID device with the same
// vendor ID will not claim to be a Rokid, so the name is checked
// before the product ID is trusted.
//
// THE PRODUCT ID IS THE FALLBACK
// ------------------------------
// The name is not always available - HidSharp marks it obsolete, and
// some HID stacks do not expose the string at all.  The known
// product IDs in <see cref="Constants"/> are then used, which is
// what the tracking service does too, so the gate and the tracker
// can never disagree about whether the glasses are there.
using HidSharp;

namespace RokidDiyStation.Core.HidCapture;

/// <summary>
/// The answer to "are the Rokid glasses attached?".
/// </summary>
/// <param name="Present">Whether a pair of glasses was found.</param>
/// <param name="Detail">A human-readable description of what was found.</param>
public sealed record RokidPresence(bool Present, string Detail);

/// <summary>
/// Probes the real USB hardware for the Rokid glasses.
/// </summary>
public static class RokidDeviceProbe
{
    /// <summary>
    /// Look for the glasses on the real machine.
    /// </summary>
    /// <remarks>
    /// The probe never OPENS a device.  It only enumerates, which
    /// makes it a pure query: it cannot interfere with another
    /// program that holds the IMU interface (the official Rokid
    /// Station runtime does exactly that), and it cannot fail for
    /// reasons that have nothing to do with whether the glasses are
    /// plugged in.
    ///
    /// Both enumeration backends are consulted, because they do not
    /// always see the same set of interfaces - that is the whole
    /// reason there are two of them in the first place.
    /// </remarks>
    public static RokidPresence Check()
    {
        var hidSharp = ProbeHidSharp();
        if (hidSharp is not null) return hidSharp;

        var hidLibrary = ProbeHidLibrary();
        if (hidLibrary is not null) return hidLibrary;

        return new RokidPresence(
            Present: false,
            Detail: $"No Rokid device found (USB vendor 0x{Constants.RokidVendorId:X4}).");
    }

    /// <summary>
    /// The HidSharp view: the real product name, when the stack gives
    /// it to us.
    /// </summary>
    private static RokidPresence? ProbeHidSharp()
    {
        try
        {
            foreach (var device in DeviceList.Local.GetHidDevices(Constants.RokidVendorId))
            {
                var pid = device.ProductID;
                var name = SafeProductName(device);

                // The name is the strong signal: a device that calls
                // itself a Rokid is one.
                if (LooksLikeRokid(name))
                {
                    return new RokidPresence(true, $"USB HID: {name} (PID=0x{pid:X4})");
                }

                // The name is missing or generic - fall back to the
                // known product IDs.
                if (Constants.RokidProductIds.ContainsKey(pid))
                {
                    var known = Constants.RokidProductIds[pid];
                    return new RokidPresence(true, $"USB HID: {known} (PID=0x{pid:X4})");
                }
            }
        }
        catch
        {
            // The HidSharp backend can fail on a machine where the HID
            // stack is unhappy.  The other backend may still see it.
        }

        return null;
    }

    /// <summary>
    /// The HidLibrary view: the known product IDs only, because
    /// HidLibrary does not surface a product name.
    /// </summary>
    private static RokidPresence? ProbeHidLibrary()
    {
        try
        {
            foreach (var device in HidEnumerator.Discover())
            {
                return new RokidPresence(
                    Present: true,
                    Detail: $"USB HID: {device.ProductName} (PID=0x{device.ProductId:X4})");
            }
        }
        catch
        {
            // Same as above.
        }

        return null;
    }

    private static string SafeProductName(HidDevice device)
    {
        try
        {
#pragma warning disable CS0612 // the name property is marked obsolete but still works
            return device.ProductName ?? string.Empty;
#pragma warning restore CS0612
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Whether a USB product name says "Rokid".
    /// </summary>
    /// <remarks>
    /// Exposed so the DECISION can be tested without the hardware.  A
    /// test cannot plug a different device into the machine, but it can
    /// certainly ask what the code would make of a given name.
    /// </remarks>
    public static bool LooksLikeRokid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains("Rokid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a product ID is one of the known Rokid glasses.
    /// </summary>
    /// <remarks>
    /// Exposed for the same reason as <see cref="LooksLikeRokid"/>.
    /// </remarks>
    public static bool IsKnownProductId(int productId)
        => Constants.RokidProductIds.ContainsKey(productId);
}
