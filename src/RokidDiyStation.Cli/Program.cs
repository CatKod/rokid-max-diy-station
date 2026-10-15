// SPDX-License-Identifier: MIT
//
// Headless testing tool for the Rokid Max DIY Station.
//
// Subcommands:
//   discover                  List every attached Rokid USB device.
//   stream [--timeout SECONDS] Read & print IMU samples live (Ctrl+C to stop).
//   parse <hex-bytes>         Decode a single hex-encoded packet and print the result.
//   fusion-emit [--replay FILE] [--host HOST] [--port N]
//                             Read IMU samples, run Mahony fusion, emit
//                             FreePIE-compatible orientation over UDP.

using RokidDiyStation.Core.Fusion;
using RokidDiyStation.Core.HidCapture;
using RokidDiyStation.Core.Output;
using RokidDiyStation.Core.Protocol;

namespace RokidDiyStation.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "discover"        => Discover(),
                "discover-sharp"  => DiscoverSharp(),
                "stream"          => Stream(args.AsSpan(1)),
                "stream-sharp"    => StreamSharp(args.AsSpan(1)),
                "parse"           => Parse(args.AsSpan(1)),
                "diagnose"        => Diagnose(),
                "diagnose-sharp"  => DiagnoseSharp(),
                "fusion-emit"     => FusionEmit(args.AsSpan(1)),
                "-h" or "--help" or "help" => PrintUsage(),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Discover()
    {
        var devices = HidEnumerator.Discover();
        if (devices.Count == 0)
        {
            Console.WriteLine("No Rokid devices found. Plug in the Rokid Max and retry.");
            return 2;
        }

        foreach (var d in devices)
        {
            Console.WriteLine($"{d.ProductName,-18} VID=0x{d.VendorId:X4}  PID=0x{d.ProductId:X4}  serial={d.SerialNumber}");
        }
        return 0;
    }

    private static int Stream(ReadOnlySpan<string> args)
    {
        var timeoutSec = ParseIntFlag(args, "timeout", fallback: 0);
        var count      = ParseIntFlag(args, "count", fallback: 0);

        using var cts = timeoutSec > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec)) : null;

        using var device = HidEnumerator.OpenFirst();
        var packetCount = 0;

        Console.WriteLine($"# streaming from {device.ProductName} (VID=0x{device.VendorId:X4} PID=0x{device.ProductId:X4})");
        Console.WriteLine("# ts_ns\taccel_x\taccel_y\taccel_z\tgyro_x\tgyro_y\tgyro_z\tmag_x\tmag_y\tmag_z\tworn");

        while (true)
        {
            if (cts?.IsCancellationRequested == true) break;
            if (count > 0 && packetCount >= count) break;

            if (!device.TryRead(timeoutMs: 100, out var report)) continue;

            var result = PacketParser.Parse(report);
            if (!result.IsOk) continue;

            var s = result.Value;
            Console.WriteLine(
                $"{s.TimestampNs}\t{s.Accel.X:F4}\t{s.Accel.Y:F4}\t{s.Accel.Z:F4}\t{s.Gyro.X:F5}\t{s.Gyro.Y:F5}\t{s.Gyro.Z:F5}\t{s.Mag.X:F2}\t{s.Mag.Y:F2}\t{s.Mag.Z:F2}\t{(s.Worn ? 1 : 0)}");
            packetCount++;
        }

        Console.WriteLine($"# {packetCount} packets");
        return 0;
    }

    private static int Parse(ReadOnlySpan<string> args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("parse: missing hex argument (e.g. \"11ba8ad9...\")");
            return 2;
        }

        var hex = string.Concat(args.ToArray());
        ReadOnlySpan<byte> bytes;
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"parse: invalid hex: {ex.Message}");
            return 2;
        }

        var result = PacketParser.Parse(bytes);
        if (!result.IsOk)
        {
            Console.WriteLine($"# status: {result.Status}");
            return 1;
        }

        var s = result.Value;
        Console.WriteLine($"status      : {result.Status}");
        Console.WriteLine($"timestamp_ns: {s.TimestampNs}");
        Console.WriteLine($"accel (m/s²): {s.Accel.X:F4} {s.Accel.Y:F4} {s.Accel.Z:F4}");
        Console.WriteLine($"gyro  (rad/s): {s.Gyro.X:F5} {s.Gyro.Y:F5} {s.Gyro.Z:F5}");
        Console.WriteLine($"mag   (µT)  : {s.Mag.X:F2} {s.Mag.Y:F2} {s.Mag.Z:F2}");
        Console.WriteLine($"worn        : {s.Worn}");
        Console.WriteLine($"host_ts_ns  : {s.HostTimestampNs}");
        return 0;
    }

    private static int ParseIntFlag(ReadOnlySpan<string> args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == $"--{name}" && int.TryParse(args[i + 1], out var value))
            {
                return value;
            }
        }
        return fallback;
    }

    private static int Diagnose()
    {
        var all = HidEnumerator.DiscoverAllInterfaces();
        Console.WriteLine($"# {all.Count} Rokid HID interface(s) detected:");
        foreach (var d in all)
        {
            var tail = d.DevicePath.Length > 80 ? d.DevicePath[^80..] : d.DevicePath;
            Console.WriteLine($"  PID=0x{d.ProductId:X4}  {d.ProductName,-20}  ...{tail}");
        }

        Console.WriteLine();
        Console.WriteLine("# attempting to open each one…");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hit = 0;
        foreach (var info in all)
        {
            if (cts.IsCancellationRequested) break;
            try
            {
                using var capture = HidEnumerator.Open(info);
                Console.WriteLine($"  OK    {capture.ProductName,-20}  handle=0x{Random.Shared.Next():X8}");
                hit++;
                break; // first success is enough for diagnose
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL  {info.ProductName,-20}  {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (hit == 0)
        {
            Console.WriteLine("# no interface opened. Common causes:");
            Console.WriteLine("#   - Rokid Station runtime is running and holds the IMU handle");
            Console.WriteLine("#   - Rokid driver owns the interface exclusively; stop its service");
            Console.WriteLine("#   - You need to run this binary as Administrator");
            return 2;
        }
        return 0;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("rokid-station-cli — headless testing tool");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  rokid-station-cli discover");
        Console.WriteLine("  rokid-station-cli diagnose");
        Console.WriteLine("  rokid-station-cli stream [--timeout SECONDS] [--count N]");
        Console.WriteLine("  rokid-station-cli parse <hex-bytes>");
        Console.WriteLine();
        Console.WriteLine("Experimental HidSharp backend (workaround for OpenDevice=false):");
        Console.WriteLine("  rokid-station-cli discover-sharp");
        Console.WriteLine("  rokid-station-cli diagnose-sharp");
        Console.WriteLine("  rokid-station-cli stream-sharp [--timeout SECONDS] [--count N]");
        Console.WriteLine();
        Console.WriteLine("Fusion + FreePIE UDP output (M4):");
        Console.WriteLine("  rokid-station-cli fusion-emit [--replay FILE] [--host HOST] [--port N]");
        Console.WriteLine("    --replay FILE  Read samples from a binary capture (45 B/sample) instead of the live device.");
        Console.WriteLine("    --host  HOST   Target host for FreePIE UDP packets (default 127.0.0.1).");
        Console.WriteLine("    --port  N      Target UDP port (default 5555).");
        return 0;
    }

    // ---------- HidSharp experimental backend ----------

    private static int DiscoverSharp()
    {
        var devices = HidSharpEnumerator.Discover();
        if (devices.Count == 0)
        {
            Console.WriteLine("[hidsharp] no Rokid devices found. Plug in the Rokid Max and retry.");
            return 2;
        }
        Console.WriteLine($"[hidsharp] {devices.Count} Rokid device(s) found:");
        foreach (var d in devices)
        {
            Console.WriteLine($"  {d.ProductName,-18} VID=0x{d.VendorId:X4}  PID=0x{d.ProductId:X4}  serial={d.SerialNumber}");
        }
        return 0;
    }

    private static int DiagnoseSharp()
    {
        var all = HidSharpEnumerator.DiscoverAllInterfaces();
        Console.WriteLine($"[hidsharp] {all.Count} Rokid HID interface(s) detected:");
        foreach (var d in all)
        {
            var tail = d.DevicePath.Length > 80 ? d.DevicePath[^80..] : d.DevicePath;
            Console.WriteLine($"  PID=0x{d.ProductId:X4}  {d.ProductName,-20}  ...{tail}");
        }

        Console.WriteLine();
        Console.WriteLine("[hidsharp] attempting to open each one…");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hit = 0;
        foreach (var info in all)
        {
            if (cts.IsCancellationRequested) break;
            try
            {
                using var capture = HidSharpEnumerator.Open(info);
                Console.WriteLine($"  OK    {capture.ProductName,-20}  handle opened");
                hit++;
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL  {info.ProductName,-20}  {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (hit == 0)
        {
            Console.WriteLine("[hidsharp] no interface opened. See docs/diag-hid-open-failure.md.");
            return 2;
        }
        return 0;
    }

    private static int StreamSharp(ReadOnlySpan<string> args)
    {
        var timeoutSec = ParseIntFlag(args, "timeout", fallback: 0);
        var count      = ParseIntFlag(args, "count", fallback: 0);

        using var cts = timeoutSec > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec)) : null;

        using var device = HidSharpEnumerator.OpenFirst();
        var packetCount = 0;

        Console.WriteLine($"[hidsharp] streaming from {device.ProductName} (VID=0x{device.VendorId:X4} PID=0x{device.ProductId:X4})");
        Console.WriteLine("# ts_ns\taccel_x\taccel_y\taccel_z\tgyro_x\tgyro_y\tgyro_z\tmag_x\tmag_y\tmag_z\tworn");

        while (true)
        {
            if (cts?.IsCancellationRequested == true) break;
            if (count > 0 && packetCount >= count) break;

            if (!device.TryRead(timeoutMs: 100, out var report)) continue;

            var result = PacketParser.Parse(report);
            if (!result.IsOk) continue;

            var s = result.Value;
            Console.WriteLine(
                $"{s.TimestampNs}\t{s.Accel.X:F4}\t{s.Accel.Y:F4}\t{s.Accel.Z:F4}\t{s.Gyro.X:F5}\t{s.Gyro.Y:F5}\t{s.Gyro.Z:F5}\t{s.Mag.X:F2}\t{s.Mag.Y:F2}\t{s.Mag.Z:F2}\t{(s.Worn ? 1 : 0)}");
            packetCount++;
        }

        Console.WriteLine($"# {packetCount} packets");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        Console.Error.WriteLine("run without arguments for usage.");
        return 2;
    }

    // ---------- M4 fusion + FreePIE UDP emit ----------

    private static int FusionEmit(ReadOnlySpan<string> args)
    {
        var replay = ParseStringFlag(args, "replay", fallback: null);
        var host   = ParseStringFlag(args, "host",   fallback: "127.0.0.1") ?? "127.0.0.1";
        var port   = ParseIntFlag(args, "port",    fallback: 5555);
        var timeoutSec = ParseIntFlag(args, "timeout", fallback: 0);

        var opts = new FreePieEmitterOptions(
            TargetHost: host,
            TargetPort: port,
            MaxPacketsPerSecond: 250);
        using var emitter = new FreePieEmitter(opts);
        var fusion = new MahonyFusion();
        using var cts = timeoutSec > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec)) : null;

        Console.WriteLine($"# fusion-emit -> {host}:{port}  replay={(replay ?? "<live>")}");

        if (replay is not null)
        {
            if (!File.Exists(replay))
            {
                Console.Error.WriteLine($"replay file not found: {replay}");
                return 2;
            }
            var bytes = File.ReadAllBytes(replay);
            const int stride = 45;
            var n = bytes.Length / stride;
            for (var i = 0; i < n; i++)
            {
                if (cts?.IsCancellationRequested == true) break;
                var off = i * stride;
                var sample = new ImuSample(
                    BitConverter.ToUInt64(bytes, off),
                    new System.Numerics.Vector3(
                        BitConverter.ToSingle(bytes, off + 8),
                        BitConverter.ToSingle(bytes, off + 12),
                        BitConverter.ToSingle(bytes, off + 16)),
                    new System.Numerics.Vector3(
                        BitConverter.ToSingle(bytes, off + 20),
                        BitConverter.ToSingle(bytes, off + 24),
                        BitConverter.ToSingle(bytes, off + 28)),
                    new System.Numerics.Vector3(
                        BitConverter.ToSingle(bytes, off + 32),
                        BitConverter.ToSingle(bytes, off + 36),
                        BitConverter.ToSingle(bytes, off + 40)),
                    bytes[off + 44] != 0,
                    0);
                fusion.Update(sample);
                emitter.Send(fusion.Orientation);
            }
            Console.WriteLine($"# sent {emitter.PacketsSent} packets, {emitter.PacketsFailed} failed");
            return 0;
        }

        // Live mode: open the device and stream.
        using var device = HidSharpEnumerator.OpenFirst();
        Console.WriteLine($"# streaming from {device.ProductName} (VID=0x{device.VendorId:X4} PID=0x{device.ProductId:X4})");
        var total = 0;
        while (true)
        {
            if (cts?.IsCancellationRequested == true) break;
            if (!device.TryRead(timeoutMs: 100, out var report)) continue;
            var result = PacketParser.Parse(report);
            if (!result.IsOk) continue;
            fusion.Update(result.Value);
            emitter.Send(fusion.Orientation);
            total++;
        }
        Console.WriteLine($"# sent {emitter.PacketsSent} packets ({emitter.PacketsFailed} failed) over {total} live samples");
        return 0;
    }

    private static string? ParseStringFlag(ReadOnlySpan<string> args, string name, string? fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == $"--{name}") return args[i + 1];
        }
        return fallback;
    }
}