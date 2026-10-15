// SPDX-License-Identifier: MIT
//
// Tray application context: owns the TaskbarIcon (H.NotifyIcon.Wpf),
// the context menu and the global hotkeys.  Constructed on the UI
// thread so the WPF infrastructure is up before we touch it.
using System.IO;
using System.Windows;
using System.Windows.Input;
using H.NotifyIcon;

namespace RokidDiyStation.App;

public sealed class TrayApplicationContext
{
    private readonly SettingsService _settingsService;
    private readonly TrackingService _tracking;
    private readonly AutostartService _autostart = new();
    private readonly TaskbarIcon _tray;
    private readonly MainWindow _mainWindow;
    private readonly NotifyIconHost _host;
    private HotkeyBinding? _spaceHotkey;
    private HotkeyBinding? _rHotkey;
    private HotkeyBinding? _cHotkey;
    private HotkeyBinding? _showHotkey;
    private bool _quitting;

    public TrayApplicationContext(SettingsService settingsService, TrackingService tracking)
    {
        _settingsService = settingsService;
        _tracking = tracking;
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
        _tracking.Start();

        // The actual window show is deferred to OnApplicationStarted
        // so the WPF dispatcher is alive.  See Program.cs.
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Show settings (Ctrl+Alt+R)", Command = new RelayCommand(_ => ShowMainWindow()) });
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Set anchor here (Ctrl+Space)", Command = new RelayCommand(_ => _tracking.SetAnchorHere()) });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Reset anchor (Ctrl+R)",       Command = new RelayCommand(_ => _tracking.ResetAnchor()) });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Recenter view (Ctrl+C)",      Command = new RelayCommand(_ => _mainWindow.Recenter()) });
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Quit", Command = new RelayCommand(_ => Quit()) });
        return menu;
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
            _mainWindow.AppendLog("Hotkey Ctrl+Space (set anchor) unavailable — another app may own it.");

        _rHotkey = TryHotkey(2, NativeMethods.MOD_CTRL, 0x52 /* VK_R */);
        if (_rHotkey is not null)
            _rHotkey.Pressed += () => _tracking.ResetAnchor();
        else
            _mainWindow.AppendLog("Hotkey Ctrl+R (reset anchor) unavailable.");

        _cHotkey = TryHotkey(3, NativeMethods.MOD_CTRL, 0x43 /* VK_C */);
        if (_cHotkey is not null)
            _cHotkey.Pressed += () => _mainWindow.Recenter();
        else
            _mainWindow.AppendLog("Hotkey Ctrl+C (recenter) unavailable.");

        // Show/hide window hotkey — Ctrl+Alt+R is a deliberately
        // awkward combo so we don't clash with common shortcuts.
        _showHotkey = TryHotkey(4, NativeMethods.MOD_CTRL | NativeMethods.MOD_ALT, 0x52 /* VK_R */);
        if (_showHotkey is not null)
            _showHotkey.Pressed += () => ToggleMainWindow();
        else
            _mainWindow.AppendLog("Hotkey Ctrl+Alt+R (show/hide window) unavailable.");
    }

    private HotkeyBinding? TryHotkey(int id, uint modifiers, uint vk)
    {
        try
        {
            var hk = new HotkeyBinding(id, modifiers, vk, _mainWindow);
            if (hk.Register()) return hk;
            hk.Dispose();
            return null;
        }
        catch
        {
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
        try { _spaceHotkey?.Dispose(); } catch { }
        try { _rHotkey?.Dispose(); } catch { }
        try { _cHotkey?.Dispose(); } catch { }
        try { _showHotkey?.Dispose(); } catch { }
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
