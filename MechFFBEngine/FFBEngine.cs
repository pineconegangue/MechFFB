using MechFFBReader;
using MechFFBReader.Events;
using MechFFBEngine.Configuration;
using MechFFBEngine.Haptic;

namespace MechFFBEngine;

/// <summary>
/// Main FFB engine - coordinates telemetry reading and force feedback output
/// Uses SDL2 (same as DCS) - proven to work with MOZA AB9!
/// </summary>
public class FFBEngine : IDisposable
{
    private readonly TelemetryReader _telemetryReader;
    private readonly FFBConfiguration _configuration;
    private SDL2HapticManager _hapticManager;
    
    // Track state for alternating effects
    private bool _isLeftFoot = true;
    private int _activeJumpJetEffectId = -1;
    private System.Threading.Timer? _machineGunTimer = null;
    private bool _machineGunsActive = false;
    
    // Streak missile detection and effect cleanup
    private DateTime _lastMissileTime = DateTime.MinValue;
    private const int STREAK_DETECTION_WINDOW_MS = 150; // If missiles fire within 150ms, consider it a streak
    private List<int> _activeStreakEffects = new List<int>(); // Track active streak effects for cleanup
    
    public bool IsRunning { get; private set; }
    public FFBConfiguration Configuration => _configuration;
    
    // Events for UI updates
    public event EventHandler<string>? OnStatusChanged;
    public event EventHandler<string>? OnError;
    
    public FFBEngine()
    {
        Console.WriteLine("=== MechFFB Engine Initializing ===");
        _telemetryReader = new TelemetryReader();
        Console.WriteLine("TelemetryReader created");
        _configuration = FFBConfiguration.Load(); // Load saved settings
        Console.WriteLine("Configuration loaded");
        _hapticManager = new SDL2HapticManager();
        Console.WriteLine("SDL2HapticManager created");
    }
    
    /// <summary>
    /// Initialize SDL2
    /// </summary>
    public bool Initialize()
    {
        try
        {
            if (!_hapticManager.Initialize())
            {
                OnError?.Invoke(this, "SDL2 initialization failed. Make sure SDL2.dll is present.");
                return false;
            }
            OnStatusChanged?.Invoke(this, "SDL2 initialized - ready for FFB!");
            return true;
        }
        catch (DllNotFoundException)
        {
            OnError?.Invoke(this, "SDL2.dll not found! Run Download-SDL2.ps1");
            return false;
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"Initialization error: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Get list of available FFB devices
    /// </summary>
    public List<SDL2HapticManager.HapticDeviceInfo> GetAvailableDevices()
    {
        return _hapticManager.GetHapticDevices();
    }
    
    /// <summary>
    /// Select device by index
    /// </summary>
    public bool SelectDevice(int deviceIndex)
    {
        try
        {
            if (_hapticManager.SelectDevice(deviceIndex))
            {
                OnStatusChanged?.Invoke(this, "FFB device selected successfully!");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"Device selection error: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Test the current device with a simple effect
    /// </summary>
    public void TestDevice()
    {
        try
        {
            OnStatusChanged?.Invoke(this, "Running test effect...");
            
            if (_hapticManager.TestDevice())
            {
                OnStatusChanged?.Invoke(this, "Test effect completed - you should have felt it!");
            }
            else
            {
                OnError?.Invoke(this, "Test effect failed - check console for details");
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"Test failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Start the FFB engine - begins reading telemetry and generating effects
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;
        
        if (!_hapticManager.HasDeviceSelected)
        {
            OnError?.Invoke(this, "No FFB device selected");
            return;
        }
        
        _telemetryReader.OnWeaponFire += HandleWeaponFire;
        _telemetryReader.OnDamage += HandleDamage;
        _telemetryReader.OnMovement += HandleMovement;
        _telemetryReader.OnJumpJet += HandleJumpJet;
        _telemetryReader.OnImpact += HandleImpact;
        _telemetryReader.Start();
        
        IsRunning = true;
        OnStatusChanged?.Invoke(this, "FFB engine started - waiting for MW5 telemetry...");
    }
    
    /// <summary>
    /// Stop the FFB engine
    /// </summary>
    public void Stop()
    {
        if (!IsRunning) return;
        
        IsRunning = false;
        _telemetryReader.Stop();
        _telemetryReader.OnWeaponFire -= HandleWeaponFire;
        _telemetryReader.OnDamage -= HandleDamage;
        _telemetryReader.OnMovement -= HandleMovement;
        _telemetryReader.OnJumpJet -= HandleJumpJet;
        _telemetryReader.OnImpact -= HandleImpact;
        
        // Clean up any active continuous effects
        if (_activeJumpJetEffectId >= 0)
        {
            _hapticManager.StopEffect(_activeJumpJetEffectId);
            _hapticManager.DestroyEffect(_activeJumpJetEffectId);
            _activeJumpJetEffectId = -1;
        }
        
        // Stop machine gun timer
        _machineGunsActive = false;
        _machineGunTimer?.Dispose();
        _machineGunTimer = null;
        
        // Clean up streak missile effects
        foreach (var effectId in _activeStreakEffects)
        {
            _hapticManager.DestroyEffect(effectId);
        }
        _activeStreakEffects.Clear();
        
        _hapticManager.StopAllEffects();
        
        OnStatusChanged?.Invoke(this, "FFB engine stopped");
    }
    
    private void HandleWeaponFire(object? sender, WeaponFireEvent e)
    {
        int magnitude = CalculateRecoilMagnitude(e);
        int effectId = -1;
        
        // Use different effect types for different weapons
        switch (e.WeaponClass)
        {
            case WeaponClass.Ballistic:
                // Sharp impact pushing to the right
                // 9000° = East = right direction
                var ballistic = _configuration.Advanced.Ballistics;
                effectId = _hapticManager.CreateImpactEffect(magnitude, ballistic.Duration, ballistic.AttackTime, ballistic.FadeTime, 9000);
                break;
                
            case WeaponClass.Energy:
                if (e.IsPPC)
                {
                    // PPC - heavy single impact pushing to the right
                    // 9000° = East = right direction
                    var ppc = _configuration.Advanced.PPCs;
                    effectId = _hapticManager.CreateImpactEffect(magnitude, ppc.Duration, ppc.AttackTime, ppc.FadeTime, 9000);
                }
                else if (e.IsMachineGun)
                {
                    // Machine gun - continuous rapid pulses while firing
                    if (e.IsActive && !_machineGunsActive)
                    {
                        // Start continuous firing
                        _machineGunsActive = true;
                        var mg = _configuration.Advanced.MachineGuns;
                        
                        // Fire immediately
                        FireMachineGunPulse(magnitude, mg);
                        
                        // Set up timer to fire every 93ms
                        _machineGunTimer = new System.Threading.Timer(_ => 
                        {
                            if (_machineGunsActive)
                            {
                                FireMachineGunPulse(magnitude, mg);
                            }
                        }, null, 93, 93);
                        
                        Console.WriteLine("Machine Gun: START continuous fire");
                    }
                    else if (!e.IsActive && _machineGunsActive)
                    {
                        // Stop continuous firing
                        _machineGunsActive = false;
                        _machineGunTimer?.Dispose();
                        _machineGunTimer = null;
                        Console.WriteLine("Machine Gun: STOP");
                    }
                    return; // Don't create single effect
                }
                else
                {
                    // Laser - continuous rumble/vibration using actual beam duration from game
                    var laser = _configuration.Advanced.Lasers;
                    
                    // Convert beam duration from seconds to milliseconds
                    int beamDurationMs = (int)(e.BeamDuration * 1000);
                    
                    // Use game's beam duration if available, otherwise fall back to config
                    int duration = beamDurationMs > 0 ? beamDurationMs : laser.Duration;
                    
                    Console.WriteLine($"Laser: BeamDuration={e.BeamDuration:F3}s ({duration}ms)");
                    effectId = _hapticManager.CreatePeriodicEffect(magnitude, duration, laser.Frequency, laser.AttackTime, laser.FadeTime);
                }
                break;
                
            case WeaponClass.Missile:
                {
                    var missile = _configuration.Advanced.Missiles;
                    var now = DateTime.Now;
                    
                    // Detect if this is part of a streak volley
                    bool isInStreakWindow = (now - _lastMissileTime).TotalMilliseconds < STREAK_DETECTION_WINDOW_MS;
                    _lastMissileTime = now;
                    
                    // Calculate duration based on firing delay
                    int duration;
                    bool isStreak = e.FiringDelay > 0.01f;
                    
                    if (isStreak)
                    {
                        // Streak missiles: Calculate total firing duration
                        // Duration = Delay × (MissileCount - 1)
                        int missileCount = (int)e.ProjectileMass;
                        float totalFiringTime = e.FiringDelay * (missileCount - 1);
                        duration = (int)(totalFiringTime * 1000); // Convert to ms
                        Console.WriteLine($"STREAK MISSILES: {missileCount} missiles, Delay={e.FiringDelay:F3}s, Duration={duration}ms, InVolley={isInStreakWindow}");
                        
                        // Clean up old streak effects from this volley to prevent stacking
                        if (isInStreakWindow && _activeStreakEffects.Count > 0)
                        {
                            // Remove oldest effect to prevent buildup (keep only last 2-3)
                            if (_activeStreakEffects.Count > 2)
                            {
                                int oldEffect = _activeStreakEffects[0];
                                _activeStreakEffects.RemoveAt(0);
                                _hapticManager.DestroyEffect(oldEffect);
                            }
                        }
                        else if (!isInStreakWindow)
                        {
                            // New volley starting, clean up all previous streak effects
                            foreach (var oldEffect in _activeStreakEffects)
                            {
                                _hapticManager.DestroyEffect(oldEffect);
                            }
                            _activeStreakEffects.Clear();
                        }
                    }
                    else
                    {
                        // Standard missiles: Use configured duration
                        duration = missile.Duration;
                        int numMissiles = (int)e.ProjectileMass;
                        Console.WriteLine($"STANDARD MISSILES: {numMissiles} missiles, Duration={duration}ms (instant volley)");
                    }
                    
                    // Apply rumble multiplier to magnitude (100% = 32767 max)
                    int rumbleMag = (int)(magnitude * missile.RumbleMultiplier);
                    rumbleMag = Math.Clamp(rumbleMag, 0, 32767);
                    
                    effectId = _hapticManager.CreatePeriodicEffect(rumbleMag, duration, missile.Frequency, missile.AttackTime, missile.FadeTime);
                    
                    // Track streak effects for cleanup
                    if (isStreak && effectId >= 0)
                    {
                        _activeStreakEffects.Add(effectId);
                    }
                }
                break;
                
            case WeaponClass.Melee:
                // Massive thud with very sharp attack
                var melee = _configuration.Advanced.Melee;
                effectId = _hapticManager.CreateImpactEffect(magnitude, melee.Duration, melee.AttackTime, melee.FadeTime);
                break;
                
            default:
                effectId = _hapticManager.CreateConstantEffect(magnitude, 150);
                break;
        }
        
        if (effectId >= 0)
        {
            _hapticManager.PlayEffect(effectId, 1);
            int cleanup = e.WeaponClass == WeaponClass.Energy && e.IsMachineGun 
                ? _configuration.Advanced.MachineGuns.Duration 
                : CalculateRecoilDuration(e);
            Task.Delay(cleanup + 100).ContinueWith(_ => _hapticManager.DestroyEffect(effectId));
        }
    }
    
    private void HandleDamage(object? sender, DamageEvent e)
    {
        Console.WriteLine($"DAMAGE: Type={e.DamageType}, Amt={e.DamageAmount:F1}");
        
        // Get intensity based on damage type
        float damageTypeIntensity = e.DamageType switch
        {
            DamageType.Trace => _configuration.Simple.LaserDamageIntensity,
            DamageType.Projectile => _configuration.Simple.BallisticDamageIntensity,
            DamageType.Missile => _configuration.Simple.MissileDamageIntensity,
            DamageType.Melee => _configuration.Simple.MeleeDamageIntensity,
            DamageType.Explosion => _configuration.Simple.ExplosionDamageIntensity,
            _ => 0.6f
        };
        
        // Scale based on actual damage amount
        // Damage values typically range from 0.5 (single small laser tick) to 100+ (massive hits)
        // Scale so ~20 damage = ~30000 magnitude at 100% intensity
        float baseMag = e.DamageAmount * 1500; // Maps 20 damage to ~30000
        int magnitude = (int)(baseMag * damageTypeIntensity * _configuration.MasterIntensity);
        magnitude = Math.Clamp(magnitude, 0, 32767);
        
        Console.WriteLine($"Damage FFB: Amt={e.DamageAmount:F1} -> Mag={magnitude}");
        
        int effectId = -1;
        
        // Different feel based on damage type
        switch (e.DamageType)
        {
            case DamageType.Trace: // Lasers, flamers
                // Burning/searing rumble
                var laserDmg = _configuration.Advanced.LaserDamage;
                effectId = _hapticManager.CreatePeriodicEffect(magnitude, laserDmg.Duration, laserDmg.Frequency, laserDmg.AttackTime, laserDmg.FadeTime);
                break;
                
            case DamageType.Projectile: // Autocannons
                // Sharp impact (no direction)
                var ballisticDmg = _configuration.Advanced.BallisticDamage;
                effectId = _hapticManager.CreateImpactEffect(magnitude, ballisticDmg.Duration, ballisticDmg.AttackTime, ballisticDmg.FadeTime, 9000);
                break;
                
            case DamageType.Missile:
                // Explosive impact (no direction)
                var missileDmg = _configuration.Advanced.MissileDamage;
                effectId = _hapticManager.CreateImpactEffect(magnitude, missileDmg.Duration, missileDmg.AttackTime, missileDmg.FadeTime, 9000);
                break;
                
            case DamageType.Melee:
                // Crushing blow (no direction)
                var meleeDmg = _configuration.Advanced.MeleeDamage;
                effectId = _hapticManager.CreateImpactEffect(magnitude, meleeDmg.Duration, meleeDmg.AttackTime, meleeDmg.FadeTime, 9000);
                break;
                
            case DamageType.Explosion:
                // Big boom with separate impact + rumble controls
                var explosionDmg = _configuration.Advanced.ExplosionDamageAdvanced;
                
                // Create combined effect with separate settings for each
                int[] explosionEffects = _hapticManager.CreateExplosionEffect(
                    magnitude+10000, 
                    explosionDmg.ImpactDuration,
                    explosionDmg.RumbleDuration,
                    explosionDmg.ImpactAttackTime, 
                    explosionDmg.ImpactFadeTime,
                    explosionDmg.RumbleAttackTime,
                    explosionDmg.RumbleFadeTime,
                    0, // No direction
                    explosionDmg.RumbleFrequency,
                    explosionDmg.ImpactMultiplier,
                    explosionDmg.RumbleMultiplier
                );
                
                // Play both effects simultaneously
                if (explosionEffects[0] >= 0 && explosionEffects[1] >= 0)
                {
                    _hapticManager.PlayEffect(explosionEffects[0], 1); // Impact
                    _hapticManager.PlayEffect(explosionEffects[1], 1); // Rumble
                    
                    // Clean up both effects after the longer of the two durations
                    int maxDuration = Math.Max(explosionDmg.ImpactDuration, explosionDmg.RumbleDuration);
                    Task.Delay(maxDuration + 100).ContinueWith(_ => 
                    {
                        _hapticManager.DestroyEffect(explosionEffects[0]);
                        _hapticManager.DestroyEffect(explosionEffects[1]);
                    });
                }
                return; // Skip the default single effect creation
                
            default:
                effectId = _hapticManager.CreateConstantEffect(magnitude, 250, 0);
                break;
        }
        
        if (effectId >= 0)
        {
            _hapticManager.PlayEffect(effectId, 1);
            Task.Delay(600).ContinueWith(_ => _hapticManager.DestroyEffect(effectId));
        }
        else
        {
            Console.WriteLine("Failed to create damage effect!");
        }
    }
    
    private void HandleMovement(object? sender, MovementEvent e)
    {
        // Only handle footsteps
        if (e.Type != MovementType.Footstep)
            return;
        
        // Alternate feet
        _isLeftFoot = !_isLeftFoot;
        
        // Push/pull on X axis (forward/backward)
        // 0 = North (push forward), 18000 = South (pull backward)
        int direction = _isLeftFoot ? 0 : 18000;
        
        Console.WriteLine($"FOOTSTEP: {(_isLeftFoot ? "PUSH" : "PULL")}, Tonnage={e.MechTonnage}, Speed={e.Velocity:F1}");
        
        // Max out at 32000, scaled by footstep intensity
        float baseMag = 32767;
        int magnitude = (int)(baseMag * _configuration.Simple.FootstepIntensity * _configuration.MasterIntensity);
        magnitude = Math.Clamp(magnitude, 0, 32767);
        
        Console.WriteLine($"Footstep FFB: Mag={magnitude}, Dir={direction}");
        
        // Sharp impact with direction
        var footstep = _configuration.Advanced.Footsteps;
        int effectId = _hapticManager.CreateImpactEffect(magnitude, footstep.Duration, footstep.AttackTime, footstep.FadeTime, direction);
        if (effectId >= 0)
        {
            _hapticManager.PlayEffect(effectId, 1);
            Task.Delay(footstep.Duration + 50).ContinueWith(_ => _hapticManager.DestroyEffect(effectId));
        }
    }
    
    private void HandleJumpJet(object? sender, JumpJetEvent e)
    {
        Console.WriteLine($"JUMP JET: State={e.State}");
        
        if (e.State == JumpJetState.Active)
        {
            // Start continuous vibration if not already active
            if (_activeJumpJetEffectId < 0)
            {
                // Max out at 32000
                float baseMag = 32767;
                int magnitude = (int)(baseMag * _configuration.Simple.JumpJetIntensity * _configuration.MasterIntensity);
                magnitude = Math.Clamp(magnitude, 0, 32767);
                
                Console.WriteLine($"Jump Jet START: Mag={magnitude}");
                
                // Continuous low-frequency rumble
                var jumpJet = _configuration.Advanced.JumpJets;
                _activeJumpJetEffectId = _hapticManager.CreatePeriodicEffect(magnitude, 10000, jumpJet.Frequency, jumpJet.AttackTime, jumpJet.FadeTime);
                if (_activeJumpJetEffectId >= 0)
                {
                    _hapticManager.PlayEffect(_activeJumpJetEffectId, 1);
                }
            }
        }
        else
        {
            // Stop the continuous effect
            if (_activeJumpJetEffectId >= 0)
            {
                Console.WriteLine($"Jump Jet STOP");
                _hapticManager.StopEffect(_activeJumpJetEffectId);
                _hapticManager.DestroyEffect(_activeJumpJetEffectId);
                _activeJumpJetEffectId = -1;
            }
        }
    }
    
    private void HandleImpact(object? sender, ImpactEvent e)
    {
        Console.WriteLine($"LANDING: Velocity={e.ImpactVelocity:F1}, Tonnage={e.MechTonnage}");
        
        // Max out at 32000
        float baseMag = 32767;
        int magnitude = (int)(baseMag * _configuration.Simple.LandingIntensity * _configuration.MasterIntensity);
        magnitude = Math.Clamp(magnitude, 0, 32767);
        
        Console.WriteLine($"Landing FFB: Mag={magnitude}");
        
        var landing = _configuration.Advanced.Landing;
        int effectId = _hapticManager.CreateImpactEffect(magnitude, landing.Duration, landing.AttackTime, landing.FadeTime);
        if (effectId >= 0)
        {
            _hapticManager.PlayEffect(effectId, 1);
            Task.Delay(landing.Duration + 100).ContinueWith(_ => _hapticManager.DestroyEffect(effectId));
        }
    }
    
    private void FireMachineGunPulse(int magnitude, MachineGunSettings mg)
    {
        int effectId = _hapticManager.CreatePeriodicEffect(magnitude, mg.Duration, mg.Frequency, mg.AttackTime, mg.FadeTime);
        if (effectId >= 0)
        {
            _hapticManager.PlayEffect(effectId, 1);
            Task.Delay(mg.Duration + 20).ContinueWith(_ => _hapticManager.DestroyEffect(effectId));
        }
    }
    
    private int CalculateRecoilMagnitude(WeaponFireEvent e)
    {
        float baseMag = 0;
        float weaponIntensity = 1.0f;
        
        // Scale based on weapon data for realistic differences
        switch (e.WeaponClass)
        {
            case WeaponClass.Ballistic:
                // Autocannons - scale based on impulse + strong baseline
                // Impulse values: AC/2 ~50-100, AC/5 ~150-250, AC/10 ~300-500, AC/20 ~800-1500
                // Scale to use full range at 100% intensity, with +10000 baseline for stronger feel
                baseMag = (e.Damage * 20) + 10000; // +10k baseline makes all ballistics feel more impactful
                weaponIntensity = _configuration.Simple.BallisticIntensity;
                break;
                
            case WeaponClass.Energy:
                if (e.IsPPC)
                {
                    // PPCs - scale based on damage + strong baseline
                    baseMag = (e.Damage * 15) + 10000;
                    weaponIntensity = _configuration.Simple.PPCIntensity;
                }
                else
                {
                    // Lasers - scale based on damage + baseline
                    baseMag = (e.Damage * 18) + 10000;
                    weaponIntensity = _configuration.Simple.LaserIntensity;
                }
                break;
                
            case WeaponClass.Missile:
                // Missiles - scale based on impulse + strong baseline
                // Single missiles ~500-1000, volleys scale with count
                baseMag = (e.Damage * 15) + 20000; // +20k baseline for strong rumble
                weaponIntensity = _configuration.Simple.MissileIntensity;
                break;
                
            case WeaponClass.Melee:
                // Melee - scale based on tonnage/damage + baseline
                baseMag = (e.Damage * 12) + 20000;
                weaponIntensity = _configuration.Simple.MeleeIntensity;
                break;
        }
        
        // Apply weapon-specific and master intensity scaling
        baseMag *= weaponIntensity * _configuration.MasterIntensity;
        
        // Only clamp to SDL2 max
        int magnitude = Math.Clamp((int)baseMag, 0, 32767);
        
        string weaponType = e.IsPPC ? "PPC" : e.WeaponClass.ToString();
        Console.WriteLine($"Weapon: {weaponType}, RawDamage={e.Damage:F1}, Intensity={weaponIntensity:F2}, FinalMag={magnitude}");
        
        return magnitude;
    }
    
    private int CalculateRecoilDuration(WeaponFireEvent e)
    {
        // Return actual duration + fade time from advanced settings
        return e.WeaponClass switch
        {
            WeaponClass.Ballistic => _configuration.Advanced.Ballistics.Duration + _configuration.Advanced.Ballistics.FadeTime,
            WeaponClass.Energy when e.IsPPC => _configuration.Advanced.PPCs.Duration + _configuration.Advanced.PPCs.FadeTime,
            WeaponClass.Energy when e.IsMachineGun => _configuration.Advanced.MachineGuns.Duration + _configuration.Advanced.MachineGuns.FadeTime,
            WeaponClass.Energy => (int)(e.BeamDuration * 1000) + _configuration.Advanced.Lasers.FadeTime, // Use beam duration from game + fade
            WeaponClass.Missile => CalculateMissileDuration(e) + _configuration.Advanced.Missiles.FadeTime, // Dynamic duration based on firing delay
            WeaponClass.Melee => _configuration.Advanced.Melee.Duration + _configuration.Advanced.Melee.FadeTime,
            _ => 200
        };
    }
    
    private int CalculateMissileDuration(WeaponFireEvent e)
    {
        // Calculate missile rumble duration based on firing delay
        if (e.FiringDelay > 0.01f)
        {
            // Streak missiles: Duration = Delay × (MissileCount - 1)
            int missileCount = (int)e.ProjectileMass;
            float totalFiringTime = e.FiringDelay * (missileCount - 1);
            return (int)(totalFiringTime * 1000);
        }
        else
        {
            // Standard missiles: Use configured duration
            return _configuration.Advanced.Missiles.Duration;
        }
    }
    
    private int CalculateDirection(System.Numerics.Vector3 hitDir)
    {
        double angle = Math.Atan2(hitDir.Y, hitDir.X);
        int degrees = (int)(angle * 18000.0 / Math.PI);
        if (degrees < 0) degrees += 36000;
        return degrees;
    }
    
    public void Dispose()
    {
        Stop();
        _hapticManager?.Dispose();
        _telemetryReader?.Dispose();
    }
}
