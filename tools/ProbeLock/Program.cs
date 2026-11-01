// SPDX-License-Identifier: MIT
//
// Probe: is the pinned panel WORLD-locked, or does it follow the head?
//
// This is the central product question, and it cannot be answered by a
// screenshot or by a coverage percentage.  A "3 DoF spatial anchor"
// that is secretly head-locked is just a screensaver.
//
// Method
// ------
// Anchor the panel looking straight ahead, then turn the head WITHOUT
// re-anchoring, and watch where the panel goes.
//
//   * World-locked: the panel stays in the room, so it slides towards
//     the opposite side of the view as the head turns.
//   * Head-locked: the panel stays dead centre.
//
// Three ways this probe can lie, and how it avoids them:
//
//  1. UNIT BUG.  SyntheticImuSource.FromEulerZYX takes RADIANS, but
//     ToEulerZYX returns DEGREES.  Mixing them up makes the head spin
//     by thousands of degrees.  Every angle here is explicit about
//     its unit.
//
//  2. LAZY FOLLOW IS TIME-BASED.  ApplyLazyFollow runs inside
//     GetView() and uses the wall-clock delta between calls.  A probe
//     that calls GetView() in a tight loop gets dt ~ 0, so the lazy
//     follow never moves and the result looks like a bug that is not
//     there.  This probe sleeps 16 ms between frames, like the real
//     render loop.
//
//  3. THE GYRO IS IN THE DEVICE FRAME.  With the device pitched, a
//     rotation about the device's own Y axis is NOT a world-vertical
//     yaw.  The gyro must be the world-vertical axis expressed in the
//     device frame:  g_device = R^-1 * (0, rate, 0).
//
// Also note the sign: in the Rokid device frame (+X right, +Y up,
// +Z back) a POSITIVE rotation about +Y swings the forward vector
// (-Z) towards -X, i.e. turns the head LEFT.  A negative rate is a
// turn to the RIGHT.
//
// Usage:
//   dotnet run --project tools/ProbeLock

using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.Protocol;
using RokidDiyStation.Core.Spatial;

internal static class Program
{
    private const float Gravity = 9.80665f;
    private const double Deg2Rad = Math.PI / 180.0;

    /// <summary>Sample rate of the Rokid Max: 437 Hz.</summary>
    private const double SampleHz = 437.0;

    /// <summary>Frame interval of the real render loop: 60 Hz.</summary>
    private const int FrameMs = 16;

    private static void Main()
    {
        Console.WriteLine("=== Panel behaviour as the head turns right (no re-anchor) ===");
        Console.WriteLine();
        Console.WriteLine("  ndc.x:  0 = centre, +/-1 = the edge of the glasses' field of view");
        Console.WriteLine("  A world-locked panel slides towards -ndc.x as the head turns right.");
        Console.WriteLine("  A head-locked panel would stay at ndc.x = 0.");
        Console.WriteLine();

        Probe("STRICT world lock (default)", WorldLockMode.Strict);
        Probe("comfort cone 15 deg (tag-along)", WorldLockMode.ComfortCone, deadzone: 15f);
        Probe("comfort cone 30 deg", WorldLockMode.ComfortCone, deadzone: 30f);
    }

    private static void Probe(string label, WorldLockMode mode, float deadzone = 0f)
    {
        Console.WriteLine($"--- {label} ---");

        var service = new SpatialAnchorService(new SpatialAnchorOptions
        {
            WorldLock = mode,
            ComfortConeDegrees = deadzone,
            ComfortConeRatePerSecond = 3f,
            PanelDistance = 1.5f,
            PanelFovFraction = 0.5f,
            NeckModelScale = 0f,
        });

        // Settle at roll 0, pitch -25 DEG, yaw 0.  FromEulerZYX wants
        // radians.
        var q0 = SyntheticImuSource.FromEulerZYX(0f, (float)(-25.0 * Deg2Rad), 0f);
        var accel = Vector3.Transform(new Vector3(0f, Gravity, 0f), Quaternion.Inverse(q0));

        ulong ts = 1_000_000_000UL;
        for (var i = 0; i < 4000; i++)
        {
            service.Update(new ImuSample(ts, accel, Vector3.Zero, Vector3.Zero, true, ts));
            ts += (ulong)(1e9 / SampleHz);
        }

        var (r0, p0, y0) = SyntheticImuSource.ToEulerZYX(service.Anchor.HeadOrientation);
        Console.WriteLine($"  settled: roll={r0:F2} pitch={p0:F2} yaw={y0:F2} DEG   (asked pitch=-25)");

        service.AnchorHere();
        Console.WriteLine($"  anchored.  {Report(service)}");

        // Turn right at 30 deg/s about the WORLD vertical axis.
        const double yawRateDeg = -30.0;   // negative = turn right
        var samplesPerFrame = (int)(SampleHz * FrameMs / 1000.0);

        for (var frame = 1; frame <= 60; frame++)   // 60 frames = 1 s
        {
            for (var k = 0; k < samplesPerFrame; k++)
            {
                var worldUpInDevice = Vector3.Transform(
                    Vector3.UnitY, Quaternion.Inverse(service.Anchor.HeadOrientation));
                var gyro = worldUpInDevice * (float)(yawRateDeg * Deg2Rad);

                service.Update(new ImuSample(ts, accel, gyro, Vector3.Zero, true, ts));
                ts += (ulong)(1e9 / SampleHz);
            }

            // The render loop calls GetView() once per frame.  Sleep so
            // the lazy follow sees a realistic dt.
            Thread.Sleep(FrameMs);
            service.GetView();

            var yawNow = SyntheticImuSource.ToEulerZYX(service.Anchor.HeadOrientation).Yaw;
            if (frame % 10 == 0)
            {
                Console.WriteLine($"  yaw {yawNow,6:F1} DEG -> {Report(service)}");
            }
        }
        Console.WriteLine();
    }

    private static string Report(SpatialAnchorService service)
    {
        var view = service.GetView();
        var mvp = view.View * service.GetProjection();

        var minX = float.MaxValue; var maxX = float.MinValue;
        var minY = float.MaxValue; var maxY = float.MinValue;

        foreach (var corner in view.PanelCorners)
        {
            var clip = Vector4.Transform(new Vector4(corner, 1f), mvp);
            var x = clip.X / clip.W;
            var y = clip.Y / clip.W;
            minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
            minY = MathF.Min(minY, y); maxY = MathF.Max(maxY, y);
        }

        var centreX = (minX + maxX) * 0.5f;
        var fullyVisible = minX >= -1f && maxX <= 1f && minY >= -1f && maxY <= 1f;
        var anyVisible = maxX >= -1f && minX <= 1f && maxY >= -1f && minY <= 1f;

        var state = fullyVisible ? "fully visible"
                  : anyVisible ? "PARTLY CLIPPED"
                  : "*** OFF SCREEN ***";

        return $"ndc.x centre {centreX,7:F3}  span [{minX,6:F2} .. {maxX,6:F2}]  {state}";
    }
}
