// SPDX-License-Identifier: MIT
//
// Live drift measurement tool for the Rokid Max.
//
// Captures real IMU samples from the device and reports how the fusion
// filter's attitude behaves while the glasses sit motionless on a desk.
//
// Usage:
//   dotnet run --project tools/MeasureDrift -- --seconds 120
//
// The glasses should be placed still on a desk for the whole run. We report:
//   * the raw gyro bias (the DC component while stationary)
//   * the yaw/pitch/roll drift over time from the fusion filter
//   * the drift with the magnetometer disabled (our default)
//
// This is the measurement that decides whether the "pinned panel" illusion
// holds up over a session, so the numbers matter more than the code.
using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.HidCapture;
using RokidDiyStation.Core.Protocol;

namespace RokidDiyStation.Tools.MeasureDrift;

internal static class Program
{
    private static int Main(string[] args)
    {
        var seconds = ParseIntFlag(args, "--seconds", fallback: 60);
        var useMag = args.Contains("--mag");

        Console.WriteLine($"# Rokid Max drift measurement — {seconds}s, magnetometer={(useMag ? "on" : "off")}");
        Console.WriteLine("# Leave the glasses motionless on a desk for the whole run.");

        using var device = HidSharpEnumerator.OpenFirst();
        Console.WriteLine($"# device: {device.ProductName}");

        var anchor = new SpatialAnchor(useMagnetometer: useMag);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Running accumulators for the raw gyro DC component.
        double gyroSumX = 0, gyroSumY = 0, gyroSumZ = 0;
        int gyroCount = 0;

        // Snapshot of the attitude at the end of the warm-up, used as the
        // drift reference. We take it once the filter is calibrated.
        Quaternion qRef = Quaternion.Identity;
        bool haveRef = false;
        double refTimeS = 0;

        // Yaw/roll/pitch at the reference point, for angle-difference maths.
        double refYaw = 0, refPitch = 0, refRoll = 0;

        var lastReport = 0.0;
        var samples = 0;

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (!device.TryRead(timeoutMs: 100, out var report)) continue;
            var parsed = PacketParser.Parse(report);
            if (!parsed.IsOk) continue;
            var sample = parsed.Value;
            samples++;

            // Raw gyro DC estimate — this is the bias the filter must remove.
            gyroSumX += sample.Gyro.X;
            gyroSumY += sample.Gyro.Y;
            gyroSumZ += sample.Gyro.Z;
            gyroCount++;

            anchor.Update(sample);
            var q = anchor.HeadOrientation;

            // Once calibration completes, latch the reference attitude.
            if (!haveRef && anchor.IsGyroCalibrated && anchor.SampleCount > 400)
            {
                qRef = q;
                var (r, p, y) = SyntheticImuSource.ToEulerZYX(q);
                refRoll = r; refPitch = p; refYaw = y;
                haveRef = true;
                refTimeS = sw.Elapsed.TotalSeconds;
                Console.WriteLine($"# reference latched at t={refTimeS:F1}s  " +
                                  $"roll={refRoll:F2} pitch={refPitch:F2} yaw={refYaw:F2}");
                Console.WriteLine("# t(s)  |gyro bias| (rad/s)   dYaw     dPitch   dRoll   |pos| (m)  resid  trust  tier");
            }

            if (haveRef)
            {
                var t = sw.Elapsed.TotalSeconds - refTimeS;
                if (t - lastReport >= 5.0)
                {
                    lastReport = t;
                    var (r, p, y) = SyntheticImuSource.ToEulerZYX(q);
                    var dYaw = NormalizeDeg(y - refYaw);
                    var dPitch = p - refPitch;
                    var dRoll = r - refRoll;
                    var biasMag = gyroCount > 0
                        ? new Vector3((float)(gyroSumX / gyroCount), (float)(gyroSumY / gyroCount), (float)(gyroSumZ / gyroCount)).Length()
                        : 0f;
                    var pos = anchor.HeadPosition;
                    var fusion = anchor.Fusion;
                    Console.WriteLine(
                        $"{t,7:F1}  {biasMag,8:F5}          {dYaw,8:F3}  {dPitch,8:F3}  {dRoll,8:F3}  {pos.Length(),8:F4}"
                      + $"   {fusion.LastResidualDegrees,6:F2}  {fusion.LastAccelTrust,4:F2}"
                      + $"  {(fusion.IsRecovering ? "REC" : fusion.IsSlowRealigning ? "slo" : "   ")}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"# total samples: {samples}  ({samples / seconds:F0} Hz)");
        if (gyroCount > 0)
        {
            Console.WriteLine($"# mean raw gyro bias (rad/s): " +
                              $"({gyroSumX / gyroCount:F5}, {gyroSumY / gyroCount:F5}, {gyroSumZ / gyroCount:F5})");
        }
        Console.WriteLine($"# final anchor state: {anchor.State}  " +
                          $"calibrated={anchor.IsGyroCalibrated}  stationary={anchor.IsStationary}");
        return 0;
    }

    private static double NormalizeDeg(double deg)
    {
        while (deg > 180.0) deg -= 360.0;
        while (deg <= -180.0) deg += 360.0;
        return deg;
    }

    private static int ParseIntFlag(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], out var v)) return v;
        }
        return fallback;
    }
}
