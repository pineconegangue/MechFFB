using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MechFFBEngine;
using MechFFBEngine.Configuration;
using MechFFBEngine.Haptic;

namespace MechFFBUI.Views;

public partial class MainWindow : Window
{
    private FFBEngine? _ffbEngine;
    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _saveTimer; // Debounce timer for saving
    private bool _isDeviceSelected;
    private System.IO.StringWriter? _consoleWriter;
    private bool _isLoadingSettings = false; // Prevent saving while loading
    private bool _deviceReopenPending = false; // Exclusive-mode change waiting for FFB to stop
    private bool _lastTelemetryConnected = false; // Last connection state shown, so the status bar only updates on change

    // Log management
    private const int MAX_LOG_LINES = 250;
    private const int LOG_FLUSH_MS = 100;
    private readonly Queue<string> _logLines = new Queue<string>();
    // Any thread can log: lines are queued here and the UI picks them up in one batch every
    // LOG_FLUSH_MS, so the log TextBox is rebuilt at most ~10x/s instead of once per line.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLog = new();
    private DispatcherTimer? _logFlushTimer;

    public bool IsDeviceSelected
    {
        get => _isDeviceSelected;
        set
        {
            _isDeviceSelected = value;
            // Test and Calibrate need a device and must not run alongside the FFB engine
            bool canTest = value && !(_ffbEngine?.IsRunning ?? false);
            TestButton.IsEnabled = canTest;
            CalibrateButton.IsEnabled = canTest;
        }
    }

    public MainWindow()
    {
        InitializeComponent();

        // Redirect Console.WriteLine to our debug window
        _consoleWriter = new System.IO.StringWriter();
        // Background threads must never block on the UI thread to log (a blocking handoff
        // deadlocked Stop()/close). Writers just enqueue; FlushLog runs on a UI timer.
        Console.SetOut(new DebugWriter(text => _pendingLog.Enqueue(text)));
        _logFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LOG_FLUSH_MS) };
        _logFlushTimer.Tick += (s, e) => FlushLog();
        _logFlushTimer.Start();

        InitializeFFBEngine();
        SetupUpdateTimer();
        SetupSaveTimer();

        // Set window handle after window is created
        this.Loaded += (s, e) =>
        {
            if (_ffbEngine != null)
            {
                var windowInteropHelper = new System.Windows.Interop.WindowInteropHelper(this);
                if (windowInteropHelper.Handle != IntPtr.Zero)
                {
                    _ffbEngine.SetWindowHandle(windowInteropHelper.Handle);
                    Console.WriteLine("Window handle passed to FFB engine");

                    // A device auto-selected during startup was opened before the handle existed,
                    // i.e. non-exclusive. If exclusive mode is enabled, re-open it now.
                    if (_ffbEngine.Configuration.EnableExclusiveMode && IsDeviceSelected)
                        ReopenSelectedDevice();
                }
            }
        };
    }

    private void FlushLog()
    {
        if (_pendingLog.IsEmpty)
            return;

        // Take everything that arrived since the last tick
        while (_pendingLog.TryDequeue(out var text))
            _logLines.Enqueue(text);

        // Remove oldest lines if over limit
        while (_logLines.Count > MAX_LOG_LINES)
        {
            _logLines.Dequeue();
        }

        // Update TextBox with all lines
        DebugLogTextBox.Text = string.Join("", _logLines);

        // Always scroll to bottom to show latest logs
        DebugLogTextBox.ScrollToEnd();
    }

    private void VerboseLog_Changed(object sender, RoutedEventArgs e)
    {
        if (_ffbEngine == null || _isLoadingSettings)
            return;

        bool verbose = VerboseLogCheckBox.IsChecked ?? false;
        _ffbEngine.VerboseLogging = verbose;
        _ffbEngine.Configuration.VerboseLogging = verbose;
        DebouncedSave();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        DebugLogTextBox.Clear();
        _logLines.Clear();
        _pendingLog.Clear();
    }

    // Custom TextWriter that writes to both console and our debug window
    private class DebugWriter : System.IO.TextWriter
    {
        private readonly Action<string> _writeAction;

        public DebugWriter(Action<string> writeAction)
        {
            _writeAction = writeAction;
        }

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (value != null)
            {
                _writeAction($"[{DateTime.Now:HH:mm:ss.fff}] {value}\n");
                System.Diagnostics.Debug.WriteLine(value); // Also write to VS Output
            }
        }

        public override void Write(string? value)
        {
            if (value != null)
                _writeAction(value);
        }
    }

    private void InitializeFFBEngine()
    {
        try
        {
            _ffbEngine = new FFBEngine();

            // Subscribe to events
            _ffbEngine.OnStatusChanged += (s, msg) => Dispatcher.Invoke(() => UpdateStatus(msg));
            _ffbEngine.OnError += (s, msg) => Dispatcher.Invoke(() => ShowError(msg));

            // Initialize DirectInput
            if (_ffbEngine.Initialize())
            {
                LoadSlidersFromConfig(); // Load saved slider values + device options (before any device is opened)
                // Devices are listed (and a single device auto-selected) here, before the window
                // handle exists - so an auto-selected device opens non-exclusive by design.
                RefreshDeviceList();
            }
            else
            {
                ShowError("Failed to initialize DirectInput");
            }
        }
        catch (Exception ex)
        {
            ShowError($"Initialization error: {ex.Message}");
        }
    }

    private void LoadSlidersFromConfig()
    {
        if (_ffbEngine == null)
            return;

        // Prevent saving while we're loading - otherwise slider changes will overwrite the loaded config!
        _isLoadingSettings = true;

        var config = _ffbEngine.Configuration;

        if (config.VerboseLogging) Console.WriteLine("=== Loading Saved Settings ===");
        if (config.VerboseLogging) Console.WriteLine($"MasterIntensity from config: {config.MasterIntensity}");
        if (config.VerboseLogging) Console.WriteLine($"BallisticIntensity from config: {config.Simple.BallisticIntensity}");
        if (config.VerboseLogging) Console.WriteLine($"LaserIntensity from config: {config.Simple.LaserIntensity}");

        // Simple mode sliders
        MasterIntensitySlider.Value = config.MasterIntensity * 100;
        if (config.VerboseLogging) Console.WriteLine($"Set MasterIntensitySlider to: {MasterIntensitySlider.Value}");

        BallisticSlider.Value = config.Simple.BallisticIntensity * 100;
        if (config.VerboseLogging) Console.WriteLine($"Set BallisticSlider to: {BallisticSlider.Value}");

        LaserSlider.Value = config.Simple.LaserIntensity * 100;
        PPCSlider.Value = config.Simple.PPCIntensity * 100;
        MissileSlider.Value = config.Simple.MissileIntensity * 100;
        MachineGunSlider.Value = config.Simple.MachineGunIntensity * 100;
        MeleeSlider.Value = config.Simple.MeleeIntensity * 100;
        LaserMGDamageSlider.Value = config.Simple.LaserMGDamageIntensity * 100;
        ProjectileDamageSlider.Value = config.Simple.ProjectileDamageIntensity * 100;
        MissileDamageSlider.Value = config.Simple.MissileDamageIntensity * 100;
        MeleeDamageSlider.Value = config.Simple.MeleeDamageIntensity * 100;
        ExplosionDamageSlider.Value = config.Simple.ExplosionDamageIntensity * 100;
        LandingSlider.Value = config.Simple.LandingIntensity * 100;
        FootstepSlider.Value = config.Simple.FootstepIntensity * 100;
        JumpJetSlider.Value = config.Simple.JumpJetIntensity * 100;

        // Advanced mode sliders
        BallisticDurationSlider.Value = config.Advanced.Ballistics.Duration;
        BallisticAttackSlider.Value = config.Advanced.Ballistics.AttackTime;
        BallisticFadeSlider.Value = config.Advanced.Ballistics.FadeTime;
        // LaserDurationSlider removed - now uses F3 beam duration from game
        LaserFrequencySlider.Value = config.Advanced.Lasers.Frequency;
        LaserAttackSlider.Value = config.Advanced.Lasers.AttackTime;
        LaserFadeSlider.Value = config.Advanced.Lasers.FadeTime;
        MGDurationSlider.Value = config.Advanced.MachineGuns.Duration;
        MGFrequencySlider.Value = config.Advanced.MachineGuns.Frequency;
        PPCDurationSlider.Value = config.Advanced.PPCs.Duration;
        MissileDurationSlider.Value = config.Advanced.Missiles.Duration;
        MissileFrequencySlider.Value = config.Advanced.Missiles.Frequency;
        MissileRumbleMultiplierSlider.Value = config.Advanced.Missiles.RumbleMultiplier * 100;
        MissileAttackSlider.Value = config.Advanced.Missiles.AttackTime;
        MissileFadeSlider.Value = config.Advanced.Missiles.FadeTime;
        FootstepDurationSlider.Value = config.Advanced.Footsteps.Duration;
        JumpJetFrequencySlider.Value = config.Advanced.JumpJets.Frequency;
        LandingDurationSlider.Value = config.Advanced.Landing.Duration;
        LaserMGDmgFrequencySlider.Value = config.Advanced.LaserMGDamage.Frequency;
        LaserMGDmgAttackSlider.Value = config.Advanced.LaserMGDamage.AttackTime;
        LaserMGDmgFadeSlider.Value = config.Advanced.LaserMGDamage.FadeTime;
        ProjectileDmgDurationSlider.Value = config.Advanced.ProjectileDamage.Duration;
        ProjectileDmgAttackSlider.Value = config.Advanced.ProjectileDamage.AttackTime;
        ProjectileDmgFadeSlider.Value = config.Advanced.ProjectileDamage.FadeTime;
        MissileDmgDurationSlider.Value = config.Advanced.MissileDamage.Duration;
        MissileDmgAttackSlider.Value = config.Advanced.MissileDamage.AttackTime;
        MissileDmgFadeSlider.Value = config.Advanced.MissileDamage.FadeTime;
        MeleeDmgDurationSlider.Value = config.Advanced.MeleeDamage.Duration;
        MeleeDmgAttackSlider.Value = config.Advanced.MeleeDamage.AttackTime;
        MeleeDmgFadeSlider.Value = config.Advanced.MeleeDamage.FadeTime;
        ExplosionImpactMultiplierSlider.Value = config.Advanced.ExplosionDamageAdvanced.ImpactMultiplier * 100;
        ExplosionImpactDurationSlider.Value = config.Advanced.ExplosionDamageAdvanced.ImpactDuration;
        ExplosionImpactMultiplierSlider.Value = config.Advanced.ExplosionDamageAdvanced.ImpactMultiplier * 100;
        ExplosionImpactAttackSlider.Value = config.Advanced.ExplosionDamageAdvanced.ImpactAttackTime;
        ExplosionImpactFadeSlider.Value = config.Advanced.ExplosionDamageAdvanced.ImpactFadeTime;
        ExplosionRumbleDurationSlider.Value = config.Advanced.ExplosionDamageAdvanced.RumbleDuration;
        ExplosionRumbleFrequencySlider.Value = config.Advanced.ExplosionDamageAdvanced.RumbleFrequency;
        ExplosionRumbleMultiplierSlider.Value = config.Advanced.ExplosionDamageAdvanced.RumbleMultiplier * 100;
        ExplosionRumbleAttackSlider.Value = config.Advanced.ExplosionDamageAdvanced.RumbleAttackTime;
        ExplosionRumbleFadeSlider.Value = config.Advanced.ExplosionDamageAdvanced.RumbleFadeTime;

        // Device options - applied explicitly (setting IsChecked to its current value raises no event).
        var startupMapping = config.GetAxisMapping(null); // per-device mapping is applied on device selection
        _ffbEngine.SetAxisMapping(startupMapping);
        ShowAxisMapping(startupMapping);
        EnableExclusiveModeCheckBox.IsChecked = config.EnableExclusiveMode;
        _ffbEngine.SetEnableExclusiveMode(config.EnableExclusiveMode);
        VerboseLogCheckBox.IsChecked = config.VerboseLogging;
        _ffbEngine.VerboseLogging = config.VerboseLogging;

        Console.WriteLine("Loaded saved settings from configuration file");

        // Re-enable saving now that loading is complete
        _isLoadingSettings = false;

        // Verify sliders retained their values
        if (config.VerboseLogging) Console.WriteLine($"=== Verification After Load ===");
        if (config.VerboseLogging) Console.WriteLine($"MasterIntensitySlider.Value: {MasterIntensitySlider.Value}");
        if (config.VerboseLogging) Console.WriteLine($"BallisticSlider.Value: {BallisticSlider.Value}");
        if (config.VerboseLogging) Console.WriteLine($"LaserSlider.Value: {LaserSlider.Value}");
        if (config.VerboseLogging) Console.WriteLine($"Config MasterIntensity: {config.MasterIntensity}");
        if (config.VerboseLogging) Console.WriteLine($"Config BallisticIntensity: {config.Simple.BallisticIntensity}");
    }

    private void SetupUpdateTimer()
    {
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500) // Update UI twice per second
        };
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();
    }

    private void SetupSaveTimer()
    {
        // Save timer - delays save by 500ms to avoid rapid file writes
        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _saveTimer.Tick += (s, e) =>
        {
            _saveTimer?.Stop();
            if (_ffbEngine != null)
            {
                _ffbEngine.Configuration.Save();
                Console.WriteLine("Settings saved (debounced)");
            }
        };
    }

    private void DebouncedSave()
    {
        // Don't save if we're currently loading settings
        if (_isLoadingSettings)
            return;

        // Restart the timer - this delays the save until user stops adjusting
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_ffbEngine == null)
            return;

        bool running = _ffbEngine.IsRunning;
        bool connected = running && _ffbEngine.IsTelemetryConnected;

        // Indicator: red = stopped, orange = running but MW5 not detected, green = receiving telemetry
        if (!running)
        {
            TelemetryStatusIndicator.Fill = Brushes.Red;
            TelemetryStatusText.Text = "FFB Stopped";
        }
        else if (connected)
        {
            TelemetryStatusIndicator.Fill = Brushes.LimeGreen;
            TelemetryStatusText.Text = "FFB Active - connected to MW5";
        }
        else
        {
            TelemetryStatusIndicator.Fill = Brushes.Orange;
            TelemetryStatusText.Text = "FFB Active - waiting for MW5";
        }

        EventCountText.Text = _ffbEngine.EventsProcessed.ToString("N0");

        // Status bar follows connection changes while running (it used to stay on "Waiting..." forever)
        if (running && connected != _lastTelemetryConnected)
            UpdateStatus(connected ? "Connected to MechWarrior 5 - FFB active" : "Waiting for MechWarrior 5...");
        _lastTelemetryConnected = connected;
    }

    private void RefreshDeviceList()
    {
        if (_ffbEngine == null)
            return;

        try
        {
            DeviceComboBox.Items.Clear();

            var devices = _ffbEngine.GetAvailableDevices();

            if (devices.Count == 0)
            {
                UpdateStatus("No FFB devices found");
                ShowError("No FFB devices detected. Make sure your device is connected.");
                return;
            }

            foreach (var device in devices)
            {
                DeviceComboBox.Items.Add(device);
            }

            DeviceComboBox.DisplayMemberPath = "Name";
            UpdateStatus($"Found {devices.Count} FFB device(s)");

            // Prefer the device you used last time; otherwise auto-select when there's only one
            var savedGuid = _ffbEngine.Configuration.SelectedDeviceGuid;
            int savedIndex = savedGuid.HasValue ? devices.FindIndex(d => d.InstanceGuid == savedGuid.Value) : -1;
            if (savedIndex >= 0)
                DeviceComboBox.SelectedIndex = savedIndex;
            else if (devices.Count == 1)
                DeviceComboBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ShowError($"Error refreshing devices: {ex.Message}");
        }
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        // Refreshing re-opens the device, which must not happen under a running engine
        if (_ffbEngine?.IsRunning == true)
        {
            UpdateStatus("Stop FFB before refreshing devices");
            return;
        }

        RefreshDeviceList();
    }

    private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ffbEngine == null || DeviceComboBox.SelectedItem == null)
            return;

        var device = (DirectInputHapticManager.HapticDeviceInfo)DeviceComboBox.SelectedItem;

        try
        {
            if (_ffbEngine.SelectDevice(device.Index))
            {
                UpdateStatus($"Selected: {device.Name}");
                IsDeviceSelected = true;

                // Remember it for next launch
                _ffbEngine.Configuration.SelectedDeviceGuid = device.InstanceGuid;
                DebouncedSave();

                // Use this device's own axis mapping (or the default if it has none yet)
                var mapping = _ffbEngine.Configuration.GetAxisMapping(device.InstanceGuid);
                _ffbEngine.SetAxisMapping(mapping);
                ShowAxisMapping(mapping);
            }
            else
            {
                ShowError($"Failed to select device: {device.Name}");
                IsDeviceSelected = false;
            }
        }
        catch (Exception ex)
        {
            ShowError($"Device selection error: {ex.Message}");
            IsDeviceSelected = false;
        }
    }

    // ── Axis mapping (Flip X / Flip Y / calibration) ─────────────────────────

    private bool _updatingAxisUi; // true while code sets the Flip boxes, so their events are ignored

    private Guid? SelectedDeviceGuid =>
        DeviceComboBox.SelectedItem is DirectInputHapticManager.HapticDeviceInfo d ? d.InstanceGuid : (Guid?)null;

    private void AxisMapping_Changed(object sender, RoutedEventArgs e)
    {
        if (_ffbEngine == null || _isLoadingSettings || _updatingAxisUi)
            return;

        var current = _ffbEngine.Configuration.GetAxisMapping(SelectedDeviceGuid);
        StoreAxisMapping(new AxisMapping
        {
            FlipX = FlipXCheckBox.IsChecked == true,
            FlipY = FlipYCheckBox.IsChecked == true,
            SwapXY = current.SwapXY // only calibration changes the swap
        });
    }

    /// <summary>Save a mapping for the selected device (or as the default if none), apply it, show it.</summary>
    private void StoreAxisMapping(AxisMapping mapping)
    {
        if (_ffbEngine == null)
            return;

        var config = _ffbEngine.Configuration;
        if (SelectedDeviceGuid is Guid guid)
            config.DeviceAxisMappings[guid] = mapping;
        else
            config.DefaultAxisMapping = mapping;

        _ffbEngine.SetAxisMapping(mapping);
        ShowAxisMapping(mapping);
        DebouncedSave();
    }

    private void ShowAxisMapping(AxisMapping mapping)
    {
        _updatingAxisUi = true;
        FlipXCheckBox.IsChecked = mapping.FlipX;
        FlipYCheckBox.IsChecked = mapping.FlipY;
        SwapAxesText.Visibility = mapping.SwapXY ? Visibility.Visible : Visibility.Collapsed;
        _updatingAxisUi = false;
    }

    private void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        if (_ffbEngine == null || _ffbEngine.IsRunning
            || DeviceComboBox.SelectedItem is not DirectInputHapticManager.HapticDeviceInfo device)
            return;

        if (_ffbEngine.ForceAxisCount < 2)
        {
            MessageBox.Show(this, "This device has a single force axis, so there is no direction to calibrate.",
                "Calibrate axes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var awayFelt = AskWhichWay("Step 1 of 2", AxisMapping.CalibrationAwayAngle);
        if (awayFelt == null) return;
        var rightFelt = AskWhichWay("Step 2 of 2", AxisMapping.CalibrationRightAngle);
        if (rightFelt == null) return;

        var mapping = AxisMapping.FromCalibration(awayFelt.Value, rightFelt.Value);
        if (mapping == null)
        {
            MessageBox.Show(this,
                "Both pushes moved the stick along the same line, so the axes can't be worked out.\n" +
                "Check the right device is selected and try again.",
                "Calibrate axes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StoreAxisMapping(mapping);
        UpdateStatus($"Axes calibrated for {device.Name}: {mapping}");
        Console.WriteLine($"Calibration for {device.Name}: away push felt {awayFelt}, right push felt {rightFelt} -> {mapping}");
    }

    /// <summary>
    /// One calibration step: a small dialog with "Play push" and four answer buttons.
    /// Returns the direction the user felt, or null if they cancelled.
    /// </summary>
    private PushDirection? AskWhichWay(string step, int canonicalAngle)
    {
        PushDirection? answer = null;
        var dialog = new Window
        {
            Title = $"Calibrate axes - {step}",
            Owner = this,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource("PrimaryBrush") as Brush ?? Brushes.White
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        var text = new TextBlock
        {
            Text = $"{step}: hold the stick lightly near centre, click Play push, then choose which way the stick was pushed.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Margin = new Thickness(0, 0, 0, 12)
        };
        if (TryFindResource("TextBrush") is Brush textBrush) text.Foreground = textBrush;
        panel.Children.Add(text);

        var play = new Button { Content = "Play push", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(play);

        var answers = new WrapPanel();
        var answerButtons = new List<Button>();
        foreach (var (label, dir) in new[]
        {
            ("Away from me", PushDirection.AwayFromYou), ("Toward me", PushDirection.TowardYou),
            ("Left", PushDirection.Left), ("Right", PushDirection.Right)
        })
        {
            var btn = new Button { Content = label, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
            btn.Click += (_, _) => { answer = dir; dialog.Close(); };
            answerButtons.Add(btn);
            answers.Children.Add(btn);
        }
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(10, 4, 10, 4) };
        cancel.Click += (_, _) => dialog.Close();
        answers.Children.Add(cancel);
        panel.Children.Add(answers);

        play.Click += async (_, _) =>
        {
            play.IsEnabled = false;
            bool ok = await Task.Run(() => _ffbEngine!.PlayCalibrationPush(canonicalAngle));
            play.IsEnabled = true;
            play.Content = ok ? "Play again" : "Push failed - retry";
            if (!ok)
                text.Text = $"{step}: the device didn't play the push. Many devices only play force feedback in " +
                            "exclusive mode - close this, tick 'Enable Exclusive Mode', then calibrate again.";
            foreach (var btn in answerButtons) btn.IsEnabled = ok;
        };

        dialog.Content = panel;
        dialog.ShowDialog();
        return answer;
    }

    private void EnableExclusiveMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_ffbEngine == null || _isLoadingSettings)
            return;

        bool enable = EnableExclusiveModeCheckBox.IsChecked ?? false;
        _ffbEngine.SetEnableExclusiveMode(enable);

        // Save to config
        _ffbEngine.Configuration.EnableExclusiveMode = enable;
        DebouncedSave();

        // The cooperative level is set when the device is opened, so re-open it now -
        // or, if FFB is running, as soon as it is stopped.
        if (IsDeviceSelected)
        {
            if (_ffbEngine.IsRunning)
            {
                _deviceReopenPending = true;
                StatusText.Text = "Exclusive mode change will apply when you press Stop";
            }
            else
            {
                ReopenSelectedDevice();
            }
        }
    }

    /// <summary>
    /// Re-open the currently selected device so a changed cooperative level (exclusive mode) applies.
    /// </summary>
    private void ReopenSelectedDevice()
    {
        _deviceReopenPending = false;
        if (_ffbEngine == null || DeviceComboBox.SelectedItem is not DirectInputHapticManager.HapticDeviceInfo device)
            return;

        if (_ffbEngine.SelectDevice(device.Index))
            UpdateStatus($"Re-opened {device.Name} ({(_ffbEngine.Configuration.EnableExclusiveMode ? "exclusive" : "non-exclusive")})");
        else
            ShowError($"Failed to re-open device: {device.Name}");
    }

    private async void TestDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_ffbEngine == null || _ffbEngine.IsRunning)
            return;

        // The test plays for ~0.6 s - run it off the UI thread so the window stays responsive,
        // and lock out anything else that touches the device until it finishes.
        TestButton.IsEnabled = false;
        CalibrateButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        DeviceComboBox.IsEnabled = false;
        try
        {
            await Task.Run(() => _ffbEngine.TestDevice());
        }
        catch (Exception ex)
        {
            ShowError($"Test failed: {ex.Message}");
        }
        finally
        {
            StartButton.IsEnabled = true;
            DeviceComboBox.IsEnabled = true;
            IsDeviceSelected = IsDeviceSelected; // re-evaluates TestButton
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_ffbEngine == null)
            return;

        try
        {
            _ffbEngine.Start();

            StartButton.IsEnabled = false;
            TestButton.IsEnabled = false;
            CalibrateButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            DeviceComboBox.IsEnabled = false;

            UpdateStatus("FFB Engine Running - Waiting for MechWarrior 5...");
        }
        catch (Exception ex)
        {
            ShowError($"Failed to start: {ex.Message}");
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_ffbEngine == null)
            return;

        try
        {
            _ffbEngine.Stop();

            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            DeviceComboBox.IsEnabled = true;
            IsDeviceSelected = IsDeviceSelected; // re-enables Test if a device is selected

            UpdateStatus("FFB Engine Stopped");

            if (_deviceReopenPending)
                ReopenSelectedDevice();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to stop: {ex.Message}");
        }
    }

    private void IntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_ffbEngine == null || _isLoadingSettings)
            return;

        // Update configuration with individual weapon type intensities
        var config = _ffbEngine.Configuration;

        config.MasterIntensity = (float)(MasterIntensitySlider.Value / 100.0);

        // Weapon types
        config.Simple.BallisticIntensity = (float)(BallisticSlider.Value / 100.0);
        config.Simple.LaserIntensity = (float)(LaserSlider.Value / 100.0);
        config.Simple.PPCIntensity = (float)(PPCSlider.Value / 100.0);
        config.Simple.MissileIntensity = (float)(MissileSlider.Value / 100.0);
        config.Simple.MachineGunIntensity = (float)(MachineGunSlider.Value / 100.0);
        config.Simple.MeleeIntensity = (float)(MeleeSlider.Value / 100.0);

        // Incoming damage by type
        config.Simple.LaserMGDamageIntensity = (float)(LaserMGDamageSlider.Value / 100.0);
        config.Simple.ProjectileDamageIntensity = (float)(ProjectileDamageSlider.Value / 100.0);
        config.Simple.MissileDamageIntensity = (float)(MissileDamageSlider.Value / 100.0);
        config.Simple.MeleeDamageIntensity = (float)(MeleeDamageSlider.Value / 100.0);
        config.Simple.ExplosionDamageIntensity = (float)(ExplosionDamageSlider.Value / 100.0);

        // Impacts
        config.Simple.LandingIntensity = (float)(LandingSlider.Value / 100.0);

        // Movement
        config.Simple.FootstepIntensity = (float)(FootstepSlider.Value / 100.0);
        config.Simple.JumpJetIntensity = (float)(JumpJetSlider.Value / 100.0);

        // Debounced save - delays save by 500ms
        DebouncedSave();
    }

    private void AdvancedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_ffbEngine == null || _isLoadingSettings)
            return;

        // Update advanced configuration
        var config = _ffbEngine.Configuration;

        // Ballistics
        config.Advanced.Ballistics.Duration = (int)BallisticDurationSlider.Value;
        config.Advanced.Ballistics.AttackTime = (int)BallisticAttackSlider.Value;
        config.Advanced.Ballistics.FadeTime = (int)BallisticFadeSlider.Value;

        // Lasers (Duration removed - now uses F3 beam duration from game)
        config.Advanced.Lasers.Frequency = (int)LaserFrequencySlider.Value;
        config.Advanced.Lasers.AttackTime = (int)LaserAttackSlider.Value;
        config.Advanced.Lasers.FadeTime = (int)LaserFadeSlider.Value;

        // Machine Guns
        config.Advanced.MachineGuns.Duration = (int)MGDurationSlider.Value;
        config.Advanced.MachineGuns.Frequency = (int)MGFrequencySlider.Value;

        // PPCs
        config.Advanced.PPCs.Duration = (int)PPCDurationSlider.Value;

        // Missiles
        config.Advanced.Missiles.Duration = (int)MissileDurationSlider.Value;
        config.Advanced.Missiles.Frequency = (int)MissileFrequencySlider.Value;
        config.Advanced.Missiles.RumbleMultiplier = (float)(MissileRumbleMultiplierSlider.Value / 100.0);
        config.Advanced.Missiles.AttackTime = (int)MissileAttackSlider.Value;
        config.Advanced.Missiles.FadeTime = (int)MissileFadeSlider.Value;

        // Footsteps
        config.Advanced.Footsteps.Duration = (int)FootstepDurationSlider.Value;

        // Jump Jets
        config.Advanced.JumpJets.Frequency = (int)JumpJetFrequencySlider.Value;

        // Landing
        config.Advanced.Landing.Duration = (int)LandingDurationSlider.Value;

        // Damage effects (laser damage duration comes from the game)
        config.Advanced.LaserMGDamage.Frequency = (int)LaserMGDmgFrequencySlider.Value;
        config.Advanced.LaserMGDamage.AttackTime = (int)LaserMGDmgAttackSlider.Value;
        config.Advanced.LaserMGDamage.FadeTime = (int)LaserMGDmgFadeSlider.Value;

        config.Advanced.ProjectileDamage.Duration = (int)ProjectileDmgDurationSlider.Value;
        config.Advanced.ProjectileDamage.AttackTime = (int)ProjectileDmgAttackSlider.Value;
        config.Advanced.ProjectileDamage.FadeTime = (int)ProjectileDmgFadeSlider.Value;

        config.Advanced.MissileDamage.Duration = (int)MissileDmgDurationSlider.Value;
        config.Advanced.MissileDamage.AttackTime = (int)MissileDmgAttackSlider.Value;
        config.Advanced.MissileDamage.FadeTime = (int)MissileDmgFadeSlider.Value;

        config.Advanced.MeleeDamage.Duration = (int)MeleeDmgDurationSlider.Value;
        config.Advanced.MeleeDamage.AttackTime = (int)MeleeDmgAttackSlider.Value;
        config.Advanced.MeleeDamage.FadeTime = (int)MeleeDmgFadeSlider.Value;

        config.Advanced.ExplosionDamageAdvanced.ImpactMultiplier = (float)(ExplosionImpactMultiplierSlider.Value / 100.0);
        config.Advanced.ExplosionDamageAdvanced.ImpactDuration = (int)ExplosionImpactDurationSlider.Value;
        config.Advanced.ExplosionDamageAdvanced.ImpactMultiplier = (float)(ExplosionImpactMultiplierSlider.Value / 100.0);
        config.Advanced.ExplosionDamageAdvanced.ImpactAttackTime = (int)ExplosionImpactAttackSlider.Value;
        config.Advanced.ExplosionDamageAdvanced.ImpactFadeTime = (int)ExplosionImpactFadeSlider.Value;
        config.Advanced.ExplosionDamageAdvanced.RumbleDuration = (int)ExplosionRumbleDurationSlider.Value;
        config.Advanced.ExplosionDamageAdvanced.RumbleFrequency = (int)ExplosionRumbleFrequencySlider.Value;
        config.Advanced.ExplosionDamageAdvanced.RumbleMultiplier = (float)(ExplosionRumbleMultiplierSlider.Value / 100.0);
        config.Advanced.ExplosionDamageAdvanced.RumbleAttackTime = (int)ExplosionRumbleAttackSlider.Value;
        config.Advanced.ExplosionDamageAdvanced.RumbleFadeTime = (int)ExplosionRumbleFadeSlider.Value;

        // Debounced save - delays save by 500ms
        DebouncedSave();
    }

    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }

    private void ShowError(string message)
    {
        StatusText.Text = $"ERROR: {message}";
        StatusText.Foreground = Brushes.OrangeRed;

        // Reset color after 3 seconds
        Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            StatusText.Foreground = (Brush)FindResource("TextBrush");
        }));

        MessageBox.Show(message, "MechFFB Error", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _updateTimer?.Stop();
        _logFlushTimer?.Stop();
        _ffbEngine?.Stop();
        _ffbEngine?.Dispose();
        base.OnClosing(e);
    }


}