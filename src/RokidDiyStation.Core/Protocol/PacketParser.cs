// SPDX-License-Identifier: MIT
//
// Decodes raw 64-byte HID reports from the Rokid Max into <see cref="ImuSample"/>.
//
// The byte layout is documented in detail in docs/PROTOCOL.md. The short version
// (offsets within the report buffer, byte 0 is the HID report ID):
//
//   [ 0]  u8     report ID                — 0x11
//   [ 1..8]    u64 LE  timestamp ns             — device clock
//   [ 9..20]   f32[3]  accelerometer (m/s²)
//   [21..32]   f32[3]  gyroscope    (rad/s)
//   [33..44]   f32[3]  magnetometer (µT)
//   [45..47]   u8[3]   keys/proximity (last byte = worn bit)
//   [48..55]   u64 LE  host timestamp           — ~10.95 ms earlier
//
// All multi-byte numbers are little-endian.

using System.Buffers.Binary;
using System.Numerics;

namespace RokidDiyStation.Core.Protocol;

/// <summary>
/// Result of attempting to parse a HID report.
/// </summary>
public enum PacketParseStatus
{
    /// <summary>Packet decoded successfully.</summary>
    Ok,
    /// <summary>Packet is shorter than <see cref="Constants.ReportLength"/>.</summary>
    TooShort,
    /// <summary>Byte 0 is not the IMU report ID (0x11).</summary>
    WrongReportId,
}

/// <summary>
/// Result envelope that avoids the cost of throwing on every malformed packet.
/// </summary>
/// <typeparam name="T">Parsed payload type.</typeparam>
/// <param name="Status">What happened during the parse attempt.</param>
/// <param name="Value">The decoded value when <paramref name="Status"/> is <see cref="PacketParseStatus.Ok"/>; default otherwise.</param>
public readonly record struct PacketParseResult<T>(PacketParseStatus Status, T Value)
{
    public bool IsOk => Status == PacketParseStatus.Ok;

    public static PacketParseResult<T> Ok(T value) => new(PacketParseStatus.Ok, value);
    public static PacketParseResult<T> TooShort() => new(PacketParseStatus.TooShort, default!);
    public static PacketParseResult<T> WrongReportId(byte actual) => new(PacketParseStatus.WrongReportId, default!);
}

/// <summary>
/// Stateless parser that decodes a single 64-byte HID report into an <see cref="ImuSample"/>.
/// </summary>
public static class PacketParser
{
    // Absolute byte offsets within the 64-byte report buffer (report-ID byte included).
    private const int OffsetReportId = 0;
    private const int OffsetTimestamp = 1;
    private const int OffsetAccel = 9;
    private const int OffsetGyro = 21;
    private const int OffsetMag = 33;
    private const int OffsetKeysStart = 45;     // bytes 45..47 inclusive
    private const int OffsetWorn = 47;          // proximity bit — last byte of the 3-byte keys region
    private const int OffsetHostTimestamp = 48;

    /// <summary>Parse a 64-byte HID report into an <see cref="ImuSample"/>.</summary>
    /// <param name="data">Raw bytes from the HID device. Must be at least 56 bytes long.</param>
    /// <returns>A parse result whose status describes whether decoding worked.</returns>
    public static PacketParseResult<ImuSample> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Constants.ReportLength)
        {
            return PacketParseResult<ImuSample>.TooShort();
        }

        var reportId = data[OffsetReportId];
        if (reportId != Constants.ReportIdImuData)
        {
            return PacketParseResult<ImuSample>.WrongReportId(reportId);
        }

        var timestamp = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(OffsetTimestamp, 8));
        var accel = ReadVector3(data, OffsetAccel);
        var gyro = ReadVector3(data, OffsetGyro);
        var mag = ReadVector3(data, OffsetMag);

        // The "worn" bit is the last byte of the 3-byte keys/proximity region
        // (byte 47). Per PROTOCOL.md this region is zero when idle and goes
        // non-zero on wear detection.
        var worn = data[OffsetWorn] != 0;

        var hostTimestamp = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(OffsetHostTimestamp, 8));

        var sample = new ImuSample(
            TimestampNs: timestamp,
            Accel: accel,
            Gyro: gyro,
            Mag: mag,
            Worn: worn,
            HostTimestampNs: hostTimestamp);

        return PacketParseResult<ImuSample>.Ok(sample);
    }

    /// <summary>
    /// Throwing convenience wrapper for callers that prefer exceptions.
    /// </summary>
    /// <exception cref="ArgumentException">If the buffer is too short.</exception>
    /// <exception cref="InvalidDataException">If the report ID does not match.</exception>
    public static ImuSample ParseOrThrow(ReadOnlySpan<byte> data)
    {
        var result = Parse(data);
        return result.Status switch
        {
            PacketParseStatus.Ok => result.Value,
            PacketParseStatus.TooShort => throw new ArgumentException(
                $"Packet length {data.Length} is below minimum {Constants.ReportLength}",
                nameof(data)),
            PacketParseStatus.WrongReportId => throw new InvalidDataException(
                $"Unexpected report ID 0x{result.Value.TimestampNs:X2}; expected IMU 0x{Constants.ReportIdImuData:X2}"),
            _ => throw new InvalidOperationException("Unknown parse status."),
        };
    }

    private static Vector3 ReadVector3(ReadOnlySpan<byte> data, int offset)
    {
        var x = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4));
        var y = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 4, 4));
        var z = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 8, 4));
        return new Vector3(x, y, z);
    }
}