// SPDX-License-Identifier: MIT
//
// Tests for the M5 spatial-anchoring foundation:
//   - GyroBiasEstimator accumulates only stationary samples
//   - StationaryDetector transitions: Moving -> Settling -> Stationary
//   - PositionIntegrator bleeds drift back to origin when stationary
//   - SpatialAnchor wires everything together and SetAnchorHere()
//     rebases the head pose to the identity at the origin

using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.Protocol;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class GyroBiasEstimatorTests
{
    [Fact]
    public void Bias_Starts_At_Zero()
    {
        var b = new GyroBiasEstimator(windowSize: 4);
        Assert.Equal(Vector3.Zero, b.Bias);
        Assert.False(b.IsCalibrated);
    }

    [Fact]
    public void Bias_Converges_To_Constant_Offset()
    {
        var b = new GyroBiasEstimator(windowSize: 8);
        var trueBias = new Vector3(0.01f, -0.02f, 0.005f);
        for (var i = 0; i < 16; i++) b.Add(trueBias);
        Assert.True(b.IsCalibrated);
        Assert.InRange(MathF.Abs(b.Bias.X - 0.01f), 0, 1e-6f);
        Assert.InRange(MathF.Abs(b.Bias.Y + 0.02f), 0, 1e-6f);
        Assert.InRange(MathF.Abs(b.Bias.Z - 0.005f), 0, 1e-6f);
    }

    [Fact]
    public void Bias_Freezes_After_Window_Fills()
    {
        var b = new GyroBiasEstimator(windowSize: 4);
        b.Add(new Vector3(0.01f, 0, 0));
        b.Add(new Vector3(0.01f, 0, 0));
        b.Add(new Vector3(0.01f, 0, 0));
        b.Add(new Vector3(0.01f, 0, 0));
        Assert.True(b.IsCalibrated);
        var frozen = b.Bias;
        // Adding more samples (even garbage) shouldn't move the bias.
        b.Add(new Vector3(99, 99, 99));
        Assert.Equal(frozen, b.Bias);
    }

    [Fact]
    public void Reset_Clears_State()
    {
        var b = new GyroBiasEstimator(windowSize: 4);
        b.Add(new Vector3(0.01f, 0, 0));
        b.Add(new Vector3(0.01f, 0, 0));
        b.Add(new Vector3(0.01f, 0, 0));
        b.Add(new Vector3(0.01f, 0, 0));
        Assert.True(b.IsCalibrated);
        b.Reset();
        Assert.False(b.IsCalibrated);
        Assert.Equal(Vector3.Zero, b.Bias);
    }
}

public class StationaryDetectorTests
{
    private static readonly Vector3 Gravity = new(0, StationaryDetector.GravityMagnitude, 0);
    private static readonly Vector3 NoGyro = Vector3.Zero;

    [Fact]
    public void State_Starts_Moving()
    {
        var d = new StationaryDetector();
        Assert.Equal(StationaryState.Moving, d.State);
    }

    [Fact]
    public void Quiet_Samples_Become_Settling()
    {
        var d = new StationaryDetector();
        var s = d.Update(NoGyro, Gravity, 0);
        Assert.Equal(StationaryState.Settling, s);
    }

    [Fact]
    public void Settling_Becomes_Stationary_After_Duration()
    {
        var d = new StationaryDetector(new StationaryDetectorOptions { MinDuration = TimeSpan.FromMilliseconds(100) });
        d.Update(NoGyro, Gravity, 0);
        d.Update(NoGyro, Gravity, 50_000_000);
        Assert.Equal(StationaryState.Settling, d.State);
        d.Update(NoGyro, Gravity, 150_000_000);
        Assert.Equal(StationaryState.Stationary, d.State);
    }

    [Fact]
    public void Loud_Gyro_Breaks_Settling()
    {
        var d = new StationaryDetector();
        d.Update(NoGyro, Gravity, 0);                     // settling
        d.Update(new Vector3(0.5f, 0, 0), Gravity, 1_000_000);  // loud!
        Assert.Equal(StationaryState.Moving, d.State);
    }

    [Fact]
    public void Loud_Accel_Breaks_Settling()
    {
        var d = new StationaryDetector();
        d.Update(NoGyro, Gravity, 0);
        d.Update(NoGyro, new Vector3(0, Gravity.Y + 5, 0), 1_000_000);  // accel > 1g threshold
        Assert.Equal(StationaryState.Moving, d.State);
    }
}

public class PositionIntegratorTests
{
    [Fact]
    public void Reset_Clears_State()
    {
        var p = new PositionIntegrator();
        p.Update(new Vector3(1, 0, 0), isStationary: false, 0);
        p.Update(new Vector3(1, 0, 0), isStationary: false, 10_000_000);
        Assert.NotEqual(Vector3.Zero, p.Position);
        p.Reset();
        Assert.Equal(Vector3.Zero, p.Position);
        Assert.Equal(Vector3.Zero, p.Velocity);
    }

    [Fact]
    public void Constant_Accel_Moves_Position()
    {
        var p = new PositionIntegrator();
        p.Update(Vector3.Zero, isStationary: false, 0);                  // prime
        p.Update(new Vector3(2, 0, 0), isStationary: false, 100_000_000);  // 2 m/s² for 0.1s
        // v = 0 + 2*0.1 = 0.2; x = 0 + 0.2*0.1 = 0.02 m
        Assert.InRange(p.Velocity.X, 0.19f, 0.21f);
        Assert.InRange(p.Position.X, 0.019f, 0.021f);
    }

    [Fact]
    public void ZUPT_Zeroes_Velocity_And_Decays_Position()
    {
        var p = new PositionIntegrator();
        // Build up some state.
        p.Update(Vector3.Zero, isStationary: false, 0);
        p.Update(new Vector3(2, 0, 0), isStationary: false, 100_000_000);
        p.Update(new Vector3(2, 0, 0), isStationary: false, 200_000_000);
        var posBeforeZupt = p.Position;
        var velBeforeZupt = p.Velocity;
        Assert.NotEqual(Vector3.Zero, posBeforeZupt);
        // Now stationary for many ticks.
        for (var i = 0; i < 50; i++)
        {
            p.Update(Vector3.Zero, isStationary: true, (ulong)(300_000_000L + i * 20_000_000L));
        }
        Assert.InRange(p.Velocity.Length(), 0, 0.001f);
        Assert.True(p.Position.Length() < posBeforeZupt.Length());
    }
}

public class SpatialAnchorTests
{
    private static ImuSample MakeSample(ulong tsNs, Vector3 accel, Vector3 gyro, Vector3? mag = null)
        => new(tsNs, accel, gyro, mag ?? new Vector3(20, 0, -45), Worn: true, HostTimestampNs: 0);

    [Fact]
    public void Anchor_Boot_Transitions_To_Calibrating()
    {
        var a = new SpatialAnchor();
        Assert.Equal(AnchorState.Booting, a.State);
        a.Update(MakeSample(1_000_000_000, new Vector3(0, 9.81f, 0), Vector3.Zero));
        Assert.Equal(AnchorState.Calibrating, a.State);
    }

    [Fact]
    public void Anchor_Calibrates_Gyro_Bias_From_Stationary_Samples()
    {
        var a = new SpatialAnchor();
        var bias = new Vector3(0.02f, -0.01f, 0.005f);
        var ts = 1_000_000_000UL;
        for (var i = 0; i < 1100; i++)
        {
            a.Update(MakeSample(ts, new Vector3(0, 9.81f, 0), bias));
            ts += 2_287_000UL;  // ~437 Hz
        }
        Assert.True(a.IsGyroCalibrated);
        Assert.InRange(MathF.Abs(a.GyroBias.X - 0.02f), 0, 1e-3f);
        Assert.InRange(MathF.Abs(a.GyroBias.Y + 0.01f), 0, 1e-3f);
    }

    [Fact]
    public void Anchor_Set_Anchor_Here_Resets_Position()
    {
        var a = new SpatialAnchor();
        var ts = 1_000_000_000UL;
        // Calibrate
        for (var i = 0; i < 1100; i++)
        {
            a.Update(MakeSample(ts, new Vector3(0, 9.81f, 0), Vector3.Zero));
            ts += 2_287_000UL;
        }
        // Apply a strong non-gravitational accel: a clear ~5 m/s² on X
        // with Y reduced so the device-frame |a| is far from g and the
        // stationary detector triggers motion.
        for (var i = 0; i < 300; i++)
        {
            a.Update(MakeSample(ts, new Vector3(5f, 5f, 0), Vector3.Zero));
            ts += 2_287_000UL;
        }
        Assert.False(a.IsStationary, "detector should report moving with strong accel");
        Assert.True(a.HeadPosition.Length() > 0.01f,
            $"expected position to grow past 0.01m, got {a.HeadPosition}");
        a.SetAnchorHere();
        Assert.InRange(a.HeadPosition.Length(), 0, 0.1f);
        Assert.InRange(1f - MathF.Abs(a.HeadOrientation.W), 0, 0.1f);  // close to identity
    }

    [Fact]
    public void Anchor_Resets_Cleanly()
    {
        var a = new SpatialAnchor();
        var ts = 1_000_000_000UL;
        for (var i = 0; i < 50; i++)
        {
            a.Update(MakeSample(ts, new Vector3(0, 9.81f, 0), Vector3.Zero));
            ts += 2_287_000UL;
        }
        a.Reset();
        Assert.Equal(AnchorState.Booting, a.State);
        Assert.Equal(Vector3.Zero, a.HeadPosition);
        Assert.Equal(Quaternion.Identity, a.HeadOrientation);
        Assert.Equal(0, a.SampleCount);
    }
}
