using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MechFFBEngine;
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

    // Log management
    private const int MAX_LOG_LINES = 250;
    private readonly Queue<string> _logLines = new Queue<string>();

    public bool IsDeviceSelected
    {
        get => _isDeviceSelected;
        set
        {
            _isDeviceSelected = value;
            // Would use INotifyPropertyChanged in production
        }
    }

    public MainWindow()
    {
        InitializeComponent();

        // Redirect Console.WriteLine to our debug window
        _consoleWriter = new System.IO.StringWriter();
        Console.SetOut(new DebugWriter(text => Dispatcher.Invoke(() => AppendLog(text))));

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
                }
            }
        };
    }

    private void AppendLog(string text)
    {
        // Add new log line
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

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        DebugLogTextBox.Clear();
        _logLines.Clear();
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
                // Restore checkbox states from config before device list
                // (must happen before RefreshDeviceList triggers auto-start)
                AutoStartCheckBox.IsChecked = _ffbEngine.Configuration.AutoStartEngine;

                LoadSlidersFromConfig(); // Load saved slider values
                RefreshDeviceList(isInitialLoad: true);
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

        Console.WriteLine("=== Loading Saved Settings ===");
        Console.WriteLine($"MasterIntensity from config: {config.MasterIntensity}");
        Console.WriteLine($"BallisticIntensity from config: {config.Simple.BallisticIntensity}");
        Console.WriteLine($"LaserIntensity from config: {config.Simple.LaserIntensity}");

        // Simple mode sliders
        MasterIntensitySlider.Value = config.MasterIntensity * 100;
        Console.WriteLine($"Set MasterIntensitySlider to: {MasterIntensitySlider.Value}");

        BallisticSlider.Value = config.Simple.BallisticIntensity * 100;
        Console.WriteLine($"Set BallisticSlider to: {BallisticSlider.Value}");

        LaserSlider.Value = config.Simple.LaserIntensity * 100;
        PPCSlider.Value = config.Simple.PPCIntensity * 100;
        MissileSlider.Value = config.Simple.MissileIntensity * 100;
        MachineGunSlider.Value = config.Simple.MachineGunIntensity * 100;
        MeleeSlider.Value = config.Simple.MeleeIntensity * 100;
        LaserDamageSlider.Value = config.Simple.LaserDamageIntensity * 100;
        BallisticDamageSlider.Value = config.Simple.BallisticDamageIntensity * 100;
        PPCDamageSlider.Value = config.Simple.PPCDamageIntensity * 100;
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
        // LaserDmgDurationSlider removed - now uses tier detection (600/760/890ms)
        LaserDmgFrequencySlider.Value = config.Advanced.LaserDamage.Frequency;
        LaserDmgAttackSlider.Value = config.Advanced.LaserDamage.AttackTime;
        LaserDmgFadeSlider.Value = config.Advanced.LaserDamage.FadeTime;
        BallisticDmgDurationSlider.Value = config.Advanced.BallisticDamage.Duration;
        BallisticDmgAttackSlider.Value = config.Advanced.BallisticDamage.AttackTime;
        BallisticDmgFadeSlider.Value = config.Advanced.BallisticDamage.FadeTime;
        PPCDmgDurationSlider.Value = config.Advanced.PPCDamage.Duration;
        PPCDmgAttackSlider.Value = config.Advanced.PPCDamage.AttackTime;
        PPCDmgFadeSlider.Value = config.Advanced.PPCDamage.FadeTime;
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

        Console.WriteLine("Loaded saved settings from configuration file");

        // Re-enable saving now that loading is complete
        _isLoadingSettings = false;

        // Verify sliders retained their values
        Console.WriteLine($"=== Verification After Load ===");
        Console.WriteLine($"MasterIntensitySlider.Value: {MasterIntensitySlider.Value}");
        Console.WriteLine($"BallisticSlider.Value: {BallisticSlider.Value}");
        Console.WriteLine($"LaserSlider.Value: {LaserSlider.Value}");
        Console.WriteLine($"Config MasterIntensity: {config.MasterIntensity}");
        Console.WriteLine($"Config BallisticIntensity: {config.Simple.BallisticIntensity}");
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
        {
            Console.WriteLine("Skipping save - currently loading settings");
            return;
        }

        // Restart the timer - this delays the save until user stops adjusting
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_ffbEngine == null)
            return;

        // Update running status
        if (_ffbEngine.IsRunning)
        {
            TelemetryStatusIndicator.Fill = Brushes.LimeGreen;
            TelemetryStatusText.Text = "FFB Active";
        }
        else
        {
            TelemetryStatusIndicator.Fill = Brushes.Red;
            TelemetryStatusText.Text = "FFB Stopped";
        }
    }

    private void RefreshDeviceList(bool isInitialLoad = false)
    {
        if (_ffbEngine == null)
            return;

        try
        {
            DeviceComboBox.Items.Clear();

            var devices = _ffbEngine.GetAvailableDevices();

            if (devices.Count == 0)
            {
                UpdateStatus("No SDL2 haptic devices found");
                ShowError("No FFB devices detected. Make sure your device is connected.");
                return;
            }

            foreach (var device in devices)
            {
                DeviceComboBox.Items.Add(device);
            }

            DeviceComboBox.DisplayMemberPath = "Name";
            UpdateStatus($"Found {devices.Count} FFB device(s)");

            // Try to restore saved device selection
            var savedGuid = _ffbEngine.Configuration.SelectedDeviceGuid;
            bool restoredDevice = false;

            if (savedGuid.HasValue)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    if (devices[i].InstanceGuid == savedGuid.Value)
                    {
                        DeviceComboBox.SelectedIndex = i;
                        Console.WriteLine($"Restored saved device: {devices[i].Name} ({savedGuid.Value})");
                        restoredDevice = true;
                        break;
                    }
                }

                if (!restoredDevice)
                {
                    Console.WriteLine($"Saved device not found (GUID: {savedGuid.Value}, Name: {_ffbEngine.Configuration.SelectedDeviceName ?? "unknown"}). Device may be disconnected.");
                }
            }

            // Fall back to auto-select first device if only one available and no saved device matched
            if (!restoredDevice && devices.Count == 1)
            {
                DeviceComboBox.SelectedIndex = 0;
            }

            // Auto-start engine if enabled and device was restored on initial load
            if (isInitialLoad && restoredDevice && _ffbEngine.Configuration.AutoStartEngine && IsDeviceSelected)
            {
                Console.WriteLine("Auto-starting FFB engine (saved device found, auto-start enabled)");
                Start_Click(this, new RoutedEventArgs());
            }
        }
        catch (Exception ex)
        {
            ShowError($"Error refreshing devices: {ex.Message}");
        }
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
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

                // Persist device selection
                _ffbEngine.Configuration.SelectedDeviceGuid = device.InstanceGuid;
                _ffbEngine.Configuration.SelectedDeviceName = device.Name;
                DebouncedSave();
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

    private void InvertDirection_Changed(object sender, RoutedEventArgs e)
    {
        if (_ffbEngine == null)
            return;

        bool invert = InvertDirectionCheckBox.IsChecked ?? false;
        _ffbEngine.SetInvertDirection(invert);

        // Save to config
        _ffbEngine.Configuration.InvertDirection = invert;
        DebouncedSave();
    }

    private void DisableExclusiveMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_ffbEngine == null)
            return;

        bool disable = DisableExclusiveModeCheckBox.IsChecked ?? false;
        _ffbEngine.SetDisableExclusiveMode(disable);

        // Save to config
        _ffbEngine.Configuration.DisableExclusiveMode = disable;
        DebouncedSave();

        // Notify user that restart is required
        if (IsDeviceSelected)
        {
            StatusText.Text = "Device restart required for exclusive mode change to take effect";
        }
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_ffbEngine == null)
            return;

        _ffbEngine.Configuration.AutoStartEngine = AutoStartCheckBox.IsChecked ?? false;
        DebouncedSave();
    }

    private void TestDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_ffbEngine == null)
            return;

        try
        {
            _ffbEngine.TestDevice();
        }
        catch (Exception ex)
        {
            ShowError($"Test failed: {ex.Message}");
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

            UpdateStatus("FFB Engine Stopped");
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
        config.Simple.LaserDamageIntensity = (float)(LaserDamageSlider.Value / 100.0);
        config.Simple.BallisticDamageIntensity = (float)(BallisticDamageSlider.Value / 100.0);
        config.Simple.PPCDamageIntensity = (float)(PPCDamageSlider.Value / 100.0);
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

        // Damage effects (Laser damage Duration removed - now uses tier detection)
        config.Advanced.LaserDamage.Frequency = (int)LaserDmgFrequencySlider.Value;
        config.Advanced.LaserDamage.AttackTime = (int)LaserDmgAttackSlider.Value;
        config.Advanced.LaserDamage.FadeTime = (int)LaserDmgFadeSlider.Value;

        config.Advanced.BallisticDamage.Duration = (int)BallisticDmgDurationSlider.Value;
        config.Advanced.BallisticDamage.AttackTime = (int)BallisticDmgAttackSlider.Value;
        config.Advanced.BallisticDamage.FadeTime = (int)BallisticDmgFadeSlider.Value;

        config.Advanced.PPCDamage.Duration = (int)PPCDmgDurationSlider.Value;
        config.Advanced.PPCDamage.AttackTime = (int)PPCDmgAttackSlider.Value;
        config.Advanced.PPCDamage.FadeTime = (int)PPCDmgFadeSlider.Value;

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

        InvertDirectionCheckBox.IsChecked = config.InvertDirection;
        _ffbEngine?.SetInvertDirection(config.InvertDirection);

        DisableExclusiveModeCheckBox.IsChecked = config.DisableExclusiveMode;
        _ffbEngine?.SetDisableExclusiveMode(config.DisableExclusiveMode);

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
        _ffbEngine?.Stop();
        _ffbEngine?.Dispose();
        base.OnClosing(e);
    }


}