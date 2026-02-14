using System.Runtime.InteropServices;

namespace MechFFBEngine.Haptic;

/// <summary>
/// Manages SDL2 haptic (force feedback) devices
/// Uses SDL2 which is what DCS and many games use for FFB
/// Now with built-in P/Invoke - no NuGet packages required!
/// </summary>
public class SDL2HapticManager : IDisposable
{
    private IntPtr _hapticDevice = IntPtr.Zero;
    private IntPtr _joystick = IntPtr.Zero;
    private List<HapticDeviceInfo> _availableDevices = new();
    private int _selectedDeviceIndex = -1;
    
    public bool IsInitialized { get; private set; }
    public bool HasDeviceSelected => _hapticDevice != IntPtr.Zero;
    
    public class HapticDeviceInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public bool SupportsConstant { get; set; }
        public bool SupportsRamp { get; set; }
        public bool SupportsSine { get; set; }
        public int NumAxes { get; set; }
    }
    
    /// <summary>
    /// Initialize SDL2 haptic subsystem
    /// </summary>
    public bool Initialize()
    {
        try
        {
            // Initialize SDL2 with joystick and haptic subsystems
            if (SDL2.SDL_Init(SDL2.SDL_INIT_JOYSTICK | SDL2.SDL_INIT_HAPTIC) < 0)
            {
                Console.WriteLine($"SDL_Init failed: {SDL2.SDL_GetErrorString()}");
                return false;
            }
            
            RefreshDeviceList();
            IsInitialized = true;
            Console.WriteLine($"SDL2 initialized successfully. Found {_availableDevices.Count} haptic devices.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize SDL2: {ex.Message}");
            IsInitialized = false;
            return false;
        }
    }
    
    /// <summary>
    /// Get list of all haptic-capable devices
    /// </summary>
    public List<HapticDeviceInfo> GetHapticDevices()
    {
        RefreshDeviceList();
        return _availableDevices.ToList();
    }
    
    private void RefreshDeviceList()
    {
        _availableDevices.Clear();
        
        int numJoysticks = SDL2.SDL_NumJoysticks();
        Console.WriteLine($"Found {numJoysticks} joystick(s)");
        
        for (int i = 0; i < numJoysticks; i++)
        {
            var name = SDL2.SDL_JoystickNameForIndexString(i);
            Console.WriteLine($"  Joystick {i}: {name}");
            
            // Open the joystick
            var tempJoy = SDL2.SDL_JoystickOpen(i);
            if (tempJoy == IntPtr.Zero)
            {
                Console.WriteLine($"    Failed to open joystick: {SDL2.SDL_GetErrorString()}");
                continue;
            }
            
            // Check if this joystick supports haptic
            int isHaptic = SDL2.SDL_JoystickIsHaptic(tempJoy);
            Console.WriteLine($"    Is haptic: {isHaptic}");
            
            if (isHaptic == 1)
            {
                var tempHaptic = SDL2.SDL_HapticOpenFromJoystick(tempJoy);
                if (tempHaptic != IntPtr.Zero)
                {
                    uint supported = SDL2.SDL_HapticQuery(tempHaptic);
                    
                    var deviceInfo = new HapticDeviceInfo
                    {
                        Index = i,
                        Name = name,
                        SupportsConstant = (supported & SDL2.SDL_HAPTIC_CONSTANT) != 0,
                        SupportsRamp = (supported & SDL2.SDL_HAPTIC_RAMP) != 0,
                        SupportsSine = (supported & SDL2.SDL_HAPTIC_SINE) != 0,
                        NumAxes = SDL2.SDL_HapticNumAxes(tempHaptic)
                    };
                    
                    _availableDevices.Add(deviceInfo);
                    Console.WriteLine($"    ✓ Haptic device added!");
                    Console.WriteLine($"    Constant: {deviceInfo.SupportsConstant}, Sine: {deviceInfo.SupportsSine}, Axes: {deviceInfo.NumAxes}");
                    
                    SDL2.SDL_HapticClose(tempHaptic);
                }
                else
                {
                    Console.WriteLine($"    Failed to open haptic: {SDL2.SDL_GetErrorString()}");
                }
            }
            
            SDL2.SDL_JoystickClose(tempJoy);
        }
    }
    
    /// <summary>
    /// Select and open a specific haptic device
    /// </summary>
    public bool SelectDevice(int deviceIndex)
    {
        try
        {
            // Release current device if any
            ReleaseDevice();
            
            Console.WriteLine($"Selecting haptic device index: {deviceIndex}");
            
            // Open the joystick
            _joystick = SDL2.SDL_JoystickOpen(deviceIndex);
            if (_joystick == IntPtr.Zero)
            {
                Console.WriteLine($"Failed to open joystick: {SDL2.SDL_GetErrorString()}");
                return false;
            }
            
            // Open haptic from joystick
            _hapticDevice = SDL2.SDL_HapticOpenFromJoystick(_joystick);
            if (_hapticDevice == IntPtr.Zero)
            {
                Console.WriteLine($"Failed to open haptic: {SDL2.SDL_GetErrorString()}");
                SDL2.SDL_JoystickClose(_joystick);
                _joystick = IntPtr.Zero;
                return false;
            }
            
            _selectedDeviceIndex = deviceIndex;
            
            // Query capabilities
            uint supported = SDL2.SDL_HapticQuery(_hapticDevice);
            Console.WriteLine($"Successfully opened haptic device");
            Console.WriteLine($"  Supported effects: 0x{supported:X8}");
            Console.WriteLine($"  Constant: {(supported & SDL2.SDL_HAPTIC_CONSTANT) != 0}");
            Console.WriteLine($"  Sine: {(supported & SDL2.SDL_HAPTIC_SINE) != 0}");
            Console.WriteLine($"  Num axes: {SDL2.SDL_HapticNumAxes(_hapticDevice)}");
            Console.WriteLine($"  Max effects: {SDL2.SDL_HapticNumEffects(_hapticDevice)}");
            Console.WriteLine($"  Max playing: {SDL2.SDL_HapticNumEffectsPlaying(_hapticDevice)}");
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to select device: {ex.Message}");
            ReleaseDevice();
            return false;
        }
    }
    
    /// <summary>
    /// Create and play a simple constant force test effect
    /// </summary>
    public bool TestDevice()
    {
        if (_hapticDevice == IntPtr.Zero)
        {
            Console.WriteLine("No haptic device selected");
            return false;
        }
        
        try
        {
            Console.WriteLine("Creating test constant force effect...");
            
            // Create a simple constant force effect
            var effect = new SDL2.SDL_HapticEffect();
            effect.type = SDL2.SDL_HAPTIC_CONSTANT;
            
            var constant = new SDL2.SDL_HapticConstant();
            constant.type = SDL2.SDL_HAPTIC_CONSTANT;
            constant.direction.type = SDL2.SDL_HAPTIC_CARTESIAN;
            constant.direction.dir0 = 10000; // X axis, full force
            constant.direction.dir1 = 0;
            constant.direction.dir2 = 0;
            constant.length = 500; // 500ms
            constant.level = 20000; // 20000 out of 32767 max
            constant.attack_length = 0;
            constant.attack_level = 0;
            constant.fade_length = 100;
            constant.fade_level = 0;
            
            // Copy constant to effect union
            effect.constant = constant;
            
            Console.WriteLine("Uploading effect to device...");
            int effectId = SDL2.SDL_HapticNewEffect(_hapticDevice, ref effect);
            if (effectId < 0)
            {
                Console.WriteLine($"Failed to create effect: {SDL2.SDL_GetErrorString()}");
                return false;
            }
            
            Console.WriteLine($"Effect created with ID: {effectId}");
            Console.WriteLine("Running effect...");
            
            if (SDL2.SDL_HapticRunEffect(_hapticDevice, effectId, 1) < 0)
            {
                Console.WriteLine($"Failed to run effect: {SDL2.SDL_GetErrorString()}");
                SDL2.SDL_HapticDestroyEffect(_hapticDevice, effectId);
                return false;
            }
            
            Console.WriteLine("Effect playing!");
            
            // Wait for effect to finish
            System.Threading.Thread.Sleep(600);
            
            // Clean up
            SDL2.SDL_HapticDestroyEffect(_hapticDevice, effectId);
            Console.WriteLine("Test completed successfully!");
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Test failed: {ex.Message}");
            Console.WriteLine($"Stack: {ex.StackTrace}");
            return false;
        }
    }
    
    /// <summary>
    /// Create a constant force effect
    /// </summary>
    public int CreateConstantEffect(int magnitude, int duration, int direction = 0)
    {
        if (_hapticDevice == IntPtr.Zero)
            return -1;
        
        var effect = new SDL2.SDL_HapticEffect();
        effect.type = SDL2.SDL_HAPTIC_CONSTANT;
        
        var constant = new SDL2.SDL_HapticConstant();
        constant.type = SDL2.SDL_HAPTIC_CONSTANT;
        constant.direction.type = SDL2.SDL_HAPTIC_CARTESIAN;
        
        // Convert direction (0-35999) to X/Y components
        double angleRad = direction * Math.PI / 18000.0;
        int x = (int)(Math.Cos(angleRad) * 10000);
        int y = (int)(Math.Sin(angleRad) * 10000);
        constant.direction.dir0 = x;
        constant.direction.dir1 = y;
        constant.direction.dir2 = 0;
        
        constant.length = (ushort)duration;
        constant.level = (short)magnitude;
        constant.attack_length = 0;
        constant.fade_length = (ushort)(duration / 4);
        
        effect.constant = constant;
        
        return SDL2.SDL_HapticNewEffect(_hapticDevice, ref effect);
    }
    
    /// <summary>
    /// Create a periodic (rumble/vibration) effect - perfect for lasers!
    /// </summary>
    /// <param name="magnitude">Strength 0-32767</param>
    /// <param name="duration">Duration in milliseconds</param>
    /// <param name="frequency">Frequency in Hz (10-100, lower = deeper rumble)</param>
    /// <param name="attackMs">Attack/fade-in time in ms</param>
    /// <param name="fadeMs">Fade-out time in ms</param>
    public int CreatePeriodicEffect(int magnitude, int duration, int frequency = 30, int attackMs = 50, int fadeMs = 100)
    {
        if (_hapticDevice == IntPtr.Zero)
            return -1;
        
        var effect = new SDL2.SDL_HapticEffect();
        effect.type = SDL2.SDL_HAPTIC_SINE;
        
        var periodic = new SDL2.SDL_HapticPeriodic();
        periodic.type = SDL2.SDL_HAPTIC_SINE;
        periodic.direction.type = SDL2.SDL_HAPTIC_CARTESIAN;
        periodic.direction.dir0 = 1000;  // X direction
        periodic.direction.dir1 = 0;     // Y direction
        periodic.direction.dir2 = 0;     // Z direction
        
        periodic.length = (uint)duration;
        periodic.delay = 0;
        periodic.button = 0;
        periodic.interval = 0;
        
        periodic.period = (ushort)(1000 / frequency); // Convert Hz to period in ms
        periodic.magnitude = (short)magnitude;
        periodic.offset = 0;
        periodic.phase = 0;
        
        // Envelope for smooth attack and fade
        periodic.attack_length = (ushort)attackMs;
        periodic.attack_level = (ushort)(magnitude / 2);
        periodic.fade_length = (ushort)fadeMs;
        periodic.fade_level = 0;
        
        effect.periodic = periodic;
        
        int effectId = SDL2.SDL_HapticNewEffect(_hapticDevice, ref effect);
        return effectId;
    }
    
    /// <summary>
    /// Create a constant effect with custom envelope - good for impacts with punch!
    /// </summary>
    /// <param name="magnitude">Peak strength 0-32767</param>
    /// <param name="duration">Total duration in milliseconds</param>
    /// <param name="attackMs">Attack/rise time in ms</param>
    /// <param name="fadeMs">Fade/decay time in ms</param>
    /// <param name="direction">Direction 0-35999 (0=North, 9000=East, etc)</param>
    public int CreateImpactEffect(int magnitude, int duration, int attackMs = 20, int fadeMs = 150, int direction = 0)
    {
        if (_hapticDevice == IntPtr.Zero)
            return -1;
        
        var effect = new SDL2.SDL_HapticEffect();
        effect.type = SDL2.SDL_HAPTIC_CONSTANT;
        
        var constant = new SDL2.SDL_HapticConstant();
        constant.type = SDL2.SDL_HAPTIC_CONSTANT;
        constant.direction.type = SDL2.SDL_HAPTIC_CARTESIAN;
        
        // Convert direction
        double angleRad = direction * Math.PI / 18000.0;
        constant.direction.dir0 = (int)(Math.Cos(angleRad) * 1000);
        constant.direction.dir1 = (int)(Math.Sin(angleRad) * 1000);
        constant.direction.dir2 = 0;
        
        constant.length = (uint)duration;
        constant.delay = 0;
        constant.button = 0;
        constant.interval = 0;
        constant.level = (short)magnitude;
        
        // Sharp attack, longer decay for impact feel
        constant.attack_length = (ushort)attackMs;
        constant.attack_level = (ushort)(magnitude / 3);
        constant.fade_length = (ushort)fadeMs;
        constant.fade_level = 0;
        
        effect.constant = constant;
        
        int effectId = SDL2.SDL_HapticNewEffect(_hapticDevice, ref effect);
        return effectId;
    }
    
    /// <summary>
    /// Create a combined explosion effect - impact + rumble vibration with separate controls
    /// Returns array of effect IDs [impact, rumble]
    /// </summary>
    public int[] CreateExplosionEffect(int baseMagnitude, int impactDuration, int rumbleDuration, int impactAttackMs, int impactFadeMs, int rumbleAttackMs, int rumbleFadeMs, int direction, int rumbleFreq, float impactMultiplier, float rumbleMultiplier)
    {
        var effectIds = new int[2];
        
        // Create impact effect - apply multiplier directly to base magnitude
        // 100% (1.0) = full base magnitude, capped at 32767
        int impactMag = (int)(baseMagnitude * impactMultiplier);
        impactMag = Math.Clamp(impactMag, 0, 32767);
        effectIds[0] = CreateImpactEffect(impactMag, impactDuration, impactAttackMs, impactFadeMs, direction);
        
        // Create rumble effect - also apply multiplier to base magnitude (independent from impact)
        // 100% (1.0) = full base magnitude, capped at 32767
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
        if (_hapticDevice == IntPtr.Zero || effectId < 0)
            return false;
        
        return SDL2.SDL_HapticRunEffect(_hapticDevice, effectId, (uint)iterations) == 0;
    }
    
    /// <summary>
    /// Stop an effect
    /// </summary>
    public void StopEffect(int effectId)
    {
        if (_hapticDevice != IntPtr.Zero && effectId >= 0)
        {
            SDL2.SDL_HapticStopEffect(_hapticDevice, effectId);
        }
    }
    
    /// <summary>
    /// Destroy an effect
    /// </summary>
    public void DestroyEffect(int effectId)
    {
        if (_hapticDevice != IntPtr.Zero && effectId >= 0)
        {
            SDL2.SDL_HapticDestroyEffect(_hapticDevice, effectId);
        }
    }
    
    /// <summary>
    /// Stop all effects
    /// </summary>
    public void StopAllEffects()
    {
        if (_hapticDevice != IntPtr.Zero)
        {
            SDL2.SDL_HapticStopAll(_hapticDevice);
        }
    }
    
    private void ReleaseDevice()
    {
        if (_hapticDevice != IntPtr.Zero)
        {
            SDL2.SDL_HapticClose(_hapticDevice);
            _hapticDevice = IntPtr.Zero;
        }
        
        if (_joystick != IntPtr.Zero)
        {
            SDL2.SDL_JoystickClose(_joystick);
            _joystick = IntPtr.Zero;
        }
        
        _selectedDeviceIndex = -1;
    }
    
    public void Dispose()
    {
        ReleaseDevice();
        
        if (IsInitialized)
        {
            SDL2.SDL_Quit();
            IsInitialized = false;
        }
    }
}
