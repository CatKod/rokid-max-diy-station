// SPDX-License-Identifier: MIT
//
// Tests for the App-layer code that doesn't require a real HID
// device: AppSettings round-trip, SettingsService load/save, and
// AxisRemap parsing.
using System.IO;
using System.Numerics;
using System.Text.Json;
using RokidDiyStation.App;
using Xunit;

namespace RokidDiyStation.App.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Defaults_Are_Reasonable()
    {
        var s = AppSettings.Default;
        Assert.Equal("127.0.0.1", s.UdpHost);
        Assert.InRange(s.UdpPort, 1, 65535);
        Assert.True(s.UseMagnetometer);
        Assert.Equal(0.0, s.Smoothing);
        Assert.Equal(60.0, s.FovDegrees);
        Assert.False(s.Autostart);
        // Bug fix (M6.1): first launch should show the window so the
        // user can see something happened when they double-click the
        // exe.  Returning users can still enable "start minimised".
        Assert.False(s.StartMinimised);
    }

    [Fact]
    public void Record_Supports_With_Syntax()
    {
        var s = AppSettings.Default with { UdpPort = 6000, FovDegrees = 90 };
        Assert.Equal(6000, s.UdpPort);
        Assert.Equal(90.0, s.FovDegrees);
        // Immutability: the original is unchanged.
        Assert.Equal(5555, AppSettings.Default.UdpPort);
    }
}

public class SettingsServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rokid-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void Load_When_File_Missing_Creates_Defaults()
    {
        var s = new SettingsService(_path);
        var loaded = s.Load();
        Assert.Equal(AppSettings.Default, loaded);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void Roundtrip_Preserves_All_Fields()
    {
        var s = new SettingsService(_path);
        s.Load();
        var next = AppSettings.Default with
        {
            UdpHost = "10.0.0.5",
            UdpPort = 9000,
            UseMagnetometer = false,
            Smoothing = 0.42,
            FovDegrees = 75.0,
            Autostart = true,
            StartMinimised = false,
            MaxPacketsPerSecond = 120,
            AnchorHotkey = "F1",
            ResetHotkey = "F2",
            RecenterHotkey = "F3",
        };
        s.Update(next);
        var s2 = new SettingsService(_path);
        var loaded = s2.Load();
        Assert.Equal(next, loaded);
    }

    [Fact]
    public void Load_Corrupt_File_Falls_Back_To_Defaults()
    {
        File.WriteAllText(_path, "{this is not valid json");
        var s = new SettingsService(_path);
        var loaded = s.Load();
        Assert.Equal(AppSettings.Default, loaded);
    }

    [Fact]
    public void Save_Is_Atomic_No_Temp_Left_Behind()
    {
        var s = new SettingsService(_path);
        s.Load();
        s.Update(AppSettings.Default with { UdpPort = 7777 });
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }
}

public class AxisRemapTests
{
    [Theory]
    [InlineData("XYZ", 0, 1, 2)]
    [InlineData("XZY", 0, 2, 1)]
    [InlineData("YXZ", 1, 0, 2)]
    [InlineData("YZX", 1, 2, 0)]
    [InlineData("ZXY", 2, 0, 1)]
    [InlineData("ZYX", 2, 1, 0)]
    [InlineData(null, 0, 1, 2)]
    [InlineData("garbage", 0, 1, 2)]
    public void Parse(string? s, int x, int y, int z)
    {
        var r = AxisRemap.Parse(s);
        Assert.Equal(x, r.X);
        Assert.Equal(y, r.Y);
        Assert.Equal(z, r.Z);
    }

    [Fact]
    public void ToString_Roundtrip()
    {
        foreach (var name in new[] { "XYZ", "XZY", "YXZ", "YZX", "ZXY", "ZYX" })
        {
            Assert.Equal(name, AxisRemap.Parse(name).ToString());
        }
    }
}

public class OrientationSmootherTests
{
    [Fact]
    public void Alpha_Zero_Returns_Raw()
    {
        var s = new OrientationSmoother(alpha: 0);
        var (q, p) = s.Update(Quaternion.Identity, new Vector3(1, 2, 3));
        Assert.Equal(new Vector3(1, 2, 3), p);
        Assert.Equal(Quaternion.Identity, q);
    }

    [Fact]
    public void First_Sample_Seeds_State()
    {
        var s = new OrientationSmoother(alpha: 0.5);
        var (q1, _) = s.Update(Quaternion.Identity, Vector3.Zero);
        var (q2, p2) = s.Update(Quaternion.Identity, new Vector3(10, 0, 0));
        // Second sample should be ~halfway.
        Assert.InRange(p2.X, 4, 6);
        Assert.Equal(Quaternion.Identity, q1);
        Assert.Equal(Quaternion.Identity, q2);
    }
}
