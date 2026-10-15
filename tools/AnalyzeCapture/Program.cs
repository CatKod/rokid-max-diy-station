// SPDX-License-Identifier: MIT
// Quick analyzer for raw IMU capture from Rokid Max.
//
// Usage: dotnet run --project tools/AnalyzeCapture -- docs/fixtures/raw-10s-capture.txt
using System.Globalization;
using System.Numerics;
using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.Protocol;

namespace RokidDiyStation.Tools.AnalyzeCapture;

internal static class Program
{
    private const double G = 9.80665; // m/s^2

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: analyze-capture <file>");
            return 2;
        }
        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"file not found: {path}");
            return 2;
        }

        var samples = LoadSamples(path).ToList();
        Console.WriteLine($"# loaded {samples.Count} samples from {path}");
        if (samples.Count < 10)
        {
            Console.Error.WriteLine("not enough samples");
            return 1;
        }

        var dt = AvgDeltaT(samples);
        var fs = 1.0 / dt;
        Console.WriteLine($"# avg dt = {dt * 1e6:F2} us  =>  fs = {fs:F2} Hz");

        ReportAccel(samples);
        ReportGyro(samples);
        ReportMag(samples);
        ReportWorn(samples);
        ReportMagnitude(samples);
        ReportGyrEnergy(samples);
        ReportMovements(samples);
        ReportFusion(samples);
        return 0;
    }

    private record Sample(
        long TsNs,
        double Ax, double Ay, double Az,
        double Gx, double Gy, double Gz,
        double Mx, double My, double Mz,
        bool Worn);

    private static IEnumerable<Sample> LoadSamples(string path)
    {
        var ci = CultureInfo.GetCultureInfo("de-DE"); // file uses comma decimal
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            if (line[0] == '#') continue;
            if (line[0] == '[') continue; // log header
            var cols = line.Split('\t');
            if (cols.Length < 11) continue;
            if (!long.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)) continue;
            yield return new Sample(
                ts,
                Parse(cols[1], ci), Parse(cols[2], ci), Parse(cols[3], ci),
                Parse(cols[4], ci), Parse(cols[5], ci), Parse(cols[6], ci),
                Parse(cols[7], ci), Parse(cols[8], ci), Parse(cols[9], ci),
                cols[10].Trim() == "1");
        }
    }

    private static double Parse(string s, CultureInfo ci) =>
        double.Parse(s, NumberStyles.Float, ci);

    private static double AvgDeltaT(List<Sample> s)
    {
        var sum = 0L;
        for (var i = 1; i < s.Count; i++) sum += s[i].TsNs - s[i - 1].TsNs;
        return (sum / (double)(s.Count - 1)) / 1e9;
    }

    private static void ReportAccel(List<Sample> s)
    {
        var ax = s.Select(x => x.Ax);
        var ay = s.Select(x => x.Ay);
        var az = s.Select(x => x.Az);
        Console.WriteLine();
        Console.WriteLine("# accel (m/s^2)");
        Console.WriteLine($"  x  min={Stat.Min(ax):F3}  max={Stat.Max(ax):F3}  mean={Stat.Mean(ax):F3}  std={Stat.Std(ax):F3}");
        Console.WriteLine($"  y  min={Stat.Min(ay):F3}  max={Stat.Max(ay):F3}  mean={Stat.Mean(ay):F3}  std={Stat.Std(ay):F3}");
        Console.WriteLine($"  z  min={Stat.Min(az):F3}  max={Stat.Max(az):F3}  mean={Stat.Mean(az):F3}  std={Stat.Std(az):F3}");
    }

    private static void ReportGyro(List<Sample> s)
    {
        var gx = s.Select(x => x.Gx);
        var gy = s.Select(x => x.Gy);
        var gz = s.Select(x => x.Gz);
        Console.WriteLine();
        Console.WriteLine("# gyro (rad/s)");
        Console.WriteLine($"  x  min={Stat.Min(gx):F4}  max={Stat.Max(gx):F4}  mean={Stat.Mean(gx):F4}  std={Stat.Std(gx):F4}");
        Console.WriteLine($"  y  min={Stat.Min(gy):F4}  max={Stat.Max(gy):F4}  mean={Stat.Mean(gy):F4}  std={Stat.Std(gy):F4}");
        Console.WriteLine($"  z  min={Stat.Min(gz):F4}  max={Stat.Max(gz):F4}  mean={Stat.Mean(gz):F4}  std={Stat.Std(gz):F4}");
    }

    private static void ReportMag(List<Sample> s)
    {
        var mx = s.Select(x => x.Mx);
        var my = s.Select(x => x.My);
        var mz = s.Select(x => x.Mz);
        Console.WriteLine();
        Console.WriteLine("# mag (µT)");
        Console.WriteLine($"  x  min={Stat.Min(mx):F2}  max={Stat.Max(mx):F2}  mean={Stat.Mean(mx):F2}");
        Console.WriteLine($"  y  min={Stat.Min(my):F2}  max={Stat.Max(my):F2}  mean={Stat.Mean(my):F2}");
        Console.WriteLine($"  z  min={Stat.Min(mz):F2}  max={Stat.Max(mz):F2}  mean={Stat.Mean(mz):F2}");
    }

    private static void ReportWorn(List<Sample> s)
    {
        var worn = s.Count(x => x.Worn);
        Console.WriteLine();
        Console.WriteLine($"# worn flag: {worn}/{s.Count} = {100.0 * worn / s.Count:F1}%");
    }

    private static void ReportMagnitude(List<Sample> s)
    {
        // |a| should be ~G when stationary or slow. Significant deviation => linear acceleration.
        var aMag = s.Select(x => Math.Sqrt(x.Ax * x.Ax + x.Ay * x.Ay + x.Az * x.Az));
        var mMag = s.Select(x => Math.Sqrt(x.Mx * x.Mx + x.My * x.My + x.Mz * x.Mz));
        Console.WriteLine();
        Console.WriteLine($"# |a|  mean={Stat.Mean(aMag):F3}  std={Stat.Std(aMag):F3}  min={Stat.Min(aMag):F3}  max={Stat.Max(aMag):F3}  (G={G:F3})");
        Console.WriteLine($"# |m|  mean={Stat.Mean(mMag):F2}  std={Stat.Std(mMag):F2}  min={Stat.Min(mMag):F2}  max={Stat.Max(mMag):F2}");
    }

    private static void ReportGyrEnergy(List<Sample> s)
    {
        // |w| in rad/s and deg/s. Head movement energy roughly = sum of |w|*dt.
        var wMag = s.Select(x => Math.Sqrt(x.Gx * x.Gx + x.Gy * x.Gy + x.Gz * x.Gz));
        var wMagDeg = wMag.Select(x => x * 180.0 / Math.PI);
        Console.WriteLine();
        Console.WriteLine($"# |w|  mean={Stat.Mean(wMag):F4} rad/s  max={Stat.Max(wMag):F4}  (= {Stat.Max(wMagDeg):F1} deg/s peak)");
    }

    private static void ReportMovements(List<Sample> s)
    {
        // Find segments where |w| > 0.1 rad/s (5.7 deg/s) and report windows.
        Console.WriteLine();
        Console.WriteLine("# movement segments (|w| > 0.1 rad/s):");
        var thresh = 0.1;
        int? startIdx = null;
        var last = -1;
        for (var i = 0; i < s.Count; i++)
        {
            var w = Math.Sqrt(s[i].Gx * s[i].Gx + s[i].Gy * s[i].Gy + s[i].Gz * s[i].Gz);
            var active = w > thresh;
            if (active && startIdx is null) startIdx = i;
            if ((!active || i == s.Count - 1) && startIdx is not null)
            {
                var endIdx = active ? i : i - 1;
                var win = s.GetRange(startIdx.Value, endIdx - startIdx.Value + 1);
                var peak = win.Max(x => Math.Sqrt(x.Gx * x.Gx + x.Gy * x.Gy + x.Gz * x.Gz));
                var t0 = win[0].TsNs / 1e6;
                var t1 = win[^1].TsNs / 1e6;
                // dominant axis
                var sumAbs = new (string Name, double Sum)[]
                {
                    ("gx", win.Sum(x => Math.Abs(x.Gx))),
                    ("gy", win.Sum(x => Math.Abs(x.Gy))),
                    ("gz", win.Sum(x => Math.Abs(x.Gz))),
                };
                var dom = sumAbs.OrderByDescending(t => t.Sum).First().Name;
                Console.WriteLine($"  t={t0:F0}..{t1:F0} ms  dur={t1 - t0:F0} ms  n={win.Count}  peak|w|={peak:F3} rad/s  axis={dom}");
                startIdx = null;
            }
            if (active) last = i;
        }
        if (startIdx is null && last < s.Count - 1)
        {
            // nothing
        }
    }

    private static void ReportFusion(List<Sample> s)
    {
        Console.WriteLine();
        Console.WriteLine("# Mahony fusion (no magnetometer) — orientation over time:");
        var f = new MahonyFusion();
        var ci = CultureInfo.InvariantCulture;
        int window = Math.Max(1, s.Count / 20); // 20 evenly-spaced samples
        for (var i = 0; i < s.Count; i++)
        {
            var sm = s[i];
            var sample = new ImuSample(
                (ulong)sm.TsNs,
                new Vector3((float)sm.Ax, (float)sm.Ay, (float)sm.Az),
                new Vector3((float)sm.Gx, (float)sm.Gy, (float)sm.Gz),
                new Vector3((float)sm.Mx, (float)sm.My, (float)sm.Mz),
                sm.Worn,
                0);
            f.Update(sample);
            if (i % window == 0 || i == s.Count - 1)
            {
                var q = f.Orientation;
                var (r, p, y) = SyntheticImuSource.ToEulerZYX(q);
                var tMs = sm.TsNs / 1e6;
                Console.WriteLine($"  t={tMs,7:F0} ms  roll={r,6:F1}°  pitch={p,6:F1}°  yaw={y,6:F1}°  q=({q.W:F3},{q.X:F3},{q.Y:F3},{q.Z:F3})");
            }
        }
        Console.WriteLine($"# consumed {f.SampleCount} samples");
    }

    private static class Stat
    {
        public static double Min(IEnumerable<double> v) => v.Min();
        public static double Max(IEnumerable<double> v) => v.Max();
        public static double Mean(IEnumerable<double> v) => v.Average();
        public static double Std(IEnumerable<double> v)
        {
            var arr = v.ToArray();
            var m = arr.Average();
            return Math.Sqrt(arr.Sum(x => (x - m) * (x - m)) / arr.Length);
        }
    }
}
