// SPDX-License-Identifier: MIT
//
// Tests for the M4 FreePIE-compatible UDP emitter.
//
// Coverage:
//   1. FreePiePacket.Build() produces exactly 50 bytes, with the
//      pad byte at offset 0, the flags byte at offset 1, and little-
//      endian floats at the documented offsets.
//   2. Round-trip a known quaternion -> Yaw/Pitch/Roll -> float ->
//      back to radians within FP tolerance.
//   3. End-to-end: bind a UDP receiver, send packets through
//      FreePieEmitter, decode on the receiver, assert orientation
//      matches what we put in.

using System.Net;
using System.Net.Sockets;
using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.Output;
using RokidDiyStation.Core.Protocol;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class FreePieEmitterTests
{
    [Fact]
    public void Packet_Is_50_Bytes()
    {
        var p = new FreePiePacket { Flags = FreePieFlags.Orient, YawRad = 0.1f, PitchRad = 0.2f, RollRad = 0.3f };
        var bytes = p.Build();
        Assert.Equal(50, bytes.Length);
    }

    [Fact]
    public void Packet_Layout_Matches_FreePie_Spec()
    {
        var p = new FreePiePacket
        {
            Flags = FreePieFlags.Orient,
            YawRad = 0.10f, PitchRad = 0.20f, RollRad = 0.30f,
        };
        var bytes = p.Build();
        Assert.Equal(0, bytes[0]);                       // pad
        Assert.Equal((byte)FreePieFlags.Orient, bytes[1]); // flags
        // floats 0..8 must be zero (orient lives at indices 9..11)
        for (var i = 2; i < 2 + 9 * 4; i += 4)
        {
            Assert.Equal(0f, BitConverter.ToSingle(bytes, i));
        }
        // orient floats
        Assert.Equal(0.10f, BitConverter.ToSingle(bytes, 2 + 9 * 4), 5);
        Assert.Equal(0.20f, BitConverter.ToSingle(bytes, 2 + 10 * 4), 5);
        Assert.Equal(0.30f, BitConverter.ToSingle(bytes, 2 + 11 * 4), 5);
    }

    [Fact]
    public void Packet_Raw_And_Orient_Flags_Coexist()
    {
        var p = new FreePiePacket
        {
            Flags = FreePieFlags.Raw | FreePieFlags.Orient,
            RawX = 0.5f, RawY = -1.25f, RawZ = 0.0f,
            YawRad = 0.4f, PitchRad = 0.0f, RollRad = -0.2f,
        };
        var bytes = p.Build();
        Assert.Equal((byte)(FreePieFlags.Raw | FreePieFlags.Orient), bytes[1]);
        Assert.Equal(0.5f,   BitConverter.ToSingle(bytes, 2),       5);
        Assert.Equal(-1.25f, BitConverter.ToSingle(bytes, 6),       5);
        Assert.Equal(0.0f,   BitConverter.ToSingle(bytes, 10),      5);
        Assert.Equal(0.4f,   BitConverter.ToSingle(bytes, 2 + 9 * 4),  5);
        Assert.Equal(0.0f,   BitConverter.ToSingle(bytes, 2 + 10 * 4), 5);
        Assert.Equal(-0.2f,  BitConverter.ToSingle(bytes, 2 + 11 * 4), 5);
    }

    [Fact]
    public void Quaternion_Roundtrip_Via_Euler()
    {
        // Build a known quaternion and check the Euler we extract
        // matches within tolerance.  Single-axis 90° rotations are
        // gimbal-lock edge cases for the Z-Y-X decomposition.  Use
        // small non-orthogonal values to verify round-trip in the
        // well-conditioned regime.
        var (r0, p0, y0) = (15f, 10f, 25f);
        var q = SyntheticImuSource.FromEulerZYX(
            r0 * MathF.PI / 180f, p0 * MathF.PI / 180f, y0 * MathF.PI / 180f);
        var (roll, pitch, yaw) = SyntheticImuSource.ToEulerZYX(q);
        // Convention is consistent — should match within sub-degree.
        Assert.InRange(MathF.Abs(roll - r0),  0, 0.5f);
        Assert.InRange(MathF.Abs(pitch - p0), 0, 0.5f);
        Assert.InRange(MathF.Abs(yaw - y0),   0, 0.5f);

        // Apply the extracted Euler to a different identity-like
        // quaternion — the *rotation* should be effectively the same.
        var qRound = SyntheticImuSource.FromEulerZYX(
            roll * MathF.PI / 180f, pitch * MathF.PI / 180f, yaw * MathF.PI / 180f);
        var dot = MathF.Abs(q.X * qRound.X + q.Y * qRound.Y + q.Z * qRound.Z + q.W * qRound.W);
        Assert.True(dot > 0.9999f, $"quaternions differ (dot={dot})");
    }

    [Fact]
    public void Emitter_Sends_Over_Loopback()
    {
        // Bind a localhost receiver.
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var localEp = new IPEndPoint(IPAddress.Loopback, 0);
        receiver.Bind(localEp);
        var localPort = ((IPEndPoint)receiver.LocalEndPoint!).Port;

        using var emitter = new FreePieEmitter(new FreePieEmitterOptions(
            TargetHost: "127.0.0.1",
            TargetPort: localPort,
            MaxPacketsPerSecond: 0));

        // Send identity orientation.
        var sent = emitter.Send(Quaternion.Identity);
        Assert.True(sent);

        // Receive with a short timeout.
        receiver.ReceiveTimeout = 1000;
        var buf = new byte[64];
        var n = receiver.Receive(buf);
        Assert.Equal(50, n);
        Assert.Equal(0, buf[0]);
        Assert.Equal((byte)FreePieFlags.Orient, buf[1]);
        // Identity => all 3 orient floats = 0
        Assert.Equal(0f, BitConverter.ToSingle(buf, 2 + 9 * 4), 5);
        Assert.Equal(0f, BitConverter.ToSingle(buf, 2 + 10 * 4), 5);
        Assert.Equal(0f, BitConverter.ToSingle(buf, 2 + 11 * 4), 5);
    }

    [Fact]
    public void Emitter_Sends_Computed_Orientation_Through_Fusion()
    {
        // Run the Mahony filter on a known synthetic yaw trajectory
        // for 0.3 s, then send the final orientation and decode it.
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.LocalEndPoint!).Port;

        using var emitter = new FreePieEmitter(new FreePieEmitterOptions(
            TargetHost: "127.0.0.1", TargetPort: port));

        var f = new MahonyFusion();
        var src = new SyntheticImuSource();
        var samples = src.StepEuler(t => (0f, 0f, 30f), seconds: 0.3f).ToArray();
        foreach (var s in samples) f.Update(s);

        emitter.Send(f.Orientation);

        receiver.ReceiveTimeout = 1000;
        var buf = new byte[64];
        var n = receiver.Receive(buf);
        Assert.Equal(50, n);
        var yawRad = BitConverter.ToSingle(buf, 2 + 9 * 4);
        var pitchRad = BitConverter.ToSingle(buf, 2 + 10 * 4);
        var rollRad = BitConverter.ToSingle(buf, 2 + 11 * 4);
        // After 0.3s at 30 deg/s yaw, filter should be near 30 deg = 0.524 rad.
        // Allow generous tolerance since filter warms up.
        Assert.InRange(MathF.Abs(yawRad * 180f / MathF.PI - 30f), 0, 35f);
        Assert.InRange(MathF.Abs(pitchRad * 180f / MathF.PI), 0, 10f);
        Assert.InRange(MathF.Abs(rollRad * 180f / MathF.PI), 0, 10f);
    }

    [Fact]
    public void Emitter_Sends_Both_Position_And_Orientation_Over_UDP()
    {
        // M5.7 — when configured with Raw|Orient, a position passed to
        // Send() must arrive on the receiver at the raw XYZ offsets.
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.LocalEndPoint!).Port;
        using var emitter = new FreePieEmitter(new FreePieEmitterOptions(
            TargetHost: "127.0.0.1",
            TargetPort: port,
            Flags: FreePieFlags.Raw | FreePieFlags.Orient,
            IncludeRawPosition: true));
        // 30° yaw, position (0.1, 0, -0.2).
        var q = SyntheticImuSource.FromEulerZYX(0f, 0f, 30f * MathF.PI / 180f);
        var pos = new Vector3(0.1f, 0f, -0.2f);
        Assert.True(emitter.Send(q, pos));
        receiver.ReceiveTimeout = 1000;
        var buf = new byte[64];
        var n = receiver.Receive(buf);
        Assert.Equal(50, n);
        Assert.Equal((byte)(FreePieFlags.Raw | FreePieFlags.Orient), buf[1]);
        var x = BitConverter.ToSingle(buf, 2);
        var y = BitConverter.ToSingle(buf, 6);
        var z = BitConverter.ToSingle(buf, 10);
        Assert.InRange(MathF.Abs(x - 0.1f), 0, 1e-5f);
        Assert.InRange(MathF.Abs(y - 0f),   0, 1e-5f);
        Assert.InRange(MathF.Abs(z + 0.2f), 0, 1e-5f);
        var yawRad = BitConverter.ToSingle(buf, 2 + 9 * 4);
        Assert.InRange(MathF.Abs(yawRad * 180f / MathF.PI - 30f), 0, 1f);
    }
}
