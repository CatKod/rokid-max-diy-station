// SPDX-License-Identifier: MIT
//
// Composition root for the Windows tray app.
//
//   * WPF Application owns the message pump
//   * SingleInstance mutex prevents a second copy
//   * SettingsService loads / persists JSON under %APPDATA%
//   * TrackingService runs the HID + UDP pipeline on a background thread
//   * TrayApplicationContext owns the H.NotifyIcon TaskbarIcon and the
//     global hotkeys (Space / R / C)
using System.Windows;
using RokidDiyStation.Core.HidCapture;
using System.IO;

namespace RokidDiyStation.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Helper: --discover / --reset / --minimised for shell wrappers
        if (args.Length > 0 && args[0] == "--discover")
        {
            foreach (var d in HidEnumerator.Discover())
            {
                Console.WriteLine($"{d.ProductName,-18} VID=0x{d.VendorId:X4}  PID=0x{d.ProductId:X4}");
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "--reset")
        {
            var path = SettingsService.DefaultPath();
            if (File.Exists(path)) File.Delete(path);
            Console.WriteLine($"Removed {path}");
            return 0;
        }

        var single = new SingleInstance();
        if (!single.IsOwner)
        {
            // Another copy is running; ask it to show its window and exit.
            SingleInstance.SignalShow();
            single.Dispose();
            return 0;
        }

        var settingsService = new SettingsService();
        settingsService.Load();
        var settings = settingsService.Current;
        if (args.Contains("--minimised"))
        {
            settings = settings with { StartMinimised = true };
            settingsService.Update(settings);
        }

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        var tracking = new TrackingService(settings, settingsService);
        var tray = new TrayApplicationContext(settingsService, tracking);
        single.OnShowRequested += _ => tray.OnExternalShowRequested(0);
        app.Exit += (_, _) => tray.Quit();

        // Defer the initial window show until the WPF dispatcher is
        // running.  Showing before Application.Run() starts the
        // dispatcher can leave the window without a message pump and
        // it never paints.
        app.Startup += (_, _) => tray.OnApplicationStarted();
        app.Activated += (_, _) => tray.OnApplicationActivated();

        // Run the message loop.  Quit() will call Application.Current.Shutdown().
        app.Run();
        return 0;
    }
}
