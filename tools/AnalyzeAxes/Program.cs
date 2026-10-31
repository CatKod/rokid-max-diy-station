// Debug the fixture parse, then use the capture to pin down the Rokid
// device frame's sign conventions.
//
// The capture in docs/fixtures/raw-10s-capture.txt was taken while
// wearing the glasses and rotating the head left -> right -> up ->
// down (see docs/M3-FUSION.md).  That documented motion sequence is
// ground truth we can use to determine which SIGN each device axis
// uses.
//
// We need three facts:
//   * X = pitch axis, Y = yaw axis AND up, Z = roll axis  (known)
//   * is the device frame right-handed with X = right?
//   * which sign of each axis corresponds to which head motion?
using System.Globalization;
using System.Numerics;

static double P(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

var path = args.Length > 0 ? args[0] : "docs/fixtures/raw-10s-capture.txt";
var raw = new List<(ulong Ts, double[] A, double[] G, double[] M)>();
int skipped = 0;

foreach (var line in File.ReadLines(path))
{
    var t = line.TrimEnd('\r');
    if (t.StartsWith('#') || t.StartsWith('[') || t.Length == 0) { skipped++; continue; }
    var parts = t.Split('\t');
    if (parts.Length < 10) { skipped++; continue; }
    raw.Add((
        ulong.Parse(parts[0], CultureInfo.InvariantCulture),
        new[] { P(parts[1]), P(parts[2]), P(parts[3]) },
        new[] { P(parts[4]), P(parts[5]), P(parts[6]) },
        new[] { P(parts[7]), P(parts[8]), P(parts[9]) }));
}

Console.WriteLine($"rows={raw.Count}  skipped={skipped}");

// Sanity: the magnitudes must be physically plausible.
double maxG = 0, maxA = 0;
foreach (var r in raw)
{
    maxG = Math.Max(maxG, Math.Max(Math.Abs(r.G[0]), Math.Max(Math.Abs(r.G[1]), Math.Abs(r.G[2]))));
    maxA = Math.Max(maxA, Math.Max(Math.Abs(r.A[0]), Math.Max(Math.Abs(r.A[1]), Math.Abs(r.A[2]))));
}
Console.WriteLine($"max |gyro component| = {maxG:F4} rad/s   (a head turn peaks around 2-4)");
Console.WriteLine($"max |accel component| = {maxA:F3} m/s^2");
Console.WriteLine();

var durS = (raw[^1].Ts - raw[0].Ts) / 1e9;
Console.WriteLine($"duration = {durS:F3} s, rate = {raw.Count / durS:F1} Hz");
Console.WriteLine();

Console.WriteLine("=== Signed integrals over the whole capture ===");
double[] sum = new double[3];
double[] absSum = new double[3];
foreach (var r in raw)
{
    for (int i = 0; i < 3; i++) { sum[i] += r.G[i]; absSum[i] += Math.Abs(r.G[i]); }
}
for (int i = 0; i < 3; i++)
{
    var name = i switch { 0 => "X (pitch axis)", 1 => "Y (yaw axis / up)", _ => "Z (roll axis)" };
    Console.WriteLine($"  {name,-20} signed={sum[i],9:F4} rad   sum|g|={absSum[i],9:F4} rad");
}
Console.WriteLine();

// The captured motion is left -> right -> up -> down.  Find the window
// where each axis dominates and report the SIGN of the integral there.
Console.WriteLine("=== Signed integral in each 0.5 s window ===");
Console.WriteLine("  t(s)      sumX       sumY       sumZ     dominant(sign)");
var t0 = raw[0].Ts;
for (int i = 0; i < raw.Count; i += 219)
{
    var end = Math.Min(raw.Count, i + 219);
    double sx = 0, sy = 0, sz = 0;
    for (int j = i; j < end; j++) { sx += raw[j].G[0]; sy += raw[j].G[1]; sz += raw[j].G[2]; }
    var names = new[] { "X", "Y", "Z" };
    var vals = new[] { sx, sy, sz };
    var dom = 0;
    for (int k = 1; k < 3; k++) if (Math.Abs(vals[k]) > Math.Abs(vals[dom])) dom = k;
    Console.WriteLine($"  {(raw[i].Ts - t0) / 1e9,5:F1}  {sx,9:F4} {sy,9:F4} {sz,9:F4}   {names[dom]}({(vals[dom] > 0 ? "+" : "-")})");
}
Console.WriteLine();

Console.WriteLine("=== Accelerometer / magnetometer means ===");
double[] am = new double[3], mm = new double[3];
foreach (var r in raw)
{
    for (int i = 0; i < 3; i++) { am[i] += r.A[i]; mm[i] += r.M[i]; }
}
for (int i = 0; i < 3; i++) { am[i] /= raw.Count; mm[i] /= raw.Count; }
Console.WriteLine($"  accel mean = ({am[0]:F3}, {am[1]:F3}, {am[2]:F3})   |a| = {new Vector3((float)am[0], (float)am[1], (float)am[2]).Length():F3}");
Console.WriteLine($"  mag   mean = ({mm[0]:F2}, {mm[1]:F2}, {mm[2]:F2})   |m| = {new Vector3((float)mm[0], (float)mm[1], (float)mm[2]).Length():F2} uT");
Console.WriteLine();
Console.WriteLine("  Y carries gravity (up).  The mag's horizontal part must be in the");
Console.WriteLine("  X-Z plane; its sign relative to the accel's Z component tells us");
Console.WriteLine("  which way is forward.");

var magH = new Vector2((float)mm[0], (float)mm[2]);
Console.WriteLine($"  mag horizontal (X,Z) = ({magH.X:F2}, {magH.Y:F2}), angle = {Math.Atan2(magH.Y, magH.X) * 180 / Math.PI:F1} deg");
Console.WriteLine($"  accel (X,Z)          = ({am[0]:F3}, {am[2]:F3})");
