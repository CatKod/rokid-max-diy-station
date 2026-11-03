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
    private System.Windows.Threading.DispatcherTimer? _virtualDisplayTimer;
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

        // The virtual display's state is worth showing live: the most
        // common failure is that the driver is not installed, and the
        // user should be able to see that from the window.
        Dispatcher.BeginInvoke(new Action(RefreshVirtualDisplayText));
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        timer.Tick += (_, _) => RefreshVirtualDisplayText();
        timer.Start();
        _virtualDisplayTimer = timer;
    }

    /// <summary>Show the virtual display's resolution and position.</summary>
    private void RefreshVirtualDisplayText()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RefreshVirtualDisplayText)); return; }

        var monitor = Spatial.SpatialStation.FindVirtualDisplay();
        VirtualDisplayText.Text = monitor is null
            ? "not attached"
            : $"{monitor.Width}x{monitor.Height} at ({monitor.Bounds.Left},{monitor.Bounds.Top})";
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (_station is null) return;
        // The spatial display is a REAL Windows monitor, so there is no
        // single window to pin any more: the whole desktop is what is
        // projected, and the user drags whatever they want onto it.
        if (!_station.OpenSpatialDisplay())
        {
            AppendLog("The virtual display is not available. "
                    + "Install the Virtual Display Driver and try again.");
            return;
        }
        // Place the panel where the user is looking now — before the
        // capture has even produced its first frame.
        _station.AnchorHere();
        AppendLog("The spatial display is open — drag any window onto it.");
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
        // The virtual-display readout timer has to stop, or it keeps
        // waking the dispatcher after the window is hidden.
        _virtualDisplayTimer?.Stop();
        _virtualDisplayTimer = null;

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
        PanelDistanceSlider.Value = s.PanelDistanceMetres;
        NeckScaleSlider.Value = s.NeckModelScale;

        // The cursor size slider's range comes from the core, so the
        // slider can never offer a value the renderer would reject.
        CursorScaleSlider.Minimum = Core.Spatial.CursorSize.MinScale;
        CursorScaleSlider.Maximum = Core.Spatial.CursorSize.MaxScale;
        CursorScaleSlider.Value = Core.Spatial.CursorSize.Clamp((float)s.CursorScale);

        // The panel scale slider's range comes from the core too, for
        // the same reason.  The persisted value may be a fraction (from
        // an older settings file) or a percentage (from the slider);
        // the core resolves which one wins.
        PanelScaleSlider.Minimum = Core.Spatial.PanelScale.MinPercent;
        PanelScaleSlider.Maximum = Core.Spatial.PanelScale.MaxPercent;
        PanelScaleSlider.Value = Core.Spatial.PanelScale.ToPercent(
            TrayApplicationContext.ResolvePanelFraction(s));

        // The comfort cone's maximum depends on the panel's size, so
        // it can only be derived AFTER the panel scale is known.
        ComfortConeSlider.Maximum = MaxComfortConeDegrees();
        ComfortConeSlider.Value = Math.Min(s.ComfortConeDegrees, ComfortConeSlider.Maximum);

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
        // The cone's maximum depends on how much of the field of view
        // the panel covers: a bigger panel leaves less room for the
        // cone to move in before the panel's edge would be clipped.
        //
        // So the maximum has to follow the LIVE panel scale, not the
        // persisted one - otherwise the user could set a big panel and
        // a wide cone, and the panel would be clipped while the gaze
        // was still inside the "rigid" cone, which is exactly the
        // promise the comfort cone makes.
        var fraction = PanelScaleSlider is not null
            ? Core.Spatial.PanelScale.FromPercent(PanelScaleSlider.Value)
            : TrayApplicationContext.ResolvePanelFraction(_settingsService.Current);

        return Core.Spatial.SpatialAnchorOptions.MaxUnclippedConeDegrees(
            fraction,
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
        CursorScaleLabel.Text = $"{CursorScaleSlider.Value:0.00}×";
        PanelScaleLabel.Text = $"{PanelScaleSlider.Value:0}%";

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

    /// <summary>
    /// The user changed the cursor's size.
    /// </summary>
    /// <remarks>
    /// Applied LIVE, like the anchoring mode.  The only practical way
    /// to pick an arrow size is to see it: the slider moves and the
    /// arrow in the glasses grows or shrinks immediately.
    ///
    /// The value is persisted by "Save settings", which is the same
    /// button that persists everything else.
    /// </remarks>
    private void OnCursorScaleChanged(object sender, RoutedEventArgs e)
    {
        // This handler is wired in XAML, so it fires during XAML
        // parsing too: setting the slider's Minimum coerces its Value,
        // which raises ValueChanged, and at that moment the label and
        // the station do not exist yet.  Every access therefore has to
        // be guarded, or the app dies with a NullReferenceException
        // before it ever shows a window.
        if (CursorScaleSlider is null) return;

        var scale = Core.Spatial.CursorSize.Clamp((float)CursorScaleSlider.Value);

        // The label may not exist yet (during XAML parsing).
        if (CursorScaleLabel is not null)
            CursorScaleLabel.Text = $"{scale:0.00}×";

        // The station may not be attached yet.
        _station?.ApplyCursorScale(scale);
    }

    /// <summary>
    /// The user changed the panel's scale - how much of the glasses'
    /// field of view it covers.
    /// </summary>
    /// <remarks>
    /// Applied LIVE, like the anchoring mode.  The panel is re-sized
    /// IN PLACE by the anchor service, so it keeps its centre, its
    /// normal and its up axis: the screen stays where it is in the
    /// room and only changes size.  Re-pinning instead would move it
    /// to wherever the user happens to be looking, which is a
    /// surprise nobody asked for.
    ///
    /// The arrow's size follows automatically: it is measured against
    /// the panel's own width, so it keeps its size RELATIVE to the
    /// content no matter how the panel is scaled.
    /// </remarks>
    private void OnPanelScaleChanged(object sender, RoutedEventArgs e)
    {
        // This handler is wired in XAML, so it fires during XAML
        // parsing too, before the label and the station exist.  Every
        // access therefore has to be guarded.
        if (PanelScaleSlider is null) return;

        var fraction = Core.Spatial.PanelScale.FromPercent(PanelScaleSlider.Value);

        if (PanelScaleLabel is not null)
            PanelScaleLabel.Text = $"{Core.Spatial.PanelScale.ToPercent(fraction):0}%";

        // The comfort cone's maximum depends on the panel's size, so
        // it has to be re-derived and the current value re-clamped.
        // A cone that is too wide would break its own promise: the
        // panel would be clipped while the gaze was still inside the
        // "rigid" cone.
        //
        // The cone's slider may not exist yet (during XAML parsing),
        // so it has to be guarded like everything else here.
        if (ComfortConeSlider is not null)
        {
            var maxCone = MaxComfortConeDegrees();
            ComfortConeSlider.Maximum = maxCone;
            if (ComfortConeSlider.Value > maxCone)
                ComfortConeSlider.Value = maxCone;
        }

        // The station may not be attached yet.
        _station?.ApplyAnchorOptions(CurrentAnchorOptions(fraction));
    }

    /// <summary>
    /// The anchor options implied by the current state of the sliders.
    /// </summary>
    /// <remarks>
    /// This is the single place that turns the window's controls into
    /// the core's options, so the live path (a slider moved) and the
    /// save path ("Save settings" pressed) cannot disagree about what
    /// the panel's size should be.
    /// </remarks>
    /// <param name="panelFraction">
    /// The panel's angular size, as a fraction of the field of view.
    /// </param>
    private Core.Spatial.SpatialAnchorOptions CurrentAnchorOptions(float panelFraction)
    {
        var s = _settingsService.Current;
        var fovFraction = Core.Spatial.PanelScale.Clamp(panelFraction);

        return new Core.Spatial.SpatialAnchorOptions
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
        };
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

        // The panel's scale comes from the SLIDER when the window is
        // driving it, and from the persisted settings otherwise.  The
        // slider is the live control, so it wins when the two could
        // disagree.
        var fraction = PanelScaleSlider is not null
            ? Core.Spatial.PanelScale.FromPercent(PanelScaleSlider.Value)
            : TrayApplicationContext.ResolvePanelFraction(s);

        _station?.ApplyAnchorOptions(CurrentAnchorOptions(fraction));
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
        var panelFraction = Core.Spatial.PanelScale.FromPercent(PanelScaleSlider.Value);
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
            CursorScale = CursorScaleSlider.Value,
            // The panel's scale is persisted in BOTH forms: the
            // percentage is what the user chose on the slider, and the
            // fraction is what the maths actually uses.  Keeping them
            // in step is what lets the core resolve them without
            // guessing.
            PanelScalePercent = Core.Spatial.PanelScale.ToPercent(panelFraction),
            PanelFovFraction = panelFraction,
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
