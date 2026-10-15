// SPDX-License-Identifier: MIT
//
// Entry point for the Windows tray app.
//
// The M0/M1/M2 milestones do not yet ship the actual UI — that lands with M5.
// For now this exists to satisfy the `<OutputType>WinExe</OutputType>` build
// requirement and to give an early place to verify that the Core HID layer
// can be loaded end-to-end on a real device.

using RokidDiyStation.Core.HidCapture;

namespace RokidDiyStation.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Until M5 wires up H.NotifyIcon + the settings window, this is a
        // command-line-friendly launcher that delegates to the Core enumerator
        // so it stays useful for smoke tests.
        if (args.Length > 0 && args[0] == "--discover")
        {
            foreach (var d in HidEnumerator.Discover())
            {
                Console.WriteLine($"{d.ProductName,-18} VID=0x{d.VendorId:X4}  PID=0x{d.ProductId:X4}");
            }
            return 0;
        }

        Console.WriteLine("Rokid DiyStation — M5 tray UI is not yet wired up. Use `rokid-station-cli` for now.");
        Console.WriteLine("Tip: pass --discover to list attached Rokid devices.");
        return 0;
    }
}