// SPDX-License-Identifier: MIT
//
// Tests for the IHidCapture abstraction. Uses an in-memory fake so we never
// touch a real USB device from the test process.

using System.Collections.Concurrent;
using RokidDiyStation.Core.HidCapture;
using Xunit;

namespace RokidDiyStation.Core.Tests;

public class FakeHidCapture : IHidCapture
{
    private readonly ConcurrentQueue<byte[]> _reports = new();
    public int VendorId { get; init; } = Constants.RokidVendorId;
    public int ProductId { get; init; } = 0x162F;
    public string ProductName { get; init; } = "Rokid Max";
    public string SerialNumber { get; init; } = "SN-FAKE-001";
    public bool Disposed { get; private set; }

    public void Enqueue(byte[] report) => _reports.Enqueue(report);

    public bool TryRead(int timeoutMs, out byte[] report)
    {
        // Tiny spin-wait so the timeout path is exercised.
        var deadline = Environment.TickCount + timeoutMs;
        while (_reports.IsEmpty && Environment.TickCount < deadline)
        {
            Thread.Sleep(1);
        }
        if (_reports.TryDequeue(out var next))
        {
            report = next;
            return true;
        }
        report = Array.Empty<byte>();
        return false;
    }

    public void Dispose() => Disposed = true;
}

public class HidCaptureContractTests
{
    [Fact]
    public void Fake_Honours_TryReadContract()
    {
        var fake = new FakeHidCapture();
        fake.Enqueue(new byte[64]);

        Assert.True(fake.TryRead(timeoutMs: 50, out var report));
        Assert.Equal(64, report.Length);

        Assert.False(fake.TryRead(timeoutMs: 5, out var empty));
        Assert.Empty(empty);
    }

    [Fact]
    public void Fake_Exposes_RokidIdentity()
    {
        var fake = new FakeHidCapture();
        Assert.Equal(0x04D2, fake.VendorId);
        Assert.Equal(0x162F, fake.ProductId);
        Assert.Equal("Rokid Max", fake.ProductName);
        Assert.False(fake.Disposed);
        fake.Dispose();
        Assert.True(fake.Disposed);
    }
}