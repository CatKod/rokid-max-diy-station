// SPDX-License-Identifier: MIT
using System.Windows;
using System.Windows.Controls;

namespace RokidDiyStation.App;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly AutostartService _autostart;
    private readonly TrackingService _tracking;
    private bool _firstClose = true;

    public MainWindow(SettingsService settingsService, AutostartService autostart, TrackingService tracking)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _autostart = autostart;
        _tracking = tracking;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BindFromSettings();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Save on close — the user might not have pressed "Save".
        try { _settingsService.Save(); } catch { }
        // First close: hide to tray and show a hint.  Quit() (from
        // the tray menu) bypasses this so the window really closes.
        if (_firstClose)
        {
            _firstClose = false;
            e.Cancel = true;
            Hide();
            AppendLog("Window hidden.  Click the tray icon (cyan 'R') to reopen, or right-click for the menu.");
        }
    }

    private void BindFromSettings()
    {
        var s = _settingsService.Current;
        HostBox.Text   = s.UdpHost;
        PortBox.Text   = s.UdpPort.ToString();
        RateSlider.Value = s.MaxPacketsPerSecond;
        SmoothSlider.Value = s.Smoothing;
        FovSlider.Value    = s.FovDegrees;
        FovLabel.Text      = $"{(int)s.FovDegrees}°";
        MagBox.IsChecked   = s.UseMagnetometer;
        RawPosBox.IsChecked = true;
        AutostartBox.IsChecked = s.Autostart;
        StartMinBox.IsChecked  = s.StartMinimised;
        AppendLog($"Settings loaded from {_settingsService.FilePath}");
    }

    public void UpdateStatus(TrackingStatus s)
    {
        StatusText.Text = s.State switch
        {
            TrackingState.Stopped   => "Stopped",
            TrackingState.Searching => "Searching for device…",
            TrackingState.Streaming => $"Streaming — {s.SamplesPerSecond} Hz",
            TrackingState.Error     => "Error",
            _                       => "Unknown",
        };
        DetailText.Text = s.State switch
        {
            TrackingState.Streaming =>
                $"cal={(s.GyroCalibrated ? "yes" : "no")} " +
                $"stationary={(s.IsStationary ? "yes" : "no")}  " +
                $"pos=({s.Position.X:F3}, {s.Position.Y:F3}, {s.Position.Z:F3}) m  " +
                $"eul=({s.Euler.RollDeg:F1}°, {s.Euler.PitchDeg:F1}°, {s.Euler.YawDeg:F1}°)",
            TrackingState.Error => s.Message,
            _ => s.Message,
        };
    }

    public void Recenter()
    {
        AppendLog("Recentred (placeholder — consumers re-derive the neutral orientation).");
    }

    private void OnSetAnchorClick(object sender, RoutedEventArgs e)
    {
        // The window has a _tracking reference; call it directly.
        _tracking.SetAnchorHere();
        AppendLog("Set anchor here.");
    }

    private void OnResetAnchorClick(object sender, RoutedEventArgs e)
    {
        _tracking.ResetAnchor();
        AppendLog("Anchor reset.");
    }

    private void OnRecenterClick(object sender, RoutedEventArgs e)
    {
        Recenter();
    }

    /// <summary>Tell the window that the next close is a real quit.
    /// Called by <see cref="TrayApplicationContext.Quit"/>.</summary>
    public void MarkQuitting() => _firstClose = false;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port) || port < 1 || port > 65535)
        {
            MessageBox.Show(this, "Port must be 1..65535", "Rokid DiyStation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var next = _settingsService.Current with
        {
            UdpHost = HostBox.Text.Trim(),
            UdpPort = port,
            MaxPacketsPerSecond = (int)RateSlider.Value,
            Smoothing = SmoothSlider.Value,
            FovDegrees = FovSlider.Value,
            UseMagnetometer = MagBox.IsChecked == true,
            Autostart = AutostartBox.IsChecked == true,
            StartMinimised = StartMinBox.IsChecked == true,
        };
        _settingsService.Update(next);
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                      ?? Environment.ProcessPath
                      ?? throw new InvalidOperationException("Cannot determine executable path");
            if (next.Autostart) _autostart.Enable(exe, next.StartMinimised);
            else                _autostart.Disable();
            AppendLog($"Saved. Autostart={(next.Autostart ? "on" : "off")}.");
        }
        catch (Exception ex)
        {
            AppendLog($"Autostart write failed: {ex.Message}");
        }
        _tracking.ApplySettings(next);
    }

    public void AppendLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(line));
            return;
        }
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        LogList.Items.Add($"[{stamp}] {line}");
        if (LogList.Items.Count > 200)
        {
            LogList.Items.RemoveAt(0);
        }
        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }
}
