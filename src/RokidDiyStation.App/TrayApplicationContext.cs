// SPDX-License-Identifier: MIT
//
// Tray application context: owns the TaskbarIcon (H.NotifyIcon.Wpf),
// the context menu and the global hotkeys.  Constructed on the UI
// thread so the WPF infrastructure is up before we touch it.
using System.Windows;
using System.Windows.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace RokidDiyStation.App;

public sealed class TrayApplicationContext
{
    private readonly SettingsService _settingsService;
    private readonly TrackingService _tracking;
    private readonly AutostartService _autostart = new();
    private readonly TaskbarIcon _tray;
    private readonly MainWindow _mainWindow;
    private HotkeyBinding? _spaceHotkey;
    private HotkeyBinding? _rHotkey;
    private HotkeyBinding? _cHotkey;
    private bool _quitting;

    public TrayApplicationContext(SettingsService settingsService, TrackingService tracking)
    {
        _settingsService = settingsService;
        _tracking = tracking;
        _mainWindow = new MainWindow(_settingsService, _autostart, _tracking);

        _tray = new TaskbarIcon
        {
            ToolTipText = "Rokid DiyStation — spatial anchor",
            Icon = BuildTrayIcon(),
            Visibility = Visibility.Visible,
            NoLeftClickDelay = true,
            ContextMenu = BuildContextMenu(),
        };
        _tray.LeftClickCommand = new RelayCommand(_ => ShowMainWindow());

        _tracking.StatusChanged += OnStatusChanged;
        _tracking.Start();

        // The actual window show is deferred to OnApplicationStarted
        // so the WPF dispatcher is alive.  See Program.cs.
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Open settings…", Command = new RelayCommand(_ => ShowMainWindow()) });
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Set anchor here (Space)", Command = new RelayCommand(_ => _tracking.SetAnchorHere()) });
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Reset anchor (R)",       Command = new RelayCommand(_ => _tracking.ResetAnchor()) });
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
        if (_settingsService.Current.StartMinimised)
        {
            _mainWindow.Hide();
            _tray.ShowNotification(
                title: "Rokid DiyStation",
                message: "Running in the tray.  Press Space to set the anchor, or click the icon to open settings.",
                NotificationIcon.Info);
        }
        else
        {
            ShowMainWindow();
        }
        RegisterHotkeys();
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
        _tray.ToolTipText = s.State switch
        {
            TrackingState.Stopped   => "Rokid DiyStation — stopped",
            TrackingState.Searching => "Rokid DiyStation — searching…",
            TrackingState.Streaming => $"Rokid DiyStation — {s.SamplesPerSecond} Hz",
            TrackingState.Error     => $"Rokid DiyStation — error: {s.Message}",
            _                       => "Rokid DiyStation",
        };
        if (_mainWindow.IsVisible)
        {
            _mainWindow.UpdateStatus(s);
        }
    }

    public void ShowMainWindow()
    {
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private void RegisterHotkeys()
    {
        // Space -> set anchor, R -> reset, C -> recenter.
        _spaceHotkey = TryHotkey(1, NativeMethods.MOD_NONE,  0x20 /* VK_SPACE */);
        if (_spaceHotkey is not null) _spaceHotkey.Pressed += () => _tracking.SetAnchorHere();
        _rHotkey = TryHotkey(2, NativeMethods.MOD_NONE,  0x52 /* VK_R */);
        if (_rHotkey is not null)     _rHotkey.Pressed     += () => _tracking.ResetAnchor();
        _cHotkey = TryHotkey(3, NativeMethods.MOD_NONE,  0x43 /* VK_C */);
        if (_cHotkey is not null)     _cHotkey.Pressed     += () => _mainWindow.Recenter();
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

    public void OnExternalShowRequested(int hwnd) => ShowMainWindow();

    /// <summary>Build a small distinctive tray icon (16x16 cyan square
    /// with a darker border).  SystemIcons.Information is too pale and
    /// many users miss it on the taskbar; this stands out and
    /// requires no shipped .ico file.</summary>
    private static System.Drawing.Icon BuildTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(16, 16);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            using var border = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 0, 188, 212), 2);
            using var fill   = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 0, 150, 180));
            g.FillRectangle(fill, 2, 2, 12, 12);
            g.DrawRectangle(border, 1, 1, 13, 13);
            // tiny "R" mark in the middle
            using var text = new System.Drawing.Font(System.Drawing.FontFamily.GenericMonospace, 7, System.Drawing.FontStyle.Bold);
            using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            g.DrawString("R", text, white, 3, 3);
        }
        var hicon = bmp.GetHicon();
        return System.Drawing.Icon.FromHandle(hicon);
    }

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _tracking.Stop();
        try { _spaceHotkey?.Dispose(); } catch { }
        try { _rHotkey?.Dispose(); } catch { }
        try { _cHotkey?.Dispose(); } catch { }
        try { _settingsService.Save(); } catch { }
        _tray.Visibility = Visibility.Collapsed;
        // Mark the window so its OnClosing handler won't intercept.
        _mainWindow.MarkQuitting();
        _mainWindow.Close();
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
