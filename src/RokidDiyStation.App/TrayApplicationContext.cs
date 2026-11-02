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
    private static Core.Spatial.SpatialAnchorOptions BuildAnchorOptions(AppSettings s) => new()
    {
        WorldLock = ParseWorldLock(s.WorldLockMode),
        // Clamped to the widest cone the display can actually show: a
        // wider cone cannot keep its promise of a fully visible panel.
        ComfortConeDegrees = (float)Math.Clamp(
            s.ComfortConeDegrees, 0.0,
            Core.Spatial.SpatialAnchorOptions.MaxUnclippedConeDegrees(
                (float)Math.Clamp(s.PanelFovFraction, 0.1, 1.0))),
        ComfortConeRatePerSecond = (float)Math.Clamp(s.ComfortConeRatePerSecond, 0.0, 10.0),
        NeckModelScale = (float)Math.Clamp(s.NeckModelScale, 0.0, 1.0),
        PanelDistance = (float)Math.Clamp(s.PanelDistanceMetres, 0.3, 10.0),
        PanelFovFraction = (float)Math.Clamp(s.PanelFovFraction, 0.1, 1.0),
    };

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
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Quit", Command = new RelayCommand(_ => Quit()) });
        return menu;
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
        // Use Ctrl+modifier so we don't steal Space/R/C from normal typing.
        // If registration fails (another app owns the hotkey) we log a
        // warning but the in-window buttons still work.
        _spaceHotkey = TryHotkey(1, HotkeyNativeMethods.MOD_CTRL, 0x20 /* VK_SPACE */);
        if (_spaceHotkey is not null)
            _spaceHotkey.Pressed += () => _tracking.SetAnchorHere();
        else
            _mainWindow.AppendLog("Hotkey Ctrl+Space (pin panel here) unavailable — another app may own it.");

        _rHotkey = TryHotkey(2, HotkeyNativeMethods.MOD_CTRL, 0x52 /* VK_R */);
        if (_rHotkey is not null)
            _rHotkey.Pressed += () => _tracking.ResetAnchor();
        else
            _mainWindow.AppendLog("Hotkey Ctrl+R (forget the panel) unavailable.");

        _cHotkey = TryHotkey(3, HotkeyNativeMethods.MOD_CTRL, 0x43 /* VK_C */);
        if (_cHotkey is not null)
            _cHotkey.Pressed += () => _mainWindow.Recenter();
        else
            _mainWindow.AppendLog("Hotkey Ctrl+C (recenter) unavailable.");

        // Show/hide window hotkey.  Ctrl+Alt+R is the intended combo,
        // but other apps own it on some machines, so fall back to a
        // list of alternates rather than silently losing the feature.
        _showHotkey = TryHotkeyWithFallback(4, "show/hide window", new[]
        {
            (HotkeyNativeMethods.MOD_CTRL | HotkeyNativeMethods.MOD_ALT, 0x52u, "Ctrl+Alt+R"),
            (HotkeyNativeMethods.MOD_CTRL | HotkeyNativeMethods.MOD_ALT, 0x48u, "Ctrl+Alt+H"),
            (HotkeyNativeMethods.MOD_CTRL | HotkeyNativeMethods.MOD_ALT, 0x53u, "Ctrl+Alt+S"),
            (HotkeyNativeMethods.MOD_CTRL | HotkeyNativeMethods.MOD_ALT, 0x4Fu, "Ctrl+Alt+O"),
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
            (HotkeyNativeMethods.MOD_CTRL | HotkeyNativeMethods.MOD_ALT, 0x50u, "Ctrl+Alt+P"),
            (HotkeyNativeMethods.MOD_CTRL | HotkeyNativeMethods.MOD_ALT | HotkeyNativeMethods.MOD_SHIFT, 0x50u, "Ctrl+Alt+Shift+P"),
            (HotkeyNativeMethods.MOD_CTRL | HotkeyNativeMethods.MOD_ALT, 0x47u, "Ctrl+Alt+G"),
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
