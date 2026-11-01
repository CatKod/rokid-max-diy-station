// SPDX-License-Identifier: MIT
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RokidDiyStation.App;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly AutostartService _autostart;
    private readonly TrackingService _tracking;
    private Spatial.SpatialStation? _station;
    private bool _quitting;

    public MainWindow(SettingsService settingsService, AutostartService autostart, TrackingService tracking)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _autostart = autostart;
        _tracking = tracking;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>
    /// Attach the spatial station and wire its live readouts.
    /// </summary>
    public void AttachStation(Spatial.SpatialStation station)
    {
        _station = station;
        station.OnStatus += (_, msg) => Dispatcher.BeginInvoke(new Action(() =>
        {
            PanelText.Text = $"panel  {msg}";
        }));
        station.OnFrameRate += fps => Dispatcher.BeginInvoke(new Action(() =>
        {
            PanelText.Text = $"panel  {station.Message}  ({fps:0} fps)";
        }));
        RefreshWindowList();
    }

    /// <summary>Rebuild the list of capturable windows.</summary>
    public void RefreshWindowList()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RefreshWindowList)); return; }
        var selected = WindowPickerBox.SelectedItem as Spatial.WindowEntry;
        WindowPickerBox.Items.Clear();
        foreach (var w in Spatial.WindowPicker.List())
            WindowPickerBox.Items.Add(w);
        if (selected is not null && WindowPickerBox.Items.Contains(selected))
            WindowPickerBox.SelectedItem = selected;
    }

    /// <summary>
    /// Re-scan the window list every time the dropdown is opened.
    /// </summary>
    /// <remarks>
    /// This is the fix for "the app does not notice new windows".
    ///
    /// The list used to be built ONCE, when the settings window was
    /// created.  So a browser tab opened five minutes after launch was
    /// simply never in the list — the app could not pin it without a
    /// restart, which is exactly what the user reported.
    ///
    /// The scan happens on <see cref="ComboBox.DropDownOpened"/>
    /// rather than on a timer, for two reasons:
    ///
    ///   * It is exactly as fresh as it needs to be.  The user cannot
    ///     see a stale list, because the list is rebuilt in the same
    ///     interaction that shows it.
    ///
    ///   * It costs nothing when the user is not choosing a window.
    ///     Enumerating the windows is not free — it walks the shell's
    ///     window list — and doing it on a timer would burn CPU for the
    ///     whole time the app runs, in a background app whose entire
    ///     point is to stay out of the way.
    ///
    /// The tray menu's pin submenu does the same thing on
    /// <see cref="System.Windows.Controls.ContextMenu.Opened"/>.
    /// </remarks>
    private void OnWindowPickerDropDownOpened(object sender, EventArgs e)
    {
        RefreshWindowList();
    }

    private void OnRefreshWindowsClick(object sender, RoutedEventArgs e)
    {
        RefreshWindowList();
    }

    private void OnWindowSelected(object sender, SelectionChangedEventArgs e)
    {
        // Selecting a window is not the same as pinning it; the user
        // still has to press the button.  This keeps the accidental
        // click from hijacking the glasses.
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (_station is null) return;
        if (WindowPickerBox.SelectedItem is not Spatial.WindowEntry w) return;
        _station.QueueCapture(w.Handle);
        // Place the panel where the user is looking now — before the
        // capture has even produced its first frame.
        _station.AnchorHere();
        AppendLog($"Pinning '{w.Title}' (0x{w.Handle.ToInt64():X}).");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BindFromSettings();
        ComfortConeSlider.ValueChanged += (_, _) => UpdateSpatialLabels();
        PanelDistanceSlider.ValueChanged += (_, _) => UpdateSpatialLabels();
        NeckScaleSlider.ValueChanged += (_, _) => UpdateSpatialLabels();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Save on close — the user might not have pressed "Save".
        try { _settingsService.Save(); } catch { }
        // The window should never really close until Quit() is called
        // from the tray menu — otherwise re-opening via the single-
        // instance signal would try to Show() a disposed window and
        // fail silently (the user reported this as a 3rd-open crash).
        // Quit() sets _quitting via MarkQuitting() before closing so
        // we know it's safe to actually close.
        if (_quitting) return;
        e.Cancel = true;
        Hide();
        AppendLog("Window hidden.  Ctrl+Alt+R to reopen, or click the tray icon.");
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
        RawPosBox.IsChecked = true;
        AutostartBox.IsChecked = s.Autostart;
        StartMinBox.IsChecked  = s.StartMinimised;
        ComfortConeSlider.Maximum = MaxComfortConeDegrees();
        ComfortConeSlider.Value = Math.Min(s.ComfortConeDegrees, ComfortConeSlider.Maximum);
        PanelDistanceSlider.Value = s.PanelDistanceMetres;
        NeckScaleSlider.Value = s.NeckModelScale;

        // Set the radio button BEFORE wiring up the handler, or the
        // Checked event fires with the other button's state still
        // showing and the cone row is enabled or hidden wrongly.
        if (string.Equals(s.WorldLockMode, "ComfortCone", StringComparison.OrdinalIgnoreCase))
            ComfortConeRadio.IsChecked = true;
        else
            StrictWorldLockRadio.IsChecked = true;

        UpdateSpatialLabels();
        AppendLog($"Settings loaded from {_settingsService.FilePath}");
    }

    /// <summary>
    /// The widest comfort cone the display can actually show.
    /// </summary>
    /// <remarks>
    /// The slider's range is capped at this value, because a comfort
    /// cone wider than the display's margin cannot keep its promise:
    /// the panel would already be clipped by the bezel while the gaze
    /// was still inside the "rigid" cone.
    ///
    /// The core clamps the cone to the same limit, so the slider and
    /// the behaviour can never disagree.
    /// </remarks>
    private double MaxComfortConeDegrees()
    {
        var s = _settingsService.Current;
        return Core.Spatial.SpatialAnchorOptions.MaxUnclippedConeDegrees(
            (float)Math.Clamp(s.PanelFovFraction, 0.1, 1.0),
            Core.Spatial.RokidDisplayProfile.DiagonalFovDegrees);
    }

    /// <summary>Refresh the text readouts next to the spatial sliders.</summary>
    private void UpdateSpatialLabels()
    {
        ComfortConeValueLabel.Text = ComfortConeSlider.Value <= 0.01
            ? "off"
            : $"±{ComfortConeSlider.Value:0}°";
        PanelDistanceLabel.Text = $"{PanelDistanceSlider.Value:0.0} m";
        NeckScaleLabel.Text = NeckScaleSlider.Value <= 0.01
            ? "none"
            : $"{NeckScaleSlider.Value * 100:0}%";

        // The comfort cone only means anything in comfort-cone mode, so
        // the whole row is hidden in strict world-lock mode.  Hiding it
        // rather than disabling it makes it obvious that the setting is
        // not in effect at all.
        var comfortConeMode = ComfortConeRadio.IsChecked == true;
        ComfortConePanel.Visibility = comfortConeMode ? Visibility.Visible : Visibility.Collapsed;
        ComfortConeLabel.Visibility = comfortConeMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The user picked a different anchoring mode.
    /// </summary>
    /// <remarks>
    /// The change is applied LIVE, not only when "Save settings" is
    /// pressed, because the whole point of the setting is that the user
    /// can feel the difference immediately — turning the head and seeing
    /// whether the panel follows.
    /// </remarks>
    private void OnWorldLockModeChanged(object sender, RoutedEventArgs e)
    {
        UpdateSpatialLabels();
        ApplySpatialOptions();
        AppendLog(ComfortConeRadio.IsChecked == true
            ? "Anchoring: comfort cone — the panel will be dragged back towards your gaze when you look away."
            : "Anchoring: strict world-lock — the panel now stays exactly where you put it.");
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
                $"cal={(s.GyroCalibrated ? "yes" : "no ")}  " +
                $"stationary={(s.IsStationary ? "yes" : "no ")}",
            TrackingState.Error => s.Message,
            _ => s.Message,
        };
        // Position is shown in metres with 3 decimals (sub-mm precision
        // is well below the IMU noise floor, no point showing more).
        PosText.Text = $"pos  X={s.Position.X,7:F3}  Y={s.Position.Y,7:F3}  Z={s.Position.Z,7:F3} m";
        // Euler angles are wrapped into the canonical (-180, 180] range
        // so the user always sees a stable number when turning the head.
        EulText.Text = $"eul  R={WrapDeg(s.Euler.RollDeg),6:F1}°" +
                       $"  P={WrapDeg(s.Euler.PitchDeg),6:F1}°" +
                       $"  Y={WrapDeg(s.Euler.YawDeg),6:F1}°";
    }

    /// <summary>Wrap an angle in degrees to the canonical (-180, 180] range.</summary>
    private static double WrapDeg(double deg)
    {
        deg %= 360.0;
        if (deg >  180.0) deg -= 360.0;
        if (deg <= -180.0) deg += 360.0;
        return deg;
    }

    /// <summary>
    /// The "I am looking straight ahead now" action.
    /// </summary>
    /// <remarks>
    /// This moves the world origin to the current head pose and
    /// re-pins the spatial panel there.  It is the drift-recovery
    /// action: a gyroscope has no absolute heading, so the panel
    /// inevitably creeps away from where the user is looking, and this
    /// is the one-key way to put it back.
    ///
    /// The anchor mutation happens on the tracking thread; the panel
    /// re-pinning is handed to the station.
    /// </remarks>
    public void Recenter()
    {
        _tracking.Recenter();
        AppendLog("Recentred — the panel is now straight ahead of you.");
    }

    /// <summary>
    /// Pin the panel where the user is currently looking.
    /// </summary>
    /// <remarks>
    /// This re-pins the panel at the current gaze without disturbing
    /// the fusion's calibration or the world origin.  It is the fast
    /// "the panel drifted, put it back in front of me" action, and it
    /// is the one to reach for when the panel has crept off to the
    /// side but the head pose is still trustworthy.
    ///
    /// <see cref="Recenter"/> is the heavier action: it rebases the
    /// world origin as well, which is what you want after a long
    /// session's worth of yaw drift.
    /// </remarks>
    private void OnSetAnchorClick(object sender, RoutedEventArgs e)
    {
        // The window has a _tracking reference; call it directly.
        _tracking.SetAnchorHere();
        AppendLog("Panel pinned at your current gaze.");
    }

    private void OnResetAnchorClick(object sender, RoutedEventArgs e)
    {
        _tracking.ResetAnchor();
        AppendLog("Panel forgotten. Pin a window again to bring it back.");
    }

    private void OnRecenterClick(object sender, RoutedEventArgs e)
    {
        Recenter();
    }

    /// <summary>
    /// Push the current spatial settings into the running station.
    /// </summary>
    /// <remarks>
    /// Called both when the settings are saved and when the anchoring
    /// MODE is changed, because the mode is the one setting whose
    /// effect the user needs to feel immediately — they turn their head
    /// and watch whether the panel comes with them.
    /// </remarks>
    private void ApplySpatialOptions(AppSettings? settings = null)
    {
        var s = settings ?? _settingsService.Current with
        {
            WorldLockMode = ComfortConeRadio.IsChecked == true ? "ComfortCone" : "Strict",
            ComfortConeDegrees = ComfortConeSlider.Value,
            PanelDistanceMetres = PanelDistanceSlider.Value,
            NeckModelScale = NeckScaleSlider.Value,
        };

        var fovFraction = (float)Math.Clamp(s.PanelFovFraction, 0.1, 1.0);

        _station?.ApplyAnchorOptions(new Core.Spatial.SpatialAnchorOptions
        {
            WorldLock = ComfortConeRadio.IsChecked == true
                ? Core.Spatial.WorldLockMode.ComfortCone
                : Core.Spatial.WorldLockMode.Strict,
            // Clamped to what the display can actually show, so the
            // persisted value and the effective value agree.
            ComfortConeDegrees = (float)Math.Clamp(
                s.ComfortConeDegrees, 0.0,
                Core.Spatial.SpatialAnchorOptions.MaxUnclippedConeDegrees(fovFraction)),
            ComfortConeRatePerSecond = (float)Math.Clamp(s.ComfortConeRatePerSecond, 0.0, 10.0),
            NeckModelScale = (float)Math.Clamp(s.NeckModelScale, 0.0, 1.0),
            PanelDistance = (float)Math.Clamp(s.PanelDistanceMetres, 0.3, 10.0),
            PanelFovFraction = fovFraction,
        });
    }

    /// <summary>Tell the window that the next close is a real quit.
    /// Called by <see cref="TrayApplicationContext.Quit"/>.</summary>
    public void MarkQuitting() => _quitting = true;

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
            Autostart = AutostartBox.IsChecked == true,
            StartMinimised = StartMinBox.IsChecked == true,
            WorldLockMode = ComfortConeRadio.IsChecked == true ? "ComfortCone" : "Strict",
            ComfortConeDegrees = ComfortConeSlider.Value,
            PanelDistanceMetres = PanelDistanceSlider.Value,
            NeckModelScale = NeckScaleSlider.Value,
        };
        _settingsService.Update(next);
        // Apply the spatial options live, so the user sees the effect
        // of the anchoring mode / distance / neck-scale changes without
        // having to re-pin or restart.
        ApplySpatialOptions(next);
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
        // The station and the renderer log from background threads, and
        // they can fire before the dispatcher starts pumping.  A
        // blocking Invoke() would hang the calling thread in that
        // case, so we use a fire-and-forget BeginInvoke and mirror
        // every line to the log file regardless.
        WriteLogFile(line);

        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(new Action(() => AppendToList(line))); }
            catch { /* the dispatcher is shutting down */ }
            return;
        }
        AppendToList(line);
    }

    private void AppendToList(string line)
    {
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

    private static void WriteLogFile(string line)
    {
        try
        {
            var path = TrackingService.DiagnosticLogPath;
            var dir = Path.GetDirectoryName(path)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch { /* best-effort */ }
    }
}
