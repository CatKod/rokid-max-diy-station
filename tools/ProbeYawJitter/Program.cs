// SPDX-License-Identifier: MIT
//
// Probe: the final verification of the two fixes.
//
//   1. THE MAGNETOMETER.  The heading correction is gone.  A true
//      90-degree turn must be reported as 90 degrees, not 142.
//
//   2. THE LAZY FOLLOW.  The panel must never teleport, and its
//      on-screen motion must not alternate frame to frame.
//
// Usage:
//   dotnet run --project tools/ProbeYawJitter

using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.Protocol;
using RokidDiyStation.Core.Spatial;

internal static class Program
{
    private const float Gravity = 9.80665f;
    private const double Deg2Rad = Math.PI / 180.0;
    private const double SampleHz = 437.0;
    private const int FrameMs = 16;

    private static void Main()
    {
        Console.WriteLine("=========================================================");
        Console.WriteLine(" FIX 1: the magnetometer");
        Console.WriteLine("=========================================================");
        Console.WriteLine();
        Console.WriteLine("true yaw    reported yaw    error");
        foreach (var deg in new[] { 30f, 60f, 90f, 120f })
        {
            var reported = RunYawTurn(deg);
            Console.WriteLine($"{deg,8:F0}    {reported,11:F2}    {reported - deg,8:F2}");
        }

        Console.WriteLine();
        Console.WriteLine("=========================================================");
        Console.WriteLine(" FIX 2: the lazy follow");
        Console.WriteLine("=========================================================");
        Console.WriteLine();
        Snap();
        Smoothness();
    }

    private static float RunYawTurn(float totalDeg)
    {
        var f = new MahonyFusion();
        var indoorField = new Vector3(95f, 12f, -52f);
        var dt = (float)(1.0 / SampleHz);
        ulong ts = 1_000_000_000UL;
        var tsStep = (ulong)(1e9 / SampleHz);

        for (var i = 0; i < 437; i++)
        {
            f.Update(new ImuSample(ts, new Vector3(0f, Gravity, 0f), Vector3.Zero, Vector3.Zero, true, ts));
            ts += tsStep;
        }

        const float rateDeg = 30f;
        var total = 0f;
        while (total < totalDeg)
        {
            var q = SyntheticImuSource.FromEulerZYX(0f, 0f, total * (float)Deg2Rad);
            var up = Vector3.Transform(Vector3.UnitY, Quaternion.Inverse(q));
            var a = Vector3.Transform(new Vector3(0f, Gravity, 0f), Quaternion.Inverse(q));
            var m = Vector3.Transform(indoorField, Quaternion.Inverse(q));
            f.Update(new ImuSample(ts, a, up * (float)(rateDeg * Deg2Rad), m, true, ts));
            ts += tsStep;
            total += rateDeg * dt;
        }

        for (var i = 0; i < 220; i++)
        {
            var q = SyntheticImuSource.FromEulerZYX(0f, 0f, total * (float)Deg2Rad);
            var a = Vector3.Transform(new Vector3(0f, Gravity, 0f), Quaternion.Inverse(q));
            var m = Vector3.Transform(indoorField, Quaternion.Inverse(q));
            f.Update(new ImuSample(ts, a, Vector3.Zero, m, true, ts));
            ts += tsStep;
        }

        var forward = Vector3.Transform(-Vector3.UnitZ, f.Orientation);
        return HorizontalAngle(Vector3.Normalize(forward));
    }

    private static void Snap()
    {
        Console.WriteLine("--- the teleport test: out past the cone, then back ---");
        var service = MakeService();
        var panel = service.Panel!;
        service.GetView();

        var angles = new List<float>();
        for (var frame = 0; frame < 360; frame++)
        {
            DriveFrame(service, frame < 180 ? -30f : 30f);
            angles.Add(PanelAngle(service, panel));
        }

        var maxStep = 0f;
        var maxAt = -1;
        for (var i = 1; i < angles.Count; i++)
        {
            var s = MathF.Abs(angles[i] - angles[i - 1]);
            if (s > maxStep) { maxStep = s; maxAt = i; }
        }

        Console.WriteLine($"  biggest single-frame step: {maxStep:F3} deg ({maxStep * 60f:F1} deg/s) at frame {maxAt}");
        Console.WriteLine($"  before the fix this was:  56.100 deg (3366.0 deg/s)");
        Console.WriteLine($"  {(maxStep * 60f < 200f ? "PASS - the panel glides" : "FAIL - it still teleports")}");
        Console.WriteLine();
    }

    private static void Smoothness()
    {
        Console.WriteLine("--- the stutter test: on-screen motion during a smooth turn ---");
        var service = MakeService();
        service.GetView();

        var ndc = new List<float>();
        for (var frame = 0; frame < 120; frame++)
        {
            DriveFrame(service, -30f);
            var view = service.GetView();
            var mvp = view.View * service.GetProjection();
            var centre = (view.PanelCorners[0] + view.PanelCorners[2]) * 0.5f;
            var clip = Vector4.Transform(new Vector4(centre, 1f), mvp);
            ndc.Add(clip.X / clip.W);
        }

        var steps = ndc.Skip(30).Zip(ndc.Skip(31), (a, b) => b - a).ToArray();
        var zero = steps.Count(s => MathF.Abs(s) < 1e-9f);

        var mean = steps.Average();
        var centred = steps.Select(s => s - mean).ToArray();
        var cov = 0f; var var2 = 0f;
        for (var i = 0; i < centred.Length - 1; i++) cov += centred[i] * centred[i + 1];
        foreach (var c in centred) var2 += c * c;
        var autocorr = var2 > 1e-18f ? cov / var2 : 1f;

        Console.WriteLine($"  frozen frames:            {zero} of {steps.Length}  (before: ~20 %)");
        Console.WriteLine($"  lag-1 autocorrelation:    {autocorr:F2}  (+1 = smooth flow, -1 = frame-to-frame alternation)");
        Console.WriteLine($"  {(zero == 0 && autocorr > 0.5f ? "PASS - the motion flows" : "FAIL - the motion alternates")}");
        Console.WriteLine();
    }

    private static SpatialAnchorService MakeService()
    {
        var service = new SpatialAnchorService(new SpatialAnchorOptions
        {
            LazyFollowDeadzoneDegrees = 15f,
            LazyFollowRatePerSecond = 3f,
            PanelDistance = 1.5f,
            PanelFovFraction = 0.5f,
            NeckModelScale = 0f,
        });

        var q0 = SyntheticImuSource.FromEulerZYX(0f, (float)(-25.0 * Deg2Rad), 0f);
        var accel = Vector3.Transform(new Vector3(0f, Gravity, 0f), Quaternion.Inverse(q0));

        ulong ts = 1_000_000_000UL;
        var tsStep = (ulong)(1e9 / SampleHz);
        for (var i = 0; i < 4000; i++)
        {
            service.Update(new ImuSample(ts, accel, Vector3.Zero, Vector3.Zero, true, ts));
            ts += tsStep;
        }
        service.AnchorHere();
        return service;
    }

    private static ulong _ts = 1_000_000_000UL;
    private static readonly ulong TsStep = (ulong)(1e9 / SampleHz);

    private static void DriveFrame(SpatialAnchorService service, float yawRateDeg)
    {
        var q0 = SyntheticImuSource.FromEulerZYX(0f, (float)(-25.0 * Deg2Rad), 0f);
        var accel = Vector3.Transform(new Vector3(0f, Gravity, 0f), Quaternion.Inverse(q0));
        var samplesPerFrame = (int)(SampleHz * FrameMs / 1000.0);
        for (var k = 0; k < samplesPerFrame; k++)
        {
            var up = Vector3.Transform(Vector3.UnitY, Quaternion.Inverse(service.Anchor.HeadOrientation));
            service.Update(new ImuSample(_ts, accel, up * (float)(yawRateDeg * Deg2Rad), Vector3.Zero, true, _ts));
            _ts += TsStep;
        }
        Thread.Sleep(FrameMs);
    }

    private static float PanelAngle(SpatialAnchorService service, PinnedPanel panel)
    {
        var view = service.GetView();
        var centre = (view.PanelCorners[0] + view.PanelCorners[2]) * 0.5f;
        return HorizontalAngle(Vector3.Normalize(centre - panel.EyeAtAnchor));
    }

    private static float HorizontalAngle(Vector3 v)
    {
        var flat = new Vector3(v.X, 0f, v.Z);
        if (flat.LengthSquared() < 1e-9f) return 0f;
        flat = Vector3.Normalize(flat);
        return -(float)(Math.Atan2(flat.X, -flat.Z) / Deg2Rad);
    }
}
