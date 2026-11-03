// SPDX-License-Identifier: MIT
//
// Tray application context: owns the TaskbarIcon (H.NotifyIcon.Wpf),
// the context menu and the global hotkeys.  Constructed on the UI
// thread so the WPF infrastructure is up before we touch it.
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using H.NotifyIcon;
using RokidDiyStation.App.Spatial;

namespace RokidDiyStation.App;

public sealed class TrayApplicationContext
{
    private readonly SettingsService _settingsService;
    private readonly TrackingService _tracking;
    private readonly AutostartService _autostart = new();
    private readonly TaskbarIcon _tray;
    private readonly MainWindow _mainWindow;
    private readonly NotifyIconHost _host;
    private readonly SpatialStation _station;
    private HotkeyBinding? _spaceHotkey;
    private HotkeyBinding? _rHotkey;
    private HotkeyBinding? _cHotkey;
    private HotkeyBinding? _showHotkey;
    private HotkeyBinding? _pinHotkey;
    private bool _quitting;

    /// <summary>
    /// Translate the user-facing settings into the anchor service's
    /// options.
    /// </summary>
    /// <remarks>
    /// The two live in different layers on purpose: <see cref="AppSettings"/>
    /// is the persisted, user-editable surface, and
    /// <see cref="Core.Spatial.SpatialAnchorOptions"/> is the tuning
    /// surface of the spatial maths.  This is the only place that
    /// knows how to map one onto the other.
    /// </remarks>
    private static Core.Spatial.SpatialAnchorOptions BuildAnchorOptions(AppSettings s)
    {
        var fraction = ResolvePanelFraction(s);

        return new Core.Spatial.SpatialAnchorOptions
        {
            WorldLock = ParseWorldLock(s.WorldLockMode),
            // Clamped to the widest cone the display can actually show: a
            // wider cone cannot keep its promise of a fully visible panel.
            ComfortConeDegrees = (float)Math.Clamp(
                s.ComfortConeDegrees, 0.0,
                Core.Spatial.SpatialAnchorOptions.MaxUnclippedConeDegrees(fraction)),
            ComfortConeRatePerSecond = (float)Math.Clamp(s.ComfortConeRatePerSecond, 0.0, 10.0),
            NeckModelScale = (float)Math.Clamp(s.NeckModelScale, 0.0, 1.0),
            PanelDistance = (float)Math.Clamp(s.PanelDistanceMetres, 0.3, 10.0),
            PanelFovFraction = fraction,
        };
    }

    /// <summary>
    /// The panel's angular size, as a fraction of the field of view.
    /// </summary>
    /// <remarks>
    /// The settings file carries the panel's scale in two forms - a
    /// fraction and a percentage - and only one of them can be
    /// authoritative.  <see cref="Core.Spatial.PanelScale.Resolve"/>
    /// owns that rule, so the tray, the settings window and the
    /// persisted file can never disagree about how big the panel is.
    /// </remarks>
    public static float ResolvePanelFraction(AppSettings s)
        => Core.Spatial.PanelScale.Resolve(
            (float?)s.PanelFovFraction, s.PanelScalePercent);

    /// <summary>
    /// Map the persisted mode name onto the enum.
    /// </summary>
    /// <remarks>
    /// Anything unrecognised — including the value in an old
    /// settings.json written before this setting existed — resolves to
    /// <see cref="Core.Spatial.WorldLockMode.Strict"/>, which is the
    /// safe default: a panel that stays in the room.
    /// </remarks>
    private static Core.Spatial.WorldLockMode ParseWorldLock(string? mode) =>
        string.Equals(mode, "ComfortCone", StringComparison.OrdinalIgnoreCase)
            ? Core.Spatial.WorldLockMode.ComfortCone
            : Core.Spatial.WorldLockMode.Strict;

    public TrayApplicationContext(SettingsService settingsService, TrackingService tracking)
    {
        _settingsService = settingsService;
        _tracking = tracking;
        // The station shares the tracker's anchor, so the panel and the
        // head pose live in the same world frame.
        _station = new SpatialStation(BuildAnchorOptions(settingsService.Current), tracking.Anchor);

        // Apply the saved cursor size straight away, so the arrow is the
        // right size from the very first frame rather than jumping to
        // the user's chosen size when the settings window opens.
        _station.ApplyCursorScale(
            Core.Spatial.CursorSize.Clamp((float)settingsService.Current.CursorScale));

        _mainWindow = new MainWindow(_settingsService, _autostart, _tracking);
        // The host window is the WPF visual-tree parent for the TaskbarIcon.
        // It is created and never shown to the user (see NotifyIconHost).
        _host = new NotifyIconHost();

        _tray = new TaskbarIcon
        {
            ToolTipText = "Rokid DiyStation — spatial anchor",
            Icon = LoadTrayIcon(),
            Visibility = Visibility.Visible,
            NoLeftClickDelay = true,
            ContextMenu = BuildContextMenu(),
        };
        // Attach the TaskbarIcon to the host so it lives in the WPF visual
        // tree — without this the icon never registers with the Shell tray.
        _host.Content = _tray;
        _tray.LeftClickCommand = new RelayCommand(_ => ToggleMainWindow());

        _tracking.StatusChanged += OnStatusChanged;
        _tracking.OnLog += line => _mainWindow.AppendLog(line);

        // The station shares the tracker's anchor, so the panel and the
        // head pose live in the same world frame.
        _tracking.AttachStation(_station);
        _station.OnLog += line => _mainWindow.AppendLog(line);
        _mainWindow.AttachStation(_station);
        _station.OnStatus += (state, msg) =>
        {
            _tray.ToolTipText = state switch
            {
                SpatialState.Capturing => $"Rokid DiyStation — {_station.Anchor.Panel?.Width * 100:0} cm panel live",
                SpatialState.Searching => $"Rokid DiyStation — {msg}",
                SpatialState.Error     => $"Rokid DiyStation — {msg}",
                _                      => "Rokid DiyStation",
            };
        };

        _tracking.Start();
        // The spatial station is started in OnApplicationStarted, once
        // the WPF dispatcher is running.  Starting it here would let
        // the render thread call back into the UI (to log) before
        // there is a message pump to service the request.
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Show settings", Command = new RelayCommand(_ => ShowMainWindow()) });
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Open the spatial display", Command = new RelayCommand(_ => OpenSpatialDisplay()) });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Close the spatial display", Command = new RelayCommand(_ => CloseSpatialDisplay()) });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Bring the mouse back", Command = new RelayCommand(_ => SnapCursorHome()) });
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Pin panel here (Ctrl+Space)", Command = new RelayCommand(_ => _tracking.SetAnchorHere()) });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Forget the panel (Ctrl+R)",     Command = new RelayCommand(_ => _tracking.ResetAnchor()) });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Recenter view (Ctrl+C)",        Command = new RelayCommand(_ => _mainWindow.Recenter()) });
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Reload the app", Command = new RelayCommand(_ => Reload()) });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Quit", Command = new RelayCommand(_ => Quit()) });
        return menu;
    }

    /// <summary>
    /// Ask the app to reload, from outside.
    /// </summary>
    /// <remarks>
    /// The pipe listener calls this from a background thread, so the
    /// actual work is marshalled to the UI thread.
    /// </remarks>
    public void RequestReload()
    {
        if (_mainWindow.Dispatcher.CheckAccess()) Reload();
        else _mainWindow.Dispatcher.BeginInvoke(new Action(Reload));
    }

    /// <summary>
    /// Restart the app in place, without the user having to hunt for the
    /// exe and start it again.
    /// </summary>
    /// <remarks>
    /// The user's reason for wanting this is worth recording, because it
    /// explains why this is a RELOAD and not a bug fix: several small
    /// faults only clear when the app is cycled - the capture session
    /// can end up in a state where it never delivers a frame again, and
    /// the display path can be left in a state that a fresh attach
    /// resolves.  Rather than chase each of them, the user asked for
    /// one button that does what "quit and start again" does.
    ///
    /// HOW IT IS DONE
    /// --------------
    /// The app cannot simply re-launch itself and exit: the virtual
    /// display has to be RELEASED first, or the new instance finds a
    /// virtual display that is already attached and its own attach
    /// fails.
    ///
    /// So the order is:
    ///
    ///   1. save the settings, so nothing the user changed is lost;
    ///   2. tear the spatial station down - which closes the virtual
    ///      display and takes the mouse barrier off;
    ///   3. stop the tracker and release the hotkeys;
    ///   4. start a NEW instance of the same exe, which takes over
    ///      because the single-instance mutex has been released;
    ///   5. quit.
    ///
    /// The new instance's "show the window" signal is not used: the
    /// user asked for a reload, and a window popping up in their face
    /// unasked is the opposite of what they want.  The new instance
    /// starts minimised exactly as the current one did.
    ///
    /// If the relaunch fails for any reason, the app says so in the log
    /// and STAYS ALIVE, because an app that has torn itself down and
    /// cannot start again is worse than the fault the reload was meant
    /// to clear.
    /// </remarks>
    private void Reload()
    {
        _mainWindow.AppendLog("Reloading…");

        // 1. Persist whatever the user changed and never pressed "Save"
        //    for.
        try { _settingsService.Save(); }
        catch (Exception ex) { _mainWindow.AppendLog($"Could not save the settings: {ex.Message}"); }

        // 2. Release the virtual display and the mouse barrier BEFORE
        //    the new instance starts, or the new instance finds the
        //    display already attached and its own attach fails.
        try { _station.CloseSpatialDisplay(); }
        catch (Exception ex) { _mainWindow.AppendLog($"Could not close the spatial display: {ex.Message}"); }

        // 3. Stop the tracker and the hotkeys.
        try { _tracking.Stop(); }
        catch (Exception ex) { _mainWindow.AppendLog($"Could not stop the tracker: {ex.Message}"); }

        // 4. Start the new instance.  The single-instance mutex is
        //    released by the OLD instance's disposal, which happens in
        //    Quit() below - so the new instance has to be started
        //    AFTER that, or it will find the mutex still held, signal
        //    the old instance to show its window, and exit.
        //
        //    That ordering is why the relaunch is scheduled on the
        //    dispatcher rather than done inline: Quit() has to run
        //    first.
        _mainWindow.Dispatcher.BeginInvoke(new Action(() => StartReplacementAndQuit()));
    }

    /// <summary>
    /// Start a fresh copy of the app and then quit this one.
    /// </summary>
    /// <remarks>
    /// This runs on the dispatcher, AFTER <see cref="Quit"/> has
    /// released the single-instance mutex - otherwise the new instance
    /// would find the mutex held, ask this one to show its window, and
    /// exit, leaving the user with no app at all.
    /// </remarks>
    private void StartReplacementAndQuit()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe))
        {
            _mainWindow.AppendLog(
                "Reload failed: the running executable could not be located, so this "
                + "instance is staying alive. Quit and start the app again by hand.");
            return;
        }

        try
        {
            // The replacement starts minimised for the same reason the
            // original did: a reload is a repair action, and a window
            // appearing in the user's face is not what they asked for.
            //
            // The "--reload" flag tells the replacement that the
            // outgoing instance is EXPECTED to still be running, so it
            // waits for the single-instance mutex instead of concluding
            // that another copy is already running and exiting.
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppDomain.CurrentDomain.BaseDirectory,
            };
            psi.ArgumentList.Add("--minimised");
            psi.ArgumentList.Add("--reload");

            var started = System.Diagnostics.Process.Start(psi);
            if (started is null)
            {
                _mainWindow.AppendLog(
                    "Reload failed: the new instance could not be started, so this "
                    + "instance is staying alive.");
                return;
            }

            _mainWindow.AppendLog("A fresh instance is starting up.");
        }
        catch (Exception ex)
        {
            _mainWindow.AppendLog(
                $"Reload failed: {ex.Message}. This instance is staying alive.");
            return;
        }

        // 5. Quit this instance, which releases the mutex and lets the
        //    new instance claim it.
        Quit();
    }

    /// <summary>
    /// Attach the virtual display and start projecting it.
    /// </summary>
    /// <remarks>
    /// The virtual display IS the product now.  It is a real Windows
    /// monitor, so once it is up, every application in the system can
    /// be dragged onto it and interacted with there - there is no
    /// per-application launcher and no DevTools protocol.
    /// </remarks>
    private void OpenSpatialDisplay()
    {
        _mainWindow.AppendLog("Opening the spatial display…");
        if (!_station.OpenSpatialDisplay())
        {
            _mainWindow.AppendLog(
                "The virtual display is not available. Install the Virtual Display "
                + "Driver (VirtualDrivers.Virtual-Display-Driver) and try again.");
            return;
        }
        // Pin the panel where the user is looking, so the desktop
        // appears in front of them rather than wherever it was last.
        _station.AnchorHere();
    }

    private void CloseSpatialDisplay()
    {
        _mainWindow.AppendLog("Closing the spatial display…");
        _station.CloseSpatialDisplay();
    }

    /// <summary>
    /// Bring the OS cursor back onto the physical desktop.
    /// </summary>
    /// <remarks>
    /// The virtual display sits at NEGATIVE coordinates, so a cursor
    /// that wandered onto it has left the physical screen and the
    /// mouse feels lost.  This is the one-key recovery.
    /// </remarks>
    private void SnapCursorHome()
    {
        if (SpatialStation.SnapCursorHome())
            _mainWindow.AppendLog("The mouse is back on the main screen.");
        else
            _mainWindow.AppendLog("Could not find the primary display to move the mouse to.");
    }


    private bool _started;

    /// <summary>Called by Program once the WPF Application has finished
    /// starting.  This is the safe place to <see cref="Window.Show"/>
    /// a window: the dispatcher is running, message pump is alive.</summary>
    public void OnApplicationStarted()
    {
        if (_started) return;
        _started = true;
        RegisterHotkeys();

        // Now that the dispatcher is alive, bring up the spatial
        // station: it opens the render window on the glasses' display
        // and starts pumping frames.
        try { _station.Start(); }
        catch (Exception ex) { _mainWindow.AppendLog($"Spatial station failed to start: {ex.Message}"); }

        // The spatial display is the product now, so it comes up with
        // the app.  Attaching it is a plain ChangeDisplaySettingsEx
        // call and needs no administrator rights; if the driver is not
        // installed, the station keeps waiting and the user can retry
        // from the tray menu.
        try
        {
            if (_station.OpenSpatialDisplay())
            {
                _mainWindow.AppendLog(
                    "The spatial display is open. Drag any window onto it, or use "
                    + "Win+Shift+Left, and it appears in the glasses.");
            }
        }
        catch (Exception ex)
        {
            _mainWindow.AppendLog($"The spatial display could not be opened: {ex.Message}");
        }

        _mainWindow.AppendLog($"Diagnostic log mirrored to: {TrackingService.DiagnosticLogPath}");
        if (_settingsService.Current.StartMinimised)
        {
            // Notification balloons are not always supported (and
            // H.NotifyIcon throws if the TaskbarIcon is not yet
            // attached to a visible owner).  We log to the status
            // panel instead — the user can right-click the tray icon
            // for the menu.
            _mainWindow.AppendLog("Started minimised. Click the tray icon (cyan 'R') to open settings.");
            _mainWindow.Hide();
        }
        else
        {
            ShowMainWindow();
        }
    }

    /// <summary>Forwarded from Application.Activated — we use this to
    /// make sure the window is visible on first launch (the Startup
    /// event fires before the main window has a handle).</summary>
    public void OnApplicationActivated()
    {
        if (!_started) return;
        if (!_settingsService.Current.StartMinimised && !_mainWindow.IsVisible)
        {
            ShowMainWindow();
        }
    }

    private void OnStatusChanged(TrackingStatus s)
    {
        if (_tray is null) return;
        // StatusChanged is raised from the background tracking thread.
        // All WPF access (ToolTipText, the window's status panel,
        // AppendLog) must be marshalled to the UI thread.
        if (_mainWindow.Dispatcher.CheckAccess()) UpdateUi(s);
        else _mainWindow.Dispatcher.BeginInvoke(new Action(() => UpdateUi(s)));
    }

    private void UpdateUi(TrackingStatus s)
    {
        _tray.ToolTipText = s.State switch
        {
            TrackingState.Stopped   => "Rokid DiyStation — stopped",
            TrackingState.Searching => "Rokid DiyStation — searching…",
            TrackingState.Streaming => $"Rokid DiyStation — {s.SamplesPerSecond} Hz",
            TrackingState.Error     => $"Rokid DiyStation — error: {s.Message}",
            _                       => "Rokid DiyStation",
        };
        // Always update the window so the user sees live data even
        // when the window is hidden behind other apps.
        _mainWindow.UpdateStatus(s);
    }

    public void ShowMainWindow()
    {
        _mainWindow.Show();
        if (_mainWindow.WindowState == System.Windows.WindowState.Minimized)
            _mainWindow.WindowState = System.Windows.WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    public void ToggleMainWindow()
    {
        if (_mainWindow.IsVisible) _mainWindow.Hide();
        else ShowMainWindow();
    }

    private void RegisterHotkeys()
    {
        // Use a four-modifier combination so we don't steal Space/R/C
        // from normal typing, and so the shortcuts cannot collide with
        // another application's own hotkeys.
        //
        // WHY FOUR MODIFIERS, AND WHY NOT Fn
        // ----------------------------------
        // The user originally asked for Fn+<key>.  That is not
        // possible on a laptop, and the reason is worth recording
        // because it will come up again:
        //
        // The Fn key is not a key the operating system ever sees.  It
        // is handled by the laptop's EMBEDDED CONTROLLER (EC), a
        // microcontroller in the keyboard firmware.  On the ASUS TUF
        // (the machine this was developed on) pressing Fn alone
        // produces NO keyboard event at all - a low-level keyboard
        // hook sees nothing, and there is no VK_FN virtual-key code
        // for RegisterHotKey to use.  The EC only intercepts Fn+<key>
        // COMBINATIONS and turns them into the laptop's own functions
        // (brightness, volume, the numeric keypad, Airplane mode),
        // and it does so in firmware, BEFORE Windows sees the
        // keystroke.  Which combinations it intercepts is configured in
        // the BIOS, not in our app.
        //
        // Ctrl+Shift+Win+Alt+<key> is the most-modified combination
        // Windows offers that still produces a real key event, so it
        // is what the app uses.
        //
        // If registration fails (another app owns the hotkey) we log a
        // warning but the in-window buttons still work.
        var all = HotkeyNativeMethods.MOD_CTRL
                | HotkeyNativeMethods.MOD_SHIFT
                | HotkeyNativeMethods.MOD_ALT
                | HotkeyNativeMethods.MOD_WIN;

        // "Pin panel here" is the action the user reaches for most, so
        // it gets a list of candidates rather than a single one.
        //
        // Ctrl+Shift+Win+Alt+Space is the intended combination, but on
        // some machines it is already owned - measured on the ASUS TUF
        // this was developed on, it was taken, while the same
        // combination with C, R, H and G was free.  So the action
        // falls back through the alternates rather than silently
        // losing the shortcut.
        //
        // The alternates are FUNCTION keys, deliberately: the letter
        // combinations are spoken for by the other actions, and two
        // actions cannot share one combination.
        _spaceHotkey = TryHotkeyWithFallback(1, "pin panel here", new[]
        {
            (all, 0x20u, "Ctrl+Shift+Win+Alt+Space"),
            (all, 0x70u, "Ctrl+Shift+Win+Alt+F1"),
            (all, 0x71u, "Ctrl+Shift+Win+Alt+F2"),
        });
        if (_spaceHotkey is not null)
            _spaceHotkey.Pressed += () => _tracking.SetAnchorHere();
        else
            _mainWindow.AppendLog("No free hotkey found for \"pin panel here\" — use the tray menu.");

        _rHotkey = TryHotkeyWithFallback(2, "forget the panel", new[]
        {
            (all, 0x52u, "Ctrl+Shift+Win+Alt+R"),
            (all, 0x72u, "Ctrl+Shift+Win+Alt+F3"),
        });
        if (_rHotkey is not null)
            _rHotkey.Pressed += () => _tracking.ResetAnchor();
        else
            _mainWindow.AppendLog("No free hotkey found for \"forget the panel\" — use the tray menu.");

        _cHotkey = TryHotkeyWithFallback(3, "recenter", new[]
        {
            (all, 0x43u, "Ctrl+Shift+Win+Alt+C"),
            (all, 0x73u, "Ctrl+Shift+Win+Alt+F4"),
        });
        if (_cHotkey is not null)
            _cHotkey.Pressed += () => _mainWindow.Recenter();
        else
            _mainWindow.AppendLog("No free hotkey found for \"recenter\" — use the tray menu.");

        // Show/hide window hotkey.  The four-modifier combination is
        // the intended combo, but other apps can own it on some
        // machines, so fall back to a list of alternates rather than
        // silently losing the feature.
        _showHotkey = TryHotkeyWithFallback(4, "show/hide window", new[]
        {
            (all, 0x48u, "Ctrl+Shift+Win+Alt+H"),
            (all, 0x74u, "Ctrl+Shift+Win+Alt+F5"),
            (all, 0x53u, "Ctrl+Shift+Win+Alt+S"),
        });
        if (_showHotkey is not null)
            _showHotkey.Pressed += () => ToggleMainWindow();
        else
            _mainWindow.AppendLog("No free hotkey found for show/hide window — use the tray icon.");

        // Bring the mouse back to the physical screen.  The spatial
        // display sits at NEGATIVE coordinates, so a cursor that
        // wandered onto it has left the physical screen and the mouse
        // feels lost.  This is the recovery action, and it gets the
        // easiest-to-reach combo because it is the one users need
        // most often.
        _pinHotkey = TryHotkeyWithFallback(5, "bring the mouse back", new[]
        {
            (all, 0x50u, "Ctrl+Shift+Win+Alt+P"),
            (all, 0x47u, "Ctrl+Shift+Win+Alt+G"),
            (all | HotkeyNativeMethods.MOD_NOREPEAT, 0x75u, "Ctrl+Shift+Win+Alt+F6"),
        });
        if (_pinHotkey is not null)
            _pinHotkey.Pressed += () => SnapCursorHome();
        else
            _mainWindow.AppendLog("No free hotkey found for \"bring the mouse back\" — use the tray menu.");
    }

    /// <summary>
    /// Register the first hotkey combination in <paramref name="candidates"/>
    /// that no other application already owns.
    /// </summary>
    /// <remarks>
    /// RegisterHotKey fails with ERROR_HOTKEY_ALREADY_REGISTERED when
    /// another process holds the combination.  That is a property of
    /// the machine, not of our app, so the only sensible response is
    /// to try the next candidate.
    /// </remarks>
    private HotkeyBinding? TryHotkeyWithFallback(
        int id,
        string action,
        (uint Modifiers, uint Key, string Label)[] candidates)
    {
        foreach (var (modifiers, key, label) in candidates)
        {
            var hk = TryHotkey(id, modifiers, key);
            if (hk is not null)
            {
                _mainWindow.AppendLog($"Hotkey {label} bound to \"{action}\".");
                return hk;
            }
        }

        _mainWindow.AppendLog(
            $"Every candidate hotkey for \"{action}\" is owned by another app.");
        return null;
    }

    private HotkeyBinding? TryHotkey(int id, uint modifiers, uint vk)
    {
        try
        {
            var hk = new HotkeyBinding(id, modifiers, vk);
            if (hk.Register()) return hk;
            hk.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            _mainWindow.AppendLog($"Hotkey registration threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void OnExternalShowRequested(int hwnd)
    {
        // The pipe listener invokes this on a background thread.
        // Show() must run on the WPF UI thread, so marshal.
        if (_mainWindow.Dispatcher.CheckAccess()) ShowMainWindow();
        else _mainWindow.Dispatcher.BeginInvoke(new Action(ShowMainWindow));
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var icoPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "..", "obj", "app.ico");
        if (File.Exists(icoPath))
        {
            try { return new System.Drawing.Icon(icoPath); } catch { /* fall through */ }
        }
        // Fallback: build a programmatic icon (always works, no file needed).
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 0, 150, 180));
            using var ring = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 0, 188, 212), 3);
            g.FillEllipse(fill, 4, 4, 23, 23);
            g.DrawEllipse(ring, 1, 1, 29, 29);
            using var font = new System.Drawing.Font("Segoe UI", 14, System.Drawing.FontStyle.Bold);
            using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            var sf = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center,
            };
            g.DrawString("R", font, white, new System.Drawing.RectangleF(0, 0, 32, 32), sf);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _tracking.Stop();

        // The spatial display is closed on the way out, so the virtual
        // monitor does not stay on the desktop after the app is gone.
        //
        // This matters: a leftover virtual monitor is a trap.  A
        // window dragged onto it goes behind the glasses and is very
        // hard to get back - and the user would not even know the
        // app was the reason.
        try { _station.CloseSpatialDisplay(); }
        catch (Exception ex) { _mainWindow.AppendLog($"The spatial display could not be closed: {ex.Message}"); }

        _station.Dispose();
        try { _spaceHotkey?.Dispose(); } catch { }
        try { _rHotkey?.Dispose(); } catch { }
        try { _cHotkey?.Dispose(); } catch { }
        try { _showHotkey?.Dispose(); } catch { }
        try { _pinHotkey?.Dispose(); } catch { }
        try { _settingsService.Save(); } catch { }
        _tray.Visibility = Visibility.Collapsed;
        // Mark the window so its OnClosing handler won't intercept.
        _mainWindow.MarkQuitting();
        _mainWindow.Close();
        try { _host.Close(); } catch { }
        Application.Current.Shutdown();
    }
}

/// <summary>Minimal ICommand for the tray menu items.</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _action;
    public RelayCommand(Action<object?> action) => _action = action;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action(parameter);
}
