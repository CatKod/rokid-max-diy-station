// SPDX-License-Identifier: MIT
//
// Unit tests for the 64-byte HID packet parser.
//
// The primary fixture is the sample packet from angkaworn-dotcom/rokid-spatial's
// docs/PROTOCOL.md, taken with the glasses stationary and worn.

using System.Numerics;
using RokidDiyStation.Core.Protocol;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class PacketParserTests
{
    /// <summary>
    /// Hex dump of the canonical stationary+worn packet from PROTOCOL.md.
    ///
    ///   11 ba8ad99166010000 802262be 63531941 581411c0 9040213b 06a83fbb 88edab3a
    ///      cdcc2cc2 3333a642 000055c2 000000 5d72329166010000 000000503c000000
    ///
    /// Expected:
    ///   timestamp = 1 540 045 245 114 ns
    ///   accel     = (-0.221,  9.583, -2.267) m/s²
    ///   gyro      = ( 0.0025, -0.0029,  0.0013) rad/s
    ///   mag       = (-43.2,  83.1, -53.25) µT
    ///   proximity = 0 → not worn
    ///   host_ts    = earlier by ~10.95 ms
    /// </summary>
    private static readonly byte[] StationaryWornFixture = Convert.FromHexString(
        "11" +
        "ba8ad99166010000" +                  // timestamp ns
        "802262be63531941581411c0" +          // accel x,y,z
        "9040213b06a83fbb88edab3a" +          // gyro x,y,z
        "cdcc2cc23333a642000055c2" +          // mag x,y,z + reserved
        "000000" +                            // keys/proximity (worn byte = 0)
        "5d72329166010000" +                  // host timestamp #2
        "000000503c000000");                  // unknown + 3-byte pad

    [Fact]
    public void Parse_KnownGoodPacket_DecodesAllFields()
    {
        var result = PacketParser.Parse(StationaryWornFixture);

        Assert.True(result.IsOk, $"Parse failed: {result.Status}");

        var s = result.Value;
        // 1 540 045 245 114 ns — within ±1 µs of expected
        Assert.InRange(s.TimestampNs, 1_540_045_244_114ul, 1_540_045_246_114ul);

        // Allow small float-rounding tolerance (1e-3 m/s² etc.)
        AssertClose(new Vector3(-0.221f, 9.583f, -2.267f), s.Accel, 5e-3f);
        AssertClose(new Vector3(0.0025f, -0.0029f, 0.0013f), s.Gyro, 5e-4f);
        AssertClose(new Vector3(-43.2f, 83.1f, -53.25f), s.Mag, 1e-2f);

        Assert.False(s.Worn);
        Assert.NotEqual(0UL, s.HostTimestampNs);

        // Host timestamp is ~10.95 ms earlier than the device timestamp.
        var dtNs = s.TimestampNs - s.HostTimestampNs;
        Assert.InRange(dtNs, 10_000_000UL, 12_000_000UL);
    }

    [Fact]
    public void Parse_TooShortBuffer_ReturnsTooShort()
    {
        var buffer = new byte[32];
        buffer[0] = Constants.ReportIdImuData;

        var result = PacketParser.Parse(buffer);

        Assert.Equal(PacketParseStatus.TooShort, result.Status);
    }

    [Fact]
    public void Parse_EmptyBuffer_ReturnsTooShort()
    {
        var result = PacketParser.Parse(ReadOnlySpan<byte>.Empty);
        Assert.Equal(PacketParseStatus.TooShort, result.Status);
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x01)] // report ID 1 = output, not IMU
    [InlineData((byte)0x02)]
    [InlineData((byte)0xFF)]
    public void Parse_WrongReportId_ReturnsWrongReportId(byte reportId)
    {
        var buffer = new byte[Constants.ReportLength];
        buffer[0] = reportId;

        var result = PacketParser.Parse(buffer);

        Assert.Equal(PacketParseStatus.WrongReportId, result.Status);
    }

    [Fact]
    public void Parse_AllZerosButReportId_ReturnsOkWithZeroValues()
    {
        var buffer = new byte[Constants.ReportLength];
        buffer[0] = Constants.ReportIdImuData;

        var result = PacketParser.Parse(buffer);

        Assert.True(result.IsOk);
        var s = result.Value;
        Assert.Equal(0UL, s.TimestampNs);
        Assert.Equal(Vector3.Zero, s.Accel);
        Assert.Equal(Vector3.Zero, s.Gyro);
        Assert.Equal(Vector3.Zero, s.Mag);
        Assert.False(s.Worn);
    }

    [Fact]
    public void ParseOrThrow_TooShortBuffer_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => PacketParser.ParseOrThrow(new byte[10]));
    }

    [Fact]
    public void ParseOrThrow_WrongReportId_ThrowsInvalidDataException()
    {
        var buffer = new byte[Constants.ReportLength];
        buffer[0] = 0x00;
        Assert.Throws<InvalidDataException>(() => PacketParser.ParseOrThrow(buffer));
    }

    [Fact]
    public void ParseOrThrow_KnownGoodPacket_DoesNotThrow()
    {
        var sample = PacketParser.ParseOrThrow(StationaryWornFixture);
        Assert.NotEqual(0UL, sample.TimestampNs);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0f, tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0f, tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0f, tolerance);
    }
}