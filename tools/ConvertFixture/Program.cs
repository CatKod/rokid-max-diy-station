// SPDX-License-Identifier: MIT
// Convert text capture (tab-separated values, comma decimal) into
// binary .bin fixture for unit tests.
//
// Layout:  little-endian, no padding
//   int64  ts_ns                  8 bytes
//   float  accel_x (m/s^2)       12 bytes (x,y,z)
//   float  gyro_x  (rad/s)       12 bytes (x,y,z)
//   float  mag_x   (µT)          12 bytes (x,y,z)
//   byte   worn (0 or 1)          1 byte
//
// Total: 45 bytes per sample.
//
// Usage: convert-fixture <input.txt> <output.bin> [--max N]
using System.Globalization;
using System.Text;

namespace RokidDiyStation.Tools.ConvertFixture;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: convert-fixture <in.txt> <out.bin> [--max N]");
            return 2;
        }
        var input = args[0];
        var output = args[1];
        int max = 0;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--max" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n))
            {
                max = n;
                break;
            }
        }

        var ci = CultureInfo.GetCultureInfo("de-DE");
        using var fs = File.Create(output);
        using var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: false);
        var written = 0;
        foreach (var line in File.ReadLines(input))
        {
            if (max > 0 && written >= max) break;
            if (line.Length == 0) continue;
            if (line[0] == '#' || line[0] == '[') continue;
            var cols = line.Split('\t');
            if (cols.Length < 11) continue;
            try
            {
                var ts = long.Parse(cols[0], CultureInfo.InvariantCulture);
                bw.Write(ts);
                bw.Write(float.Parse(cols[1], NumberStyles.Float, ci));
                bw.Write(float.Parse(cols[2], NumberStyles.Float, ci));
                bw.Write(float.Parse(cols[3], NumberStyles.Float, ci));
                bw.Write(float.Parse(cols[4], NumberStyles.Float, ci));
                bw.Write(float.Parse(cols[5], NumberStyles.Float, ci));
                bw.Write(float.Parse(cols[6], NumberStyles.Float, ci));
                bw.Write(float.Parse(cols[7], NumberStyles.Float, ci));
                bw.Write(float.Parse(cols[8], NumberStyles.Float, ci));
                bw.Write(float.Parse(cols[9], NumberStyles.Float, ci));
                bw.Write((byte)(cols[10].Trim() == "1" ? 1 : 0));
                written++;
            }
            catch (FormatException ex)
            {
                Console.Error.WriteLine($"skip malformed line: {ex.Message}");
            }
        }
        Console.WriteLine($"wrote {written} samples to {output}");
        return 0;
    }
}
