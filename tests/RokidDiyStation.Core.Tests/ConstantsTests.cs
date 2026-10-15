// SPDX-License-Identifier: MIT
//
// Sanity checks on the VID / PID / report-ID constants.

using RokidDiyStation.Core;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class ConstantsTests
{
    [Fact]
    public void RokidVendorId_Is_0x04D2()
    {
        Assert.Equal(0x04D2, Constants.RokidVendorId);
    }

    [Fact]
    public void RokidMax_Is_InProductMap()
    {
        Assert.True(Constants.RokidProductIds.ContainsKey(0x162F));
        Assert.Equal("Rokid Max", Constants.RokidProductIds[0x162F]);
    }

    [Theory]
    [InlineData(0x162B, "Rokid Air")]
    [InlineData(0x162C, "Rokid Air Plus")]
    [InlineData(0x162F, "Rokid Max")]
    [InlineData(0x2002, "Rokid Max 2")]
    [InlineData(0x2180, "Rokid Max Pro")]
    public void AllRookidPids_HaveNames(int pid, string expectedName)
    {
        Assert.Equal(expectedName, Constants.RokidProductIds[pid]);
    }

    [Fact]
    public void ImuReportId_Is_0x11()
    {
        Assert.Equal((byte)0x11, Constants.ReportIdImuData);
    }

    [Fact]
    public void ReportLength_Is_64()
    {
        Assert.Equal(64, Constants.ReportLength);
    }
}