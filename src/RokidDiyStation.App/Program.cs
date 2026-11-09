// SPDX-License-Identifier: MIT
//
// Composition root for the Windows tray app.
//
//   * WPF Application owns the message pump
//   * SingleInstance mutex prevents a second copy
//   * StartupPreflight is the gate: the glasses have to be plugged in
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

        // "--preflight" runs ONLY the startup gate and reports the
        // result on the console.
        //
        // The gate's whole job is to decide whether the app runs, and
        // that decision has to be verifiable without a mouse click and
        // without a modal dialog blocking the script.  This flag is
        // that window: it exercises the real hardware probe and the
        // real virtual-display cleanup, prints the outcome, and
        // exits.
        if (args.Contains("--preflight"))
        {
            var probe = StartupPreflight.Evaluate(args.Contains("--no-device"));
            Console.WriteLine($"can_start={probe.CanStart}");
            Console.WriteLine($"title={probe.Title}");
            Console.WriteLine("message:");
            Console.WriteLine(probe.Message);
            Console.WriteLine("detail:");
            Console.WriteLine(probe.Detail);
            return probe.CanStart ? 0 : StartupPreflight.ExitCodeNoDevice;
        }

        var single = new SingleInstance(takeOver: args.Contains("--reload"));
        if (!single.IsOwner)
        {
            // Another copy is running; ask it to show its window and exit.
            SingleInstance.SignalShow();
            single.Dispose();
            return 0;
        }

        // THE STARTUP GATE.
        //
        // The Rokid Max has to be plugged in before the app starts, so
        // the glasses are checked HERE - before a single window, tray
        // icon or thread exists.  If they are not there, the virtual
        // display is taken off the desktop (a crashed previous session
        // can leave one behind), the user is told what to do, and the
        // app quits instead of pretending to work.
        //
        // The gate runs AFTER the single-instance check, so that a
        // second copy of a running app never puts a dialog in the
        // user's face - it just asks the running one to show itself.
        //
        // The gate owns the single-instance mutex from here on, so it
        // has to release it on the way out.
        //
        // "--no-device" forces the refusal branch, so the path can be
        // exercised on a machine where the glasses ARE plugged in.
        var preflight = StartupPreflight.Evaluate(args.Contains("--no-device"));
        if (!preflight.CanStart)
        {
            StartupPreflight.Log("Startup refused: the Rokid Max is not connected.");
            StartupPreflight.Log(preflight.Detail);

            StartupPreflight.ShowRefusal(preflight);

            single.ReleaseOwnership();
            single.Dispose();
            return StartupPreflight.ExitCodeNoDevice;
        }

        StartupPreflight.Log($"Startup preflight passed: {preflight.Detail}");

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

        // A reload can be requested from the outside - by a script, a
        // diagnostic tool, or a test - through the same pipe that
        // carries the "show the window" signal.  That is what makes
        // the reload verifiable: the hard part is the interaction
        // between two processes, and an interaction that can only be
        // triggered by a mouse click cannot be checked automatically.
        single.OnReloadRequested += () => tray.RequestReload();

        // The same reasoning applies to the quit: the app's EXIT path
        // has to take the virtual display off the desktop, and that
        // path can only be exercised if something can ask for it from
        // the outside.  A kill does not run it.
        //
        // It has to be marshalled onto the dispatcher, because the
        // pipe's listener is a background thread and Quit() closes
        // WPF windows - which may only be touched from the
        // dispatcher's thread.
        single.OnQuitRequested += () => tray.RequestQuit();

        // And the same for the "close the spatial display" command,
        // which is the user's actual complaint: closing the spatial
        // display has to take the virtual display off the desktop, and
        // that has to be checkable without a mouse click.
        single.OnCloseSpatialRequested += () => tray.RequestCloseSpatialDisplay();
        single.OnOpenSpatialRequested += () => tray.RequestOpenSpatialDisplay();

        // The single-instance mutex is released EXPLICITLY on the way
        // out, not left to the garbage collector.
        //
        // This matters for the reload: the replacement instance is
        // started before this one has finished exiting, so it finds
        // the mutex held and waits for it.  If the release happened at
        // an unpredictable moment - or never, because the finalizer
        // never runs before the process is killed - the replacement
        // would sit there until its deadline expired.
        app.Exit += (_, _) =>
        {
            tray.Quit();
            single.ReleaseOwnership();
        };

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
