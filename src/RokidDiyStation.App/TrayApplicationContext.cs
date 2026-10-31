// SPDX-License-Identifier: MIT
//
// Tray application context: owns the TaskbarIcon (H.NotifyIcon.Wpf),
// the context menu and the global hotkeys.  Constructed on the UI
// thread so the WPF infrastructure is up before we touch it.
using System.IO;
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
        UseMagnetometer = s.UseMagnetometer,
        LazyFollowDeadzoneDegrees = (float)Math.Clamp(s.LazyFollowDeadzoneDegrees, 0.0, 45.0),
        LazyFollowRatePerSecond = (float)Math.Clamp(s.LazyFollowRatePerSecond, 0.0, 10.0),
        NeckModelScale = (float)Math.Clamp(s.NeckModelScale, 0.0, 1.0),
        PanelDistance = (float)Math.Clamp(s.PanelDistanceMetres, 0.3, 10.0),
        PanelFovFraction = (float)Math.Clamp(s.PanelFovFraction, 0.1, 1.0),
    };

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
        menu.Items.Add(BuildPinSubmenu());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Pin panel here (Ctrl+Space)", Command = new RelayCommand(_ => _tracking.SetAnchorHere()) });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Forget the panel (Ctrl+R)",     Command = new RelayCommand(_ => _tracking.ResetAnchor()) });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Recenter view (Ctrl+C)",        Command = new RelayCommand(_ => _mainWindow.Recenter()) });
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Quit", Command = new RelayCommand(_ => Quit()) });
        return menu;
    }

    /// <summary>
    /// The "Pin a window" submenu, listing the currently visible
    /// top-level windows so the user can choose one to project onto
    /// the glasses.
    /// </summary>
    /// <remarks>
    /// The list is rebuilt every time the menu opens, because windows
    /// come and go.  It is built on the UI thread, which is where the
    /// menu lives.
    /// </remarks>
    private System.Windows.Controls.MenuItem BuildPinSubmenu()
    {
        var pin = new System.Windows.Controls.MenuItem { Header = "Pin a window…" };
        var refresh = new System.Windows.Controls.MenuItem
        {
            Header = "Refresh list",
            Command = new RelayCommand(_ => RefreshPinSubmenu(pin)),
        };

        void Populate()
        {
            pin.Items.Clear();
            pin.Items.Add(refresh);
            pin.Items.Add(new System.Windows.Controls.Separator());

            var windows = WindowPicker.List();
            if (windows.Count == 0)
            {
                pin.Items.Add(new System.Windows.Controls.MenuItem
                {
                    Header = "(no capturable windows found)",
                    IsEnabled = false,
                });
                return;
            }

            foreach (var w in windows.Take(30))
            {
                var entry = w;
                pin.Items.Add(new System.Windows.Controls.MenuItem
                {
                    Header = entry.ToString(),
                    Command = new RelayCommand(_ => PinWindow(entry.Handle)),
                });
            }
        }

        pin.SubmenuOpened += (_, _) => Populate();
        Populate();
        return pin;
    }

    private void RefreshPinSubmenu(System.Windows.Controls.MenuItem pin)
    {
        var parent = pin.Parent as System.Windows.Controls.ItemsControl;
        var idx = parent?.Items.IndexOf(pin) ?? -1;
        if (parent is null || idx < 0) return;
        var replacement = BuildPinSubmenu();
        parent.Items.RemoveAt(idx);
        parent.Items.Insert(idx, replacement);
    }

    private void PinWindow(IntPtr hwnd)
    {
        _mainWindow.AppendLog($"Pinning window 0x{hwnd.ToInt64():X}.");
        _station.QueueCapture(hwnd);
        // Pin the panel where the user is looking RIGHT NOW, before any
        // drift has accumulated.  The capture takes a moment to start,
        // so the panel is placed at the pose the user had when they
        // pressed the hotkey.
        _station.AnchorHere();
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
        _spaceHotkey = TryHotkey(1, NativeMethods.MOD_CTRL, 0x20 /* VK_SPACE */);
        if (_spaceHotkey is not null)
            _spaceHotkey.Pressed += () => _tracking.SetAnchorHere();
        else
            _mainWindow.AppendLog("Hotkey Ctrl+Space (pin panel here) unavailable — another app may own it.");

        _rHotkey = TryHotkey(2, NativeMethods.MOD_CTRL, 0x52 /* VK_R */);
        if (_rHotkey is not null)
            _rHotkey.Pressed += () => _tracking.ResetAnchor();
        else
            _mainWindow.AppendLog("Hotkey Ctrl+R (forget the panel) unavailable.");

        _cHotkey = TryHotkey(3, NativeMethods.MOD_CTRL, 0x43 /* VK_C */);
        if (_cHotkey is not null)
            _cHotkey.Pressed += () => _mainWindow.Recenter();
        else
            _mainWindow.AppendLog("Hotkey Ctrl+C (recenter) unavailable.");

        // Show/hide window hotkey.  Ctrl+Alt+R is the intended combo,
        // but other apps own it on some machines, so fall back to a
        // list of alternates rather than silently losing the feature.
        _showHotkey = TryHotkeyWithFallback(4, "show/hide window", new[]
        {
            (NativeMethods.MOD_CTRL | NativeMethods.MOD_ALT, 0x52u, "Ctrl+Alt+R"),
            (NativeMethods.MOD_CTRL | NativeMethods.MOD_ALT, 0x48u, "Ctrl+Alt+H"),
            (NativeMethods.MOD_CTRL | NativeMethods.MOD_ALT, 0x53u, "Ctrl+Alt+S"),
            (NativeMethods.MOD_CTRL | NativeMethods.MOD_ALT, 0x4Fu, "Ctrl+Alt+O"),
        });
        if (_showHotkey is not null)
            _showHotkey.Pressed += () => ToggleMainWindow();
        else
            _mainWindow.AppendLog("No free hotkey found for show/hide window — use the tray icon.");

        // Pin the foreground window to the glasses.  This is the
        // primary interaction of the whole product, so it gets the
        // easiest-to-reach combo.
        _pinHotkey = TryHotkeyWithFallback(5, "pin foreground window", new[]
        {
            (NativeMethods.MOD_CTRL | NativeMethods.MOD_ALT, 0x50u, "Ctrl+Alt+P"),
            (NativeMethods.MOD_CTRL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT, 0x50u, "Ctrl+Alt+Shift+P"),
            (NativeMethods.MOD_CTRL | NativeMethods.MOD_ALT, 0x47u, "Ctrl+Alt+G"),
        });
        if (_pinHotkey is not null)
            _pinHotkey.Pressed += () => PinForegroundWindow();
        else
            _mainWindow.AppendLog("No free hotkey found for pin foreground window — use the tray menu.");
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

    /// <summary>
    /// Pin the window the user is currently looking at.
    /// </summary>
    /// <remarks>
    /// The foreground window is the one under the mouse cursor when the
    /// cursor is over a window, otherwise the one with keyboard focus.
    /// We prefer the cursor's window because the user is usually
    /// pointing at the thing they want to pin, while focus may still be
    /// on the app they launched it from.
    /// </remarks>
    private void PinForegroundWindow()
    {
        var hwnd = WindowPicker.GetCursorWindow();
        if (hwnd == IntPtr.Zero)
        {
            _mainWindow.AppendLog("No window under the cursor to pin.");
            return;
        }
        PinWindow(hwnd);
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
