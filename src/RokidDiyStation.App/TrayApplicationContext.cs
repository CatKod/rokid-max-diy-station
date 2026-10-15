// SPDX-License-Identifier: MIT
//
// Tray application context: owns the TaskbarIcon (H.NotifyIcon.Wpf),
// the context menu and the global hotkeys.  Constructed on the UI
// thread so the WPF infrastructure is up before we touch it.
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
            Icon = System.Drawing.SystemIcons.Information,
            Visibility = Visibility.Visible,
            NoLeftClickDelay = true,
            ContextMenu = BuildContextMenu(),
        };
        _tray.LeftClickCommand = new RelayCommand(_ => ShowMainWindow());

        _tracking.StatusChanged += OnStatusChanged;
        _tracking.Start();

        if (_settingsService.Current.StartMinimised)
        {
            _mainWindow.Hide();
        }
        else
        {
            ShowMainWindow();
        }
        _mainWindow.Loaded += (_, _) => RegisterHotkeys();
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
