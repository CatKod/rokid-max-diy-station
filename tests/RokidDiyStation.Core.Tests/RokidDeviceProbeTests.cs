// SPDX-License-Identifier: MIT
//
// Tests for the hardware probe that decides whether the glasses are
// attached.
//
// WHY THIS MATTERS ENOUGH TO TEST
// -------------------------------
// The startup gate refuses to start the app when the glasses are
// absent, so a FALSE POSITIVE here is a real bug: the app would
// start, find no glasses, and sit in "searching for device…" forever
// - which is exactly the state the gate exists to prevent.
//
// The false positive comes from the VENDOR ID.  0x04D2 is a generic
// USB vendor ID, and it is shared with a lot of HID devices that have
// nothing to do with the glasses.
//
// The probe's DECISION is tested here; the enumeration itself is a
// call into the operating system and cannot be.
using RokidDiyStation.Core;
using RokidDiyStation.Core.HidCapture;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class RokidDeviceProbeTests
{
    [Fact]
    public void The_Rokid_Max_Is_Recognised_By_Its_Name()
    {
        Assert.True(RokidDeviceProbe.LooksLikeRokid("Rokid Max"));
        Assert.True(RokidDeviceProbe.LooksLikeRokid("ROKID MAX"));
        Assert.True(RokidDeviceProbe.LooksLikeRokid("Rokid Air Plus"));
        Assert.True(RokidDeviceProbe.LooksLikeRokid("Rokid Max Pro"));
    }

    [Fact]
    public void An_Unrelated_Device_Sharing_The_Vendor_Id_Is_Not_Glasses()
    {
        // The vendor ID alone is not an identity: 0x04D2 is generic.
        Assert.False(RokidDeviceProbe.LooksLikeRokid("USB Composite Device"));
        Assert.False(RokidDeviceProbe.LooksLikeRokid("HID-compliant mouse"));
        Assert.False(RokidDeviceProbe.LooksLikeRokid("Generic USB Hub"));
        Assert.False(RokidDeviceProbe.LooksLikeRokid(""));
        Assert.False(RokidDeviceProbe.LooksLikeRokid(null));
    }

    [Fact]
    public void The_Known_Product_Ids_Are_The_Rokid_Glasses()
    {
        Assert.True(RokidDeviceProbe.IsKnownProductId(0x162F));   // Rokid Max
        Assert.True(RokidDeviceProbe.IsKnownProductId(0x162B));   // Rokid Air
        Assert.True(RokidDeviceProbe.IsKnownProductId(0x162C));   // Rokid Air Plus
        Assert.True(RokidDeviceProbe.IsKnownProductId(0x2002));   // Rokid Max 2
        Assert.True(RokidDeviceProbe.IsKnownProductId(0x2180));   // Rokid Max Pro
    }

    [Fact]
    public void An_Unknown_Product_Id_Is_Not_Treated_As_Glasses()
    {
        // 0x0001 is a generic HID device, and 0xFFFF is what some
        // non-HID devices report.  Neither is a Rokid.
        Assert.False(RokidDeviceProbe.IsKnownProductId(0x0001));
        Assert.False(RokidDeviceProbe.IsKnownProductId(0xFFFF));
        Assert.False(RokidDeviceProbe.IsKnownProductId(0x1234));
    }

    [Fact]
    public void The_Vendor_Id_Is_The_Rokid_One()
    {
        // The whole probe hangs off this, so it is worth pinning: a
        // typo here would silently make the probe find nothing.
        Assert.Equal(0x04D2, Constants.RokidVendorId);
    }
}
