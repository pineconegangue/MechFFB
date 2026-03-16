using SharpDX;
using SharpDX.DirectInput;

namespace MechFFBEngine.Haptic;

/// <summary>
/// Manages DirectInput Force Feedback devices
/// Complete replacement for SDL2HapticManager with DirectX FFB support
/// Optimized for VPForce Rhino and other DirectInput FFB devices
/// </summary>
public class DirectInputHapticManager : IDisposable
{
    private DirectInput? _directInput;
    private Joystick? _joystick;
    private List<HapticDeviceInfo> _availableDevices = new();
    private int _selectedDeviceIndex = -1;
    private Dictionary<int, Effect> _activeEffects = new();
    private int _nextEffectId = 0;
    private int[]? _deviceAxes = null; // Cache device axes

    /// <summary>
    /// Optional window handle for cooperative level
    /// Set this from your UI application's main window handle if you want exclusive access
    /// </summary>
    public IntPtr WindowHandle { get; set; } = IntPtr.Zero;

    /// <summary>
    /// Invert force direction (some devices have opposite axis conventions)
    /// Set to true if impacts push the wrong way
    /// </summary>
    public bool InvertDirection { get; set; } = false;

    /// <summary>
    /// Disable exclusive mode - use non-exclusive for all devices
    /// Useful for devices like Sidewinder FFB2 that need hardware centering spring
    /// </summary>
    public bool DisableExclusiveMode { get; set; } = false;

    public bool IsInitialized { get; private set; }
    public bool HasDeviceSelected => _joystick != null;

    public class HapticDeviceInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public Guid InstanceGuid { get; set; }
        public bool SupportsConstantForce { get; set; }
        public bool SupportsPeriodicEffects { get; set; }
        public bool SupportsRampForce { get; set; }
        public int NumAxes { get; set; }
        public int MaxEffects { get; set; }
    }

    /// <summary>
    /// Initialize DirectInput
    /// </summary>
    public bool Initialize()
    {
        try
        {
            Console.WriteLine("=== Initializing DirectInput for Force Feedback ===");

            _directInput = new DirectInput();

            RefreshDeviceList();
            IsInitialized = true;
            Console.WriteLine($"✓ DirectInput initialized successfully. Found {_availableDevices.Count} FFB device(s).");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Failed to initialize DirectInput: {ex.Message}");
            IsInitialized = false;
            return false;
        }
    }

    /// <summary>
    /// Get list of all force feedback capable devices
    /// </summary>
    public List<HapticDeviceInfo> GetHapticDevices()
    {
        RefreshDeviceList();
        return _availableDevices.ToList();
    }

    private void RefreshDeviceList()
    {
        if (_directInput == null)
            return;

        _availableDevices.Clear();

        try
        {
            // Get all devices - not just GameControl class, as some FFB devices use other classes
            var devices = _directInput.GetDevices(DeviceClass.All, DeviceEnumerationFlags.AttachedOnly);

            Console.WriteLine($"Found {devices.Count} device(s) total");
            Console.WriteLine("Scanning for Force Feedback capable devices...");

            int index = 0;
            int ffbDeviceIndex = 0; // Separate counter for FFB devices only

            foreach (var deviceInstance in devices)
            {
                Console.WriteLine($"  Device {index}: {deviceInstance.ProductName}");
                Console.WriteLine($"    GUID: {deviceInstance.InstanceGuid}");
                Console.WriteLine($"    Type: {deviceInstance.Type}");

                // Try to open the device and check if it actually supports FFB
                // Some devices don't report FFB in their flags but still support it
                bool supportsForceFeedback = false;
                try
                {
                    var tempJoystick = new Joystick(_directInput, deviceInstance.InstanceGuid);
                    var capabilities = tempJoystick.Capabilities;

                    // Check if device has FFB capability
                    supportsForceFeedback = capabilities.Flags.HasFlag(DeviceFlags.ForceFeedback);

                    Console.WriteLine($"    FFB Flag: {supportsForceFeedback}");
                    Console.WriteLine($"    Capabilities Flags: {capabilities.Flags}");

                    tempJoystick.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    Could not query device: {ex.Message}");
                }

                if (supportsForceFeedback)
                {
                    Console.WriteLine($"    ✓ Supports Force Feedback!");

                    // Temporarily open device to query capabilities
                    try
                    {
                        var tempJoystick = new Joystick(_directInput, deviceInstance.InstanceGuid);
                        var capabilities = tempJoystick.Capabilities;

                        var deviceInfo = new HapticDeviceInfo
                        {
                            Index = ffbDeviceIndex,  // Use FFB device counter, not global index
                            Name = deviceInstance.ProductName,
                            InstanceGuid = deviceInstance.InstanceGuid,
                            NumAxes = capabilities.AxeCount,
                            MaxEffects = 32 // Default max effects, SharpDX doesn't expose this directly
                        };

                        // Check supported effect types
                        var effects = tempJoystick.GetEffects();
                        deviceInfo.SupportsConstantForce = effects.Any(e => e.Guid == DirectInputFFB.GUID_ConstantForce);
                        deviceInfo.SupportsPeriodicEffects = effects.Any(e => e.Guid == DirectInputFFB.GUID_Sine);
                        deviceInfo.SupportsRampForce = effects.Any(e => e.Guid == DirectInputFFB.GUID_RampForce);

                        _availableDevices.Add(deviceInfo);
                        ffbDeviceIndex++; // Increment FFB device counter

                        Console.WriteLine($"      FFB Device Index: {deviceInfo.Index}");

                        Console.WriteLine($"      Axes: {deviceInfo.NumAxes}");
                        Console.WriteLine($"      Max Effects: {deviceInfo.MaxEffects}");
                        Console.WriteLine($"      Constant Force: {deviceInfo.SupportsConstantForce}");
                        Console.WriteLine($"      Periodic (Sine): {deviceInfo.SupportsPeriodicEffects}");
                        Console.WriteLine($"      Ramp Force: {deviceInfo.SupportsRampForce}");

                        tempJoystick.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    ✗ Error querying device: {ex.Message}");
                    }
                }
                else
                {
                    // Even if FFB flag is not set, let's try to create effects anyway
                    // Some devices (like MOZA) might still work
                    Console.WriteLine($"    ⚠ FFB flag not set, but will try anyway...");

                    try
                    {
                        var tempJoystick = new Joystick(_directInput, deviceInstance.InstanceGuid);
                        var capabilities = tempJoystick.Capabilities;

                        // Try to get effects list - if this works, device supports FFB
                        var effects = tempJoystick.GetEffects();

                        if (effects.Count > 0)
                        {
                            Console.WriteLine($"    ✓ Device has {effects.Count} effect types - treating as FFB device!");

                            var deviceInfo = new HapticDeviceInfo
                            {
                                Index = ffbDeviceIndex,  // Use FFB device counter, not global index
                                Name = deviceInstance.ProductName,
                                InstanceGuid = deviceInstance.InstanceGuid,
                                NumAxes = capabilities.AxeCount,
                                MaxEffects = 32
                            };

                            deviceInfo.SupportsConstantForce = effects.Any(e => e.Guid == DirectInputFFB.GUID_ConstantForce);
                            deviceInfo.SupportsPeriodicEffects = effects.Any(e => e.Guid == DirectInputFFB.GUID_Sine);
                            deviceInfo.SupportsRampForce = effects.Any(e => e.Guid == DirectInputFFB.GUID_RampForce);

                            _availableDevices.Add(deviceInfo);
                            ffbDeviceIndex++; // Increment FFB device counter

                            Console.WriteLine($"      FFB Device Index: {deviceInfo.Index}");

                            Console.WriteLine($"      Axes: {deviceInfo.NumAxes}");
                            Console.WriteLine($"      Constant Force: {deviceInfo.SupportsConstantForce}");
                            Console.WriteLine($"      Periodic (Sine): {deviceInfo.SupportsPeriodicEffects}");
                        }
                        else
                        {
                            Console.WriteLine($"    ✗ No FFB effects available");
                        }

                        tempJoystick.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    ✗ Cannot open as FFB device: {ex.Message}");
                    }
                }

                index++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Error enumerating devices: {ex.Message}");
        }
    }

    /// <summary>
    /// Select and open a specific FFB device
    /// </summary>
    public bool SelectDevice(int deviceIndex)
    {
        try
        {
            // Release current device if any
            ReleaseDevice();

            if (deviceIndex < 0 || deviceIndex >= _availableDevices.Count)
            {
                Console.WriteLine($"✗ Invalid device index: {deviceIndex}");
                return false;
            }

            var deviceInfo = _availableDevices[deviceIndex];
            Console.WriteLine($"=== Selecting FFB Device ===");
            Console.WriteLine($"  Name: {deviceInfo.Name}");
            Console.WriteLine($"  GUID: {deviceInfo.InstanceGuid}");

            if (_directInput == null)
            {
                Console.WriteLine("✗ DirectInput not initialized");
                return false;
            }

            // Create joystick device
            _joystick = new Joystick(_directInput, deviceInfo.InstanceGuid);

            // Set cooperative level
            // If DisableExclusiveMode is true, always use non-exclusive
            // Otherwise, if WindowHandle is provided, use exclusive mode
            try
            {
                if (DisableExclusiveMode)
                {
                    // Force non-exclusive mode (for devices like Sidewinder FFB2)
                    _joystick.SetCooperativeLevel(IntPtr.Zero,
                        CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                    Console.WriteLine("  Cooperative level: Background + NonExclusive (DisableExclusiveMode = true)");
                }
                else if (WindowHandle != IntPtr.Zero)
                {
                    // Application provided window handle - use exclusive mode for best FFB
                    _joystick.SetCooperativeLevel(WindowHandle,
                        CooperativeLevel.Background | CooperativeLevel.Exclusive);
                    Console.WriteLine("  Cooperative level: Background + Exclusive (with window handle)");
                }
                else
                {
                    // No window handle - use non-exclusive mode
                    _joystick.SetCooperativeLevel(IntPtr.Zero,
                        CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                    Console.WriteLine("  Cooperative level: Background + NonExclusive");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠ Could not set cooperative level: {ex.Message}");
                Console.WriteLine("  Trying without SetCooperativeLevel...");
                // Some devices work without explicitly setting cooperative level
            }

            // Acquire the device
            _joystick.Acquire();

            // Query and cache FFB axes - try multiple methods as devices vary
            Console.WriteLine("  Querying device axes...");

            // Method 1: Try ForceFeedbackActuator type
            var ffbObjects = _joystick.GetObjects(DeviceObjectTypeFlags.ForceFeedbackActuator);
            if (ffbObjects.Count > 0)
            {
                _deviceAxes = ffbObjects.Select(obj => obj.Offset).ToArray();
                Console.WriteLine($"  Found {_deviceAxes.Length} FFB actuator(s)");
            }
            else
            {
                // Method 2: Try any Axis type
                Console.WriteLine("  No FFB actuators found, trying all axes...");
                var allAxes = _joystick.GetObjects(DeviceObjectTypeFlags.Axis);
                if (allAxes.Count > 0)
                {
                    // For racing wheels, typically only use first 1-2 axes (X, Y for steering)
                    // Using all axes (8+) causes Invalid Arguments error
                    int axesToUse = Math.Min(2, allAxes.Count);
                    _deviceAxes = allAxes.Take(axesToUse).Select(obj => obj.Offset).ToArray();
                    Console.WriteLine($"  Found {allAxes.Count} total axes, using first {_deviceAxes.Length} for FFB");
                    foreach (var axis in allAxes.Take(axesToUse))
                    {
                        Console.WriteLine($"    - {axis.Name} (Offset: {axis.Offset})");
                    }
                }
                else
                {
                    // Method 3: Default to X axis (offset 0)
                    Console.WriteLine("  No axes enumerated, defaulting to X axis (offset 0)");
                    _deviceAxes = new int[] { 0 };
                }
            }

            _selectedDeviceIndex = deviceIndex;

            Console.WriteLine($"✓ Device selected and acquired successfully");
            Console.WriteLine($"  Ready for Force Feedback effects!");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Failed to select device: {ex.Message}");
            Console.WriteLine($"  Stack trace: {ex.StackTrace}");
            ReleaseDevice();
            return false;
        }
    }

    /// <summary>
    /// Test the device with a simple constant force effect
    /// </summary>
    public bool TestDevice()
    {
        if (_joystick == null)
        {
            Console.WriteLine("✗ No device selected");
            return false;
        }

        try
        {
            Console.WriteLine("=== Testing FFB Device ===");

            // Query axes using multiple methods
            var ffbObjects = _joystick.GetObjects(DeviceObjectTypeFlags.ForceFeedbackActuator);
            var axes = new List<int>();

            if (ffbObjects.Count > 0)
            {
                Console.WriteLine($"Device has {ffbObjects.Count} FFB actuator(s):");
                foreach (var obj in ffbObjects)
                {
                    Console.WriteLine($"  - {obj.Name} (Offset: {obj.Offset})");
                    axes.Add(obj.Offset);
                }
            }
            else
            {
                Console.WriteLine("No FFB actuators, trying all axes...");
                var allAxes = _joystick.GetObjects(DeviceObjectTypeFlags.Axis);
                if (allAxes.Count > 0)
                {
                    // Only use first 1-2 axes for racing wheels
                    int axesToUse = Math.Min(2, allAxes.Count);
                    Console.WriteLine($"Device has {allAxes.Count} axis/axes, using first {axesToUse}:");
                    foreach (var obj in allAxes.Take(axesToUse))
                    {
                        Console.WriteLine($"  - {obj.Name} (Offset: {obj.Offset})");
                        axes.Add(obj.Offset);
                    }
                }
                else
                {
                    Console.WriteLine("No axes found, using default X axis (offset 0)");
                    axes.Add(0);
                }
            }

            Console.WriteLine($"Using {axes.Count} axis/axes for FFB effect");
            Console.WriteLine("Creating test effect (500ms constant force)...");

            // Try Cartesian coordinates first (most common)
            bool useCartesian = true;

            // Create parameters with actual device axes
            var parameters = new EffectParameters();
            parameters.Duration = 500000; // 500ms in microseconds
            parameters.Flags = (useCartesian ? EffectFlags.Cartesian : EffectFlags.Polar) | EffectFlags.ObjectOffsets;
            parameters.Gain = 10000; // 100%
            parameters.SamplePeriod = 0;
            parameters.StartDelay = 0;
            parameters.TriggerButton = -1;
            parameters.TriggerRepeatInterval = 0;

            // Use the actual axes from the device
            parameters.Axes = axes.ToArray();

            // Direction - create array matching number of axes
            int[] directions = new int[axes.Count];
            if (axes.Count == 1)
            {
                directions[0] = 10000; // Full force on single axis
            }
            else if (axes.Count >= 2)
            {
                directions[0] = 10000; // X: Right
                directions[1] = 0;     // Y: Neutral
                for (int i = 2; i < axes.Count; i++)
                    directions[i] = 0; // Other axes neutral
            }
            parameters.Directions = directions;

            // Envelope
            var envelope = new Envelope();
            envelope.AttackLevel = 0;
            envelope.AttackTime = 0;
            envelope.FadeLevel = 0;
            envelope.FadeTime = 100000; // 100ms fade
            parameters.Envelope = envelope;

            // Constant force - moderate strength
            var constantForce = new ConstantForce();
            constantForce.Magnitude = 6000; // 60% of 10000 max
            parameters.Parameters = constantForce;

            Console.WriteLine("Uploading effect to device...");

            Effect? effect = null;
            try
            {
                effect = new Effect(_joystick, DirectInputFFB.GUID_ConstantForce, parameters);
            }
            catch (SharpDXException ex) when (ex.ResultCode == SharpDX.Result.InvalidArg)
            {
                Console.WriteLine("  ⚠ Cartesian coordinates failed, trying Polar...");

                // Retry with Polar coordinates
                parameters.Flags = EffectFlags.Polar | EffectFlags.ObjectOffsets;

                // Polar uses angle (0-35999) and magnitude
                if (axes.Count >= 2)
                {
                    parameters.Directions = new int[] { 9000, 0 }; // 90 degrees = right
                }
                else
                {
                    parameters.Directions = new int[] { 0 }; // Straight
                }

                effect = new Effect(_joystick, DirectInputFFB.GUID_ConstantForce, parameters);
                Console.WriteLine("  ✓ Polar coordinates worked!");
            }

            if (effect == null)
            {
                Console.WriteLine("✗ Could not create effect");
                return false;
            }

            Console.WriteLine("Starting effect...");
            effect.Start();

            Console.WriteLine("✓ Test effect playing! You should feel it now.");

            // Wait for effect to complete
            System.Threading.Thread.Sleep(600);

            effect.Stop();
            effect.Dispose();

            Console.WriteLine("✓ Test complete!");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Test failed: {ex.Message}");
            Console.WriteLine($"  Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    /// <summary>
    /// Create a constant force effect with envelope
    /// </summary>
    public int CreateConstantEffect(int magnitude, int duration, int direction = 0)
    {
        if (_joystick == null || _deviceAxes == null || _deviceAxes.Length == 0)
            return -1;

        try
        {
            var parameters = new EffectParameters();
            parameters.Duration = duration * 1000; // ms to microseconds
            parameters.Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets;
            parameters.Gain = 10000;
            parameters.SamplePeriod = 0;
            parameters.StartDelay = 0;
            parameters.TriggerButton = -1;
            parameters.TriggerRepeatInterval = 0;

            // Use device's actual axes
            parameters.Axes = _deviceAxes;

            // Direction conversion now handled in FFBEngine (clockwise/counter-clockwise)
            // Just use the direction value directly

            int[] directions = new int[_deviceAxes.Length];
            if (_deviceAxes.Length == 1)
            {
                directions[0] = 10000;
            }
            else if (_deviceAxes.Length >= 2)
            {
                double angleRad = direction * Math.PI / 18000.0;
                directions[0] = (int)(Math.Cos(angleRad) * 10000);
                directions[1] = (int)(Math.Sin(angleRad) * 10000);
                for (int i = 2; i < _deviceAxes.Length; i++)
                    directions[i] = 0;
            }
            parameters.Directions = directions;

            // Envelope
            var envelope = new Envelope();
            envelope.AttackLevel = 0;
            envelope.AttackTime = 0;
            envelope.FadeLevel = 0;
            envelope.FadeTime = (duration / 4) * 1000; // microseconds
            parameters.Envelope = envelope;

            // Constant force
            var constantForce = new ConstantForce();
            constantForce.Magnitude = (int)((magnitude / 32767.0) * 10000);
            parameters.Parameters = constantForce;

            var effect = new Effect(_joystick, DirectInputFFB.GUID_ConstantForce, parameters);

            int effectId = _nextEffectId++;
            _activeEffects[effectId] = effect;

            return effectId;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Failed to create constant effect: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Create a periodic (rumble/vibration) effect - perfect for lasers and missiles!
    /// </summary>
    public int CreatePeriodicEffect(int magnitude, int duration, int frequency = 30, int attackMs = 50, int fadeMs = 100)
    {
        if (_joystick == null || _deviceAxes == null || _deviceAxes.Length == 0)
            return -1;

        try
        {
            var parameters = new EffectParameters();
            parameters.Duration = duration * 1000;
            parameters.Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets;
            parameters.Gain = 10000;
            parameters.SamplePeriod = 0;
            parameters.StartDelay = 0;
            parameters.TriggerButton = -1;
            parameters.TriggerRepeatInterval = 0;

            // Use device's actual axes
            parameters.Axes = _deviceAxes;

            // Direction - all axes equally for vibration
            int[] directions = new int[_deviceAxes.Length];
            for (int i = 0; i < _deviceAxes.Length; i++)
                directions[i] = 10000;
            parameters.Directions = directions;

            // Envelope
            var envelope = new Envelope();
            envelope.AttackLevel = (int)((magnitude / 32767.0 / 2.0) * 10000);
            envelope.AttackTime = attackMs * 1000;
            envelope.FadeLevel = 0;
            envelope.FadeTime = fadeMs * 1000;
            parameters.Envelope = envelope;

            // Periodic force
            var periodic = new PeriodicForce();
            periodic.Magnitude = (int)((magnitude / 32767.0) * 10000);
            periodic.Offset = 0;
            periodic.Phase = 0;
            periodic.Period = (1000000 / frequency);
            parameters.Parameters = periodic;

            var effect = new Effect(_joystick, DirectInputFFB.GUID_Sine, parameters);

            int effectId = _nextEffectId++;
            _activeEffects[effectId] = effect;

            return effectId;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Failed to create periodic effect: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Create a constant effect with custom envelope - good for impacts!
    /// </summary>
    public int CreateImpactEffect(int magnitude, int duration, int attackMs = 20, int fadeMs = 150, int direction = 0)
    {
        if (_joystick == null || _deviceAxes == null || _deviceAxes.Length == 0)
            return -1;

        try
        {
            var parameters = new EffectParameters();
            parameters.Duration = duration * 1000;
            parameters.Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets;
            parameters.Gain = 10000;
            parameters.SamplePeriod = 0;
            parameters.StartDelay = 0;
            parameters.TriggerButton = -1;
            parameters.TriggerRepeatInterval = 0;

            // Use device's actual axes
            parameters.Axes = _deviceAxes;

            // Direction conversion now handled in FFBEngine (clockwise/counter-clockwise)
            // Just use the direction value directly

            int[] directions = new int[_deviceAxes.Length];
            if (_deviceAxes.Length == 1)
            {
                directions[0] = 10000;
            }
            else if (_deviceAxes.Length >= 2)
            {
                double angleRad = direction * Math.PI / 18000.0;
                directions[0] = (int)(Math.Cos(angleRad) * 10000);
                directions[1] = (int)(Math.Sin(angleRad) * 10000);
                for (int i = 2; i < _deviceAxes.Length; i++)
                    directions[i] = 0;
            }
            parameters.Directions = directions;

            // Envelope - sharp attack, long fade for impact
            var envelope = new Envelope();
            envelope.AttackLevel = (int)((magnitude / 32767.0 / 3.0) * 10000);
            envelope.AttackTime = attackMs * 1000;
            envelope.FadeLevel = 0;
            envelope.FadeTime = fadeMs * 1000;
            parameters.Envelope = envelope;

            // Constant force
            var constantForce = new ConstantForce();
            constantForce.Magnitude = (int)((magnitude / 32767.0) * 10000);
            parameters.Parameters = constantForce;

            var effect = new Effect(_joystick, DirectInputFFB.GUID_ConstantForce, parameters);

            int effectId = _nextEffectId++;
            _activeEffects[effectId] = effect;

            return effectId;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Failed to create impact effect: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Create a combined explosion effect - impact + rumble with separate controls
    /// Returns array of effect IDs [impact, rumble]
    /// </summary>
    public int[] CreateExplosionEffect(int baseMagnitude, int impactDuration, int rumbleDuration,
        int impactAttackMs, int impactFadeMs, int rumbleAttackMs, int rumbleFadeMs,
        int direction, int rumbleFreq, float impactMultiplier, float rumbleMultiplier)
    {
        var effectIds = new int[2];

        // Create impact effect
        int impactMag = (int)(baseMagnitude * impactMultiplier);
        impactMag = Math.Clamp(impactMag, 0, 32767);
        effectIds[0] = CreateImpactEffect(impactMag, impactDuration, impactAttackMs, impactFadeMs, direction);

        // Create rumble effect
        int rumbleMag = (int)(baseMagnitude * rumbleMultiplier);
        rumbleMag = Math.Clamp(rumbleMag, 0, 32767);
        effectIds[1] = CreatePeriodicEffect(rumbleMag, rumbleDuration, rumbleFreq, rumbleAttackMs, rumbleFadeMs);

        return effectIds;
    }

    /// <summary>
    /// Play an effect
    /// </summary>
    public bool PlayEffect(int effectId, int iterations = 1)
    {
        if (!_activeEffects.TryGetValue(effectId, out var effect))
            return false;

        try
        {
            effect.Start(iterations);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Failed to play effect {effectId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stop an effect
    /// </summary>
    public void StopEffect(int effectId)
    {
        if (_activeEffects.TryGetValue(effectId, out var effect))
        {
            try
            {
                effect.Stop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Failed to stop effect {effectId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Update the magnitude of an existing periodic effect in-place via SetParameters.
    /// Used to keep a long-running laser damage effect at the correct intensity as
    /// damage ticks arrive with varying amounts throughout the beam duration.
    /// </summary>
    public void UpdateEffectMagnitude(int effectId, int magnitude, int attackMs, int fadeMs)
    {
        if (!_activeEffects.TryGetValue(effectId, out var effect))
            return;

        try
        {
            var parameters = new EffectParameters();
            parameters.Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets;

            var envelope = new Envelope();
            envelope.AttackLevel = (int)((magnitude / 32767.0 / 2.0) * 10000);
            envelope.AttackTime = attackMs * 1000;
            envelope.FadeLevel = 0;
            envelope.FadeTime = fadeMs * 1000;
            parameters.Envelope = envelope;

            var periodic = new PeriodicForce();
            periodic.Magnitude = (int)((magnitude / 32767.0) * 10000);
            periodic.Offset = 0;
            periodic.Phase = 0;
            periodic.Period = 0; // 0 = keep existing period unchanged
            parameters.Parameters = periodic;

            effect.SetParameters(parameters,
                EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Envelope);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Failed to update effect magnitude {effectId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensure the device's force feedback output is active.
    /// Some drivers pause FF output after effect.Stop() — calling Continue() before
    /// starting the next effect guarantees the device is in an output-enabled state.
    /// </summary>
    public void ContinueDevice()
    {
        try
        {
            _joystick?.SendForceFeedbackCommand(ForceFeedbackCommand.Continue);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Failed to continue device: {ex.Message}");
        }
    }

    /// <summary>
    /// Destroy an effect and free resources.
    /// Callers that need to halt a running effect must call StopEffect first.
    /// DestroyEffect does NOT call Stop() — avoids a redundant driver round-trip
    /// for the common case where the effect has already been stopped or naturally expired.
    /// </summary>
    public void DestroyEffect(int effectId)
    {
        if (_activeEffects.TryGetValue(effectId, out var effect))
        {
            try
            {
                effect.Dispose();
                _activeEffects.Remove(effectId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Failed to destroy effect {effectId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Stop all effects
    /// </summary>
    public void StopAllEffects()
    {
        foreach (var effect in _activeEffects.Values)
        {
            try
            {
                effect.Stop();
            }
            catch { }
        }
    }

    private void ReleaseDevice()
    {
        // Clean up all effects
        foreach (var effect in _activeEffects.Values)
        {
            try
            {
                effect.Stop();
                effect.Dispose();
            }
            catch { }
        }
        _activeEffects.Clear();

        if (_joystick != null)
        {
            try
            {
                _joystick.Unacquire();
                _joystick.Dispose();
            }
            catch { }
            _joystick = null;
        }

        _selectedDeviceIndex = -1;
    }

    public void Dispose()
    {
        ReleaseDevice();

        if (_directInput != null)
        {
            _directInput.Dispose();
            _directInput = null;
        }

        IsInitialized = false;
    }
}