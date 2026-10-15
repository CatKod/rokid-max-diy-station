// SPDX-License-Identifier: MIT
//
// FreePIE-compatible UDP orientation emitter.
//
// We speak the same wire format that opentrack's `tracker-freepie-udp`
// receives (and that the FreePIE scripting hub historically emitted).
// That format is the easiest way to feed the IMU orientation into
// opentrack on a second machine, or directly into any game/emu that
// implements this protocol.
//
// Wire format (little-endian, packed, 50 bytes total):
//
//   offset  size  field
//   ------  ----  -----
//   0       1     pad (always 0)
//   1       1     flags: bit0=Raw, bit1=Orient
//   2       48    float[12]  -- see below
//
// With flags = Orient (2):
//   fl[0..2] = yaw, pitch, roll        (radians)
//   fl[3..8] = 0
//   fl[9..11] = 0
//
// With flags = Raw | Orient (3):
//   fl[0..2] = raw X, Y, Z            (metres)
//   fl[3..8] = 0
//   fl[9..11] = yaw, pitch, roll      (radians)
//
// Source for the layout:
//   https://github.com/opentrack/opentrack/blob/master/
//     tracker-freepie-udp/ftnoir_tracker_freepie-udp.cpp
//
// Sign convention in opentrack's receiver:
//   yaw   positive to the left
//   pitch positive up
//   roll  positive to the left
// The Rokid device frame has the Y axis as yaw (gimbal-locked when
// |pitch| approaches 90°), so the conventions match.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using RokidDiyStation.Core.Fusion;

namespace RokidDiyStation.Core.Output;

[Flags]
public enum FreePieFlags : byte
{
    /// <summary>No fields populated — packet is just padding/heartbeat.</summary>
    None = 0,
    /// <summary>First three floats are raw position X, Y, Z (metres).</summary>
    Raw = 1 << 0,
    /// <summary>Last three floats are yaw, pitch, roll (radians).</summary>
    Orient = 1 << 1,
}

/// <summary>
/// Mutable FreePIE UDP packet.  Owns a 50-byte backing buffer so the
/// hot-path can reuse the same allocation across frames.
/// </summary>
public sealed class FreePiePacket
{
    public const int SizeBytes = 50;

    private readonly byte[] _buffer = new byte[SizeBytes];

    public FreePiePacket()
    {
        _buffer.AsSpan(0, 2).Clear(); // pad + flags = 0
    }

    /// <summary>Last populated orientation.  Defaults to identity.</summary>
    public float YawRad { get; set; }
    public float PitchRad { get; set; }
    public float RollRad { get; set; }
    public float RawX { get; set; }
    public float RawY { get; set; }
    public float RawZ { get; set; }
    public FreePieFlags Flags { get; set; } = FreePieFlags.Orient;

    /// <summary>Write the current state into the internal 50-byte buffer and return it as a byte array reference.</summary>
    /// <remarks>Caller must consume the array before the next Build() call (it is reused).</remarks>
    public byte[] Build()
    {
        _buffer[0] = 0;
        _buffer[1] = (byte)Flags;
        // clear float region up front (cheap; only 48 bytes)
        for (var i = 2; i < SizeBytes; i += 4)
        {
            _buffer[i + 0] = 0;
            _buffer[i + 1] = 0;
            _buffer[i + 2] = 0;
            _buffer[i + 3] = 0;
        }
        if (Flags.HasFlag(FreePieFlags.Raw))
        {
            BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(2),  RawX);
            BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(6),  RawY);
            BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(10), RawZ);
        }
        if (Flags.HasFlag(FreePieFlags.Orient))
        {
            BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(2 + 9 * 4), YawRad);
            BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(2 + 10 * 4), PitchRad);
            BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(2 + 11 * 4), RollRad);
        }
        return _buffer;
    }
}

/// <summary>Settings for <see cref="FreePieEmitter"/>.</summary>
public sealed record FreePieEmitterOptions(
    string TargetHost = "127.0.0.1",
    int TargetPort = 5555,
    int MaxPacketsPerSecond = 250,
    FreePieFlags Flags = FreePieFlags.Orient,
    bool IncludeRawPosition = false)
{
    public static readonly FreePieEmitterOptions Default = new();
}

/// <summary>
/// Sends FreePIE-compatible orientation packets over UDP at a
/// configurable rate.  Pulls fresh orientation from a
/// <see cref="MahonyFusion"/> each tick.
/// </summary>
public sealed class FreePieEmitter : IDisposable
{
    private readonly FreePieEmitterOptions _opts;
    private readonly Socket _socket;
    private readonly IPEndPoint _endpoint;
    private readonly FreePiePacket _packet = new();

    public long PacketsSent { get; private set; }
    public long PacketsFailed { get; private set; }

    public FreePieEmitter(FreePieEmitterOptions? options = null)
    {
        _opts = options ?? FreePieEmitterOptions.Default;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            Blocking = false,
        };
        _endpoint = new IPEndPoint(IPAddress.Parse(_opts.TargetHost), _opts.TargetPort);
        _packet.Flags = _opts.IncludeRawPosition
            ? _opts.Flags | FreePieFlags.Raw
            : _opts.Flags;
    }

    /// <summary>
    /// Send one packet using the latest orientation of <paramref name="fusion"/>.
    /// </summary>
    /// <returns>True if the datagram was dispatched (not necessarily received).</returns>
    public bool Send(in Quaternion orientation, Vector3? rawPosition = null)
    {
        // Convert quaternion to Yaw/Pitch/Roll in radians using the
        // same Z-Y-X intrinsic convention the fusion filter uses, then
        // flip yaw sign so positive yaw points LEFT in FreePIE coords
        // (the Rokid device has positive Y = turn-left).
        var (rollDeg, pitchDeg, yawDeg) = SyntheticImuSource.ToEulerZYX(orientation);
        _packet.YawRad   = yawDeg   * MathF.PI / 180f;
        _packet.PitchRad = pitchDeg * MathF.PI / 180f;
        _packet.RollRad  = rollDeg  * MathF.PI / 180f;
        if (rawPosition is Vector3 p)
        {
            _packet.RawX = p.X; _packet.RawY = p.Y; _packet.RawZ = p.Z;
        }
        var bytes = _packet.Build();
        try
        {
            _socket.SendTo(bytes, bytes.Length, SocketFlags.None, _endpoint);
            PacketsSent++;
            return true;
        }
        catch (SocketException)
        {
            PacketsFailed++;
            return false;
        }
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}
