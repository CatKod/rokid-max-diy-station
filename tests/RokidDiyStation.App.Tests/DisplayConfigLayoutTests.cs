// SPDX-License-Identifier: MIT
//
// Tests that lock the display-configuration (CCD) binding to the
// Windows SDK.
//
// WHY THIS MATTERS ENOUGH TO TEST
// -------------------------------
// The CCD binding got the query flags and two struct layouts WRONG,
// and every symptom looked like a broken MACHINE rather than a broken
// binding:
//
//   * the app's QDC_DATABASE_CURRENT was 0x01, which is really
//     QDC_ALL_PATHS, and its QDC_ONLY_ACTIVE_PATHS was 0x04, which
//     is really QDC_DATABASE_CURRENT - the two were swapped;
//   * so QueryDisplayConfig failed with ERROR_INVALID_PARAMETER (87)
//     for every call, and the app concluded the topology was
//     unreadable;
//   * the mode index was read from the wrong dword of
//     DISPLAYCONFIG_PATH_SOURCE_INFO, so every path referred to no
//     mode at all;
//   * and the two EDID ids in DISPLAYCONFIG_TARGET_DEVICE_NAME were
//     declared UINT32 instead of UINT16, which pushed the device-path
//     string four bytes off - so the "MTT1337" fingerprint could never
//     match and the virtual monitor was invisible.
//
// The failure mode is SILENT.  Nothing throws: the API just answers
// "no" forever, and the symptom is "the spatial display never opens".
//
// So the sizes are asserted here, exactly.  They are the cheapest
// possible check that the binding still matches wingdi.h.
using System.Runtime.InteropServices;

using RokidDiyStation.App.Spatial;

using Xunit;

namespace RokidDiyStation.App.Tests;

public class DisplayConfigLayoutTests
{
    [Fact]
    public void ThePathStructMatchesTheSdk()
    {
        // DISPLAYCONFIG_PATH_INFO = 72 bytes on x64.
        Assert.Equal(72, Marshal.SizeOf<DisplayConfigController.DISPLAYCONFIG_PATH_INFO>());

        // The source half: LUID (8) + id (4) + the mode-index union (4)
        // + statusFlags (4).
        Assert.Equal(20, Marshal.SizeOf<DisplayConfigController.PATH_SOURCE_INFO>());
    }

    [Fact]
    public void TheModeStructIsAUnionAndStays64Bytes()
    {
        // The three payloads OVERLAP.  Declaring them sequentially
        // makes the struct 120 bytes, and the API then writes past the
        // end of the array.
        Assert.Equal(64, Marshal.SizeOf<DisplayConfigController.DISPLAYCONFIG_MODE_INFO>());

        // The union slot starts right after infoType, id and adapterId.
        Assert.Equal(16, (int)Marshal.OffsetOf<DisplayConfigController.DISPLAYCONFIG_MODE_INFO>("sourceMode"));
        Assert.Equal(16, (int)Marshal.OffsetOf<DisplayConfigController.DISPLAYCONFIG_MODE_INFO>("targetMode"));
        Assert.Equal(16, (int)Marshal.OffsetOf<DisplayConfigController.DISPLAYCONFIG_MODE_INFO>("desktopImageInfo"));
    }

    [Fact]
    public void TheTargetNameStructMatchesTheSdk()
    {
        // DISPLAYCONFIG_TARGET_DEVICE_NAME = 420 bytes on x64.
        Assert.Equal(420, Marshal.SizeOf<DisplayConfigController.DISPLAYCONFIG_TARGET_DEVICE_NAME>());

        // The device path is the LAST field, so it has to start at
        // 420 - 256 = 164.  With the EDID ids wrongly declared as
        // UINT32 it started eight bytes earlier instead, and every
        // lookup for the virtual monitor's EDID came back empty.
        Assert.Equal(164, (int)Marshal.OffsetOf<DisplayConfigController.DISPLAYCONFIG_TARGET_DEVICE_NAME>("monitorDevicePath"));
        Assert.Equal(36, (int)Marshal.OffsetOf<DisplayConfigController.DISPLAYCONFIG_TARGET_DEVICE_NAME>("monitorFriendlyDeviceName"));
    }

    [Fact]
    public void ThePreferredModeStructMatchesTheSdk()
    {
        // DISPLAYCONFIG_TARGET_PREFERRED_MODE is 80 bytes on x64.
        //
        // It is 76 bytes on x86, and that number is what the MSDN
        // documentation quotes - so it is a trap.  The target mode
        // contains a UINT64, which is 8-byte aligned on x64, and the
        // header plus the width plus the height come to 28 bytes, so
        // the mode starts at 32 rather than 28.  That padding is what
        // makes the struct 80 bytes.
        //
        // This matters: the header's size field has to match what the
        // OS expects, or the call is refused.
        Assert.Equal(80, Marshal.SizeOf<DisplayConfigController.DISPLAYCONFIG_TARGET_PREFERRED_MODE>());

        // The width and the height are PLAIN fields.  Reading them
        // out of the mode union returns whatever the target mode
        // happens to contain - which is why the virtual display's
        // size came back as 0x0.
        Assert.Equal(20, (int)Marshal.OffsetOf<DisplayConfigController.DISPLAYCONFIG_TARGET_PREFERRED_MODE>("width"));
        Assert.Equal(24, (int)Marshal.OffsetOf<DisplayConfigController.DISPLAYCONFIG_TARGET_PREFERRED_MODE>("height"));
        Assert.Equal(32, (int)Marshal.OffsetOf<DisplayConfigController.DISPLAYCONFIG_TARGET_PREFERRED_MODE>("targetMode"));
    }

    [Fact]
    public void TheSourceModeMatchesTheSdk()
    {
        // DISPLAYCONFIG_SOURCE_MODE = 20 bytes: the width, the
        // height, the pixel format and the position.
        Assert.Equal(20, Marshal.SizeOf<DisplayConfigController.SOURCE_MODE>());
        Assert.Equal(12, (int)Marshal.OffsetOf<DisplayConfigController.SOURCE_MODE>("position"));
    }

    [Fact]
    public void TheVideoSignalInfoMatchesTheSdk()
    {
        // DISPLAYCONFIG_VIDEO_SIGNAL_INFO = 48 bytes on x64.
        Assert.Equal(48, Marshal.SizeOf<DisplayConfigController.VIDEO_SIGNAL_INFO>());
        Assert.Equal(48, Marshal.SizeOf<DisplayConfigController.TARGET_MODE>());
    }
}
