using MechFFBReader;
using MechFFBReader.Events;
using MechFFBEngine.Configuration;
using MechFFBEngine.Haptic;

namespace MechFFBEngine;

/// <summary>
/// Main FFB engine - coordinates telemetry reading and force feedback output
/// Now using DirectInput for VPForce Rhino compatibility!
/// </summary>
public class FFBEngine : IDisposable
{
    private readonly TelemetryReader _telemetryReader;
    private readonly FFBConfiguration _configuration;
    private DirectInputHapticManager _hapticManager;
    
    // Track state for alternating effects
    private bool _isLeftFoot = true;
    private int _activeJumpJetEffectId = -1;
    private System.Threading.Timer? _machineGunTimer = null;
    private bool _machineGunsActive = false;
    private int _currentMachineGunMagnitude = 0; // Current magnitude for active machine gun firing
    
    // Streak missile detection and effect cleanup
    private DateTime _lastMissileTime = DateTime.MinValue;
    private const int STREAK_DETECTION_WINDOW_MS = 150;
    private List<int> _activeStreakEffects = new List<int>();
    
    public bool IsRunning { get; private set; }
    public FFBConfiguration Configuration => _configuration;
    
    // Events for UI updates
    public event EventHandler<string>? OnStatusChanged;
    public event EventHandler<string>? OnError;
    
    public FFBEngine()
    {
        Console.WriteLine("=== MechFFB Engine Initializing (DirectInput) ===");
        _telemetryReader = new TelemetryReader();
        Console.WriteLine("TelemetryReader created");
        _configuration = FFBConfiguration.Load();
        Console.WriteLine("Configuration loaded");
        _hapticManager = new DirectInputHapticManager();
        Console.WriteLine("DirectInputHapticManager created");
    }
    
    /// <summary>
    /// Set window handle for exclusive device access (optional but recommended)
    /// </summary>
    public void SetWindowHandle(IntPtr windowHandle)
    {
        _hapticManager.WindowHandle = windowHandle;
        Console.WriteLine($"Window handle set: {windowHandle}");
    }
    
    /// <summary>
    /// Invert force direction (for devices with opposite axis conventions)
    /// </summary>
    public void SetInvertDirection(bool invert)
    {
        _hapticManager.InvertDirection = invert;
        Console.WriteLine($"Direction inversion: {(invert ? "ENABLED" : "DISABLED")}");
    }
    
    /// <summary>
    /// Initialize DirectInput
    /// </summary>
    public bool Initialize()
    {
        try
        {
            if (!_hapticManager.Initialize())
            {
                OnError?.Invoke(this, "DirectInput initialization failed.");
                return false;
            }
            OnStatusChanged?.Invoke(this, "DirectInput initialized - ready for FFB!");
            return true;
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
    public List<DirectInputHapticManager.HapticDeviceInfo> GetAvailableDevices()
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
    
    // All the Handle* methods remain exactly the same as the original
    // Just using DirectInputHapticManager instead of SDL2HapticManager
    
    private void HandleWeaponFire(object? sender, WeaponFireEvent e)
    {
        int magnitude = CalculateRecoilMagnitude(e);
        int effectId = -1;
        
        switch (e.WeaponClass)
        {
            case WeaponClass.Ballistic:
                var ballistic = _configuration.Advanced.Ballistics;
                effectId = _hapticManager.CreateImpactEffect(magnitude, ballistic.Duration, ballistic.AttackTime, ballistic.FadeTime, 9000);
                break;
                
            case WeaponClass.Energy:
                if (e.IsPPC)
                {
                    var ppc = _configuration.Advanced.PPCs;
                    effectId = _hapticManager.CreateImpactEffect(magnitude, ppc.Duration, ppc.AttackTime, ppc.FadeTime, 9000);
                }
                else if (e.IsMachineGun)
                {
                    // Machine gun - continuous rapid pulses while firing
                    if (e.IsActive)
                    {
                        var mg = _configuration.Advanced.MachineGuns;
                        
                        // Always update magnitude - allows slider changes to take effect immediately
                        _currentMachineGunMagnitude = magnitude;
                        
                        if (!_machineGunsActive || _machineGunTimer == null)
                        {
                            // Start or restart continuous firing
                            _machineGunsActive = true;
                            
                            // Clean up old timer if it exists
                            _machineGunTimer?.Dispose();
                            
                            // Fire immediately
                            FireMachineGunPulse(_currentMachineGunMagnitude, mg);
                            
                            // Set up timer to fire every 93ms
                            _machineGunTimer = new System.Threading.Timer(_ => 
                            {
                                if (_machineGunsActive)
                                {
                                    // Use current magnitude from field (updates with slider changes)
                                    FireMachineGunPulse(_currentMachineGunMagnitude, _configuration.Advanced.MachineGuns);
                                }
                            }, null, 93, 93);
                            
                            Console.WriteLine($"Machine Gun: START/RESTART continuous fire, magnitude={_currentMachineGunMagnitude}");
                        }
                        // else: Already firing, magnitude updated above
                    }
                    else if (!e.IsActive && _machineGunsActive)
                    {
                        // Stop continuous firing
                        _machineGunsActive = false;
                        _machineGunTimer?.Dispose();
                        _machineGunTimer = null;
                        _currentMachineGunMagnitude = 0;
                        Console.WriteLine("Machine Gun: STOP");
                    }
                    return; // Don't create single effect
                }
                else
                {
                    var laser = _configuration.Advanced.Lasers;
                    int duration = (int)(e.BeamDuration * 1000);
                    effectId = _hapticManager.CreatePeriodicEffect(magnitude, duration, laser.Frequency, laser.AttackTime, laser.FadeTime);
                }
                break;
                
            case WeaponClass.Missile:
                var missile = _configuration.Advanced.Missiles;
                DateTime now = DateTime.Now;
                
                if (e.FiringDelay > 0.01f)
                {
                    // Streak missiles - individual effects per missile
                    int missileCount = (int)e.ProjectileMass;
                    float delayBetweenMissiles = e.FiringDelay * 1000;
                    
                    for (int i = 0; i < missileCount; i++)
                    {
                        int delay = (int)(i * delayBetweenMissiles);
                        Task.Delay(delay).ContinueWith(_ =>
                        {
                            int streakEffectId = _hapticManager.CreatePeriodicEffect(magnitude, missile.Duration, missile.Frequency, missile.AttackTime, missile.FadeTime);
                            if (streakEffectId >= 0)
                            {
                                _activeStreakEffects.Add(streakEffectId);
                                _hapticManager.PlayEffect(streakEffectId, 1);
                                Task.Delay(missile.Duration + missile.FadeTime + 50).ContinueWith(__ =>
                                {
                                    _hapticManager.DestroyEffect(streakEffectId);
                                    _activeStreakEffects.Remove(streakEffectId);
                                });
                            }
                        });
                    }
                    return;
                }
                else
                {
                    // Standard missiles - single effect
                    effectId = _hapticManager.CreatePeriodicEffect(magnitude, missile.Duration, missile.Frequency, missile.AttackTime, missile.FadeTime);
                }
                break;
                
            case WeaponClass.Melee:
                var melee = _configuration.Advanced.Melee;
                effectId = _hapticManager.CreateImpactEffect(magnitude, melee.Duration, melee.AttackTime, melee.FadeTime, 9000);
                break;
        }
        
        if (effectId >= 0)
        {
            _hapticManager.PlayEffect(effectId, 1);
            int duration = CalculateRecoilDuration(e);
            Task.Delay(duration + 50).ContinueWith(_ => _hapticManager.DestroyEffect(effectId));
        }
    }
    
    private void HandleDamage(object? sender, DamageEvent e)
    {
        // NOTE: Game does not provide actual hit direction data - Float1/2/3 are always (0,1,0) or (0,0.75,0)
        // Using fixed direction (9000° = back pull, feels like forward push) for all impact damage
        int direction = 9000; // Fixed: Always pull back (feels like being pushed forward)
        
        // Debug logging (can be removed after confirming)
        // Console.WriteLine($"DAMAGE DEBUG: HitDir=({e.HitDirection.X:F2}, {e.HitDirection.Y:F2}, {e.HitDirection.Z:F2}), Using fixed={direction}°");
        
        // Scale damage similar to weapons - need baseline + scaling for small damage values
        // Damage amounts are typically 0.1 to 50+
        // Use aggressive scaling: (damage * 500) + 10000 baseline for good feel
        float baseMag = (e.DamageAmount * 500) + 10000;
        int magnitude = Math.Clamp((int)(baseMag * _configuration.MasterIntensity), 0, 32767);
        
        Console.WriteLine($"DAMAGE: Type={(DamageType)((int)e.DamageType)}, Amount={e.DamageAmount:F1}, Dir={direction}°, Mag={magnitude}");
        
        int effectId = -1;
        
        if ((int)e.DamageType == 4) // DamageType 4 = Explosion
        {
            var explosion = _configuration.Advanced.ExplosionDamage;
            var explosionAdv = _configuration.Advanced.ExplosionDamageAdvanced;
            
            float intensity = _configuration.Simple.ExplosionDamageIntensity;
            int explosionMag = (int)(magnitude * intensity);
            
            var effectIds = _hapticManager.CreateExplosionEffect(
                explosionMag,
                explosionAdv.ImpactDuration,
                explosionAdv.RumbleDuration,
                explosionAdv.ImpactAttackTime,
                explosionAdv.ImpactFadeTime,
                explosionAdv.RumbleAttackTime,
                explosionAdv.RumbleFadeTime,
                direction,
                explosionAdv.RumbleFrequency,
                explosionAdv.ImpactMultiplier,
                explosionAdv.RumbleMultiplier
            );
            
            if (effectIds[0] >= 0 && effectIds[1] >= 0)
            {
                _hapticManager.PlayEffect(effectIds[0], 1);
                _hapticManager.PlayEffect(effectIds[1], 1);
                
                int maxDuration = Math.Max(explosionAdv.ImpactDuration + explosionAdv.ImpactFadeTime, 
                                          explosionAdv.RumbleDuration + explosionAdv.RumbleFadeTime);
                Task.Delay(maxDuration + 50).ContinueWith(_ =>
                {
                    _hapticManager.DestroyEffect(effectIds[0]);
                    _hapticManager.DestroyEffect(effectIds[1]);
                });
            }
            return;
        }
        
        var settings = e.DamageType switch
        {
            (DamageType)0 => _configuration.Advanced.LaserDamage,      // Trace/Laser
            (DamageType)1 => _configuration.Advanced.BallisticDamage,  // Projectile/Ballistic
            (DamageType)2 => _configuration.Advanced.MissileDamage,    // Missile
            (DamageType)3 => _configuration.Advanced.MeleeDamage,      // Melee
            _ => _configuration.Advanced.BallisticDamage
        };
        
        float damageIntensity = (int)e.DamageType switch
        {
            0 => _configuration.Simple.LaserDamageIntensity,      // Trace/Laser
            1 => (e.DamageAmount >= 9.0f && e.DamageAmount <= 11.0f) 
                 ? _configuration.Simple.PPCDamageIntensity        // PPC damage
                 : _configuration.Simple.BallisticDamageIntensity, // Ballistic damage
            2 => _configuration.Simple.MissileDamageIntensity,    // Missile
            3 => _configuration.Simple.MeleeDamageIntensity,      // Melee
            _ => 0.6f
        };
        
        magnitude = (int)(magnitude * damageIntensity);
        magnitude = Math.Clamp(magnitude, 0, 32767);
        
        // Use appropriate effect type for each damage type (matching original SDL2 implementation)
        switch ((int)e.DamageType)
        {
            case 0: // Trace (Laser damage)
                // Detect laser tier by damage amount and apply appropriate duration
                // Small Laser: ~2-4 damage (~0.6s duration)
                // Medium Laser: ~4-6 damage (~0.76s duration)
                // Large Laser: ~7-10 damage (~0.89s duration)
                // ER variants are slightly higher
                
                int laserDuration;
                if (e.DamageAmount <= 4.0f)
                {
                    // Small laser tier
                    laserDuration = 600; // ~0.6s like small laser beam
                    Console.WriteLine($"Laser Damage: SMALL tier ({e.DamageAmount:F1} dmg) -> {laserDuration}ms duration");
                }
                else if (e.DamageAmount <= 6.5f)
                {
                    // Medium laser tier
                    laserDuration = 760; // ~0.76s like medium laser beam
                    Console.WriteLine($"Laser Damage: MEDIUM tier ({e.DamageAmount:F1} dmg) -> {laserDuration}ms duration");
                }
                else
                {
                    // Large laser tier
                    laserDuration = 890; // ~0.89s like large laser beam
                    Console.WriteLine($"Laser Damage: LARGE tier ({e.DamageAmount:F1} dmg) -> {laserDuration}ms duration");
                }
                
                var laserDmg = _configuration.Advanced.LaserDamage;
                effectId = _hapticManager.CreatePeriodicEffect(magnitude, laserDuration, laserDmg.Frequency, laserDmg.AttackTime, laserDmg.FadeTime);
                break;
                
            case 1: // Projectile (Ballistic/PPC damage)
                // Detect PPC vs Autocannon by damage amount
                // PPCs do around 10 damage, ACs vary (2-20 typically)
                bool isPPCDamage = e.DamageAmount >= 9.0f && e.DamageAmount <= 11.0f;
                
                if (isPPCDamage)
                {
                    var ppcDmg = _configuration.Advanced.PPCDamage;
                    effectId = _hapticManager.CreateImpactEffect(magnitude, ppcDmg.Duration, ppcDmg.AttackTime, ppcDmg.FadeTime, direction);
                }
                else
                {
                    var ballisticDmg = _configuration.Advanced.BallisticDamage;
                    effectId = _hapticManager.CreateImpactEffect(magnitude, ballisticDmg.Duration, ballisticDmg.AttackTime, ballisticDmg.FadeTime, direction);
                }
                break;
                
            case 2: // Missile damage
                // Impact effect for missile damage
                var missileDmg = _configuration.Advanced.MissileDamage;
                effectId = _hapticManager.CreateImpactEffect(magnitude, missileDmg.Duration, missileDmg.AttackTime, missileDmg.FadeTime, direction);
                break;
                
            case 3: // Melee damage
                // Heavy impact for melee
                var meleeDmg = _configuration.Advanced.MeleeDamage;
                effectId = _hapticManager.CreateImpactEffect(magnitude, meleeDmg.Duration, meleeDmg.AttackTime, meleeDmg.FadeTime, direction);
                break;
                
            default:
                // Fallback for unknown damage types
                effectId = _hapticManager.CreateImpactEffect(magnitude, settings.Duration, settings.AttackTime, settings.FadeTime, direction);
                break;
        }
        
        if (effectId >= 0)
        {
            _hapticManager.PlayEffect(effectId, 1);
            
            // Get duration based on damage type
            int duration = (int)e.DamageType switch
            {
                0 => _configuration.Advanced.LaserDamage.Duration + _configuration.Advanced.LaserDamage.FadeTime,
                1 => _configuration.Advanced.BallisticDamage.Duration + _configuration.Advanced.BallisticDamage.FadeTime,
                2 => _configuration.Advanced.MissileDamage.Duration + _configuration.Advanced.MissileDamage.FadeTime,
                3 => _configuration.Advanced.MeleeDamage.Duration + _configuration.Advanced.MeleeDamage.FadeTime,
                _ => settings.Duration + settings.FadeTime
            };
            
            Task.Delay(duration + 50).ContinueWith(_ => _hapticManager.DestroyEffect(effectId));
        }
        else
        {
            Console.WriteLine("Failed to create damage effect!");
        }
    }
    
    private void HandleMovement(object? sender, MovementEvent e)
    {
        if (e.Type != MovementType.Footstep)
            return;
        
        _isLeftFoot = !_isLeftFoot;
        int direction = _isLeftFoot ? 0 : 18000;
        
        Console.WriteLine($"FOOTSTEP: {(_isLeftFoot ? "PUSH" : "PULL")}, Tonnage={e.MechTonnage}, Speed={e.Velocity:F1}");
        
        float baseMag = 32767;
        int magnitude = (int)(baseMag * _configuration.Simple.FootstepIntensity * _configuration.MasterIntensity);
        magnitude = Math.Clamp(magnitude, 0, 32767);
        
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
            if (_activeJumpJetEffectId < 0)
            {
                float baseMag = 32767;
                int magnitude = (int)(baseMag * _configuration.Simple.JumpJetIntensity * _configuration.MasterIntensity);
                magnitude = Math.Clamp(magnitude, 0, 32767);
                
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
            if (_activeJumpJetEffectId >= 0)
            {
                _hapticManager.StopEffect(_activeJumpJetEffectId);
                _hapticManager.DestroyEffect(_activeJumpJetEffectId);
                _activeJumpJetEffectId = -1;
            }
        }
    }
    
    private void HandleImpact(object? sender, ImpactEvent e)
    {
        Console.WriteLine($"LANDING: Velocity={e.ImpactVelocity:F1}, Tonnage={e.MechTonnage}");
        
        float baseMag = 32767;
        int magnitude = (int)(baseMag * _configuration.Simple.LandingIntensity * _configuration.MasterIntensity);
        magnitude = Math.Clamp(magnitude, 0, 32767);
        
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
        
        switch (e.WeaponClass)
        {
            case WeaponClass.Ballistic:
                baseMag = (e.Damage * 20) + 10000;
                weaponIntensity = _configuration.Simple.BallisticIntensity;
                break;
                
            case WeaponClass.Energy:
                if (e.IsPPC)
                {
                    baseMag = (e.Damage * 15) + 10000;
                    weaponIntensity = _configuration.Simple.PPCIntensity;
                }
                else if (e.IsMachineGun)
                {
                    baseMag = (e.Damage * 18) + 25000;
                    weaponIntensity = _configuration.Simple.MachineGunIntensity;
                }
                else
                {
                    baseMag = (e.Damage * 18) + 10000;
                    weaponIntensity = _configuration.Simple.LaserIntensity;
                }
                break;
                
            case WeaponClass.Missile:
                baseMag = (e.Damage * 15) + 20000;
                weaponIntensity = _configuration.Simple.MissileIntensity;
                break;
                
            case WeaponClass.Melee:
                baseMag = (e.Damage * 12) + 20000;
                weaponIntensity = _configuration.Simple.MeleeIntensity;
                break;
        }
        
        baseMag *= weaponIntensity * _configuration.MasterIntensity;
        int magnitude = Math.Clamp((int)baseMag, 0, 32767);
        
        string weaponType = e.IsPPC ? "PPC" : e.WeaponClass.ToString();
        Console.WriteLine($"Weapon: {weaponType}, RawDamage={e.Damage:F1}, Intensity={weaponIntensity:F2}, FinalMag={magnitude}");
        
        return magnitude;
    }
    
    private int CalculateRecoilDuration(WeaponFireEvent e)
    {
        return e.WeaponClass switch
        {
            WeaponClass.Ballistic => _configuration.Advanced.Ballistics.Duration + _configuration.Advanced.Ballistics.FadeTime,
            WeaponClass.Energy when e.IsPPC => _configuration.Advanced.PPCs.Duration + _configuration.Advanced.PPCs.FadeTime,
            WeaponClass.Energy when e.IsMachineGun => _configuration.Advanced.MachineGuns.Duration + _configuration.Advanced.MachineGuns.FadeTime,
            WeaponClass.Energy => (int)(e.BeamDuration * 1000) + _configuration.Advanced.Lasers.FadeTime,
            WeaponClass.Missile => CalculateMissileDuration(e) + _configuration.Advanced.Missiles.FadeTime,
            WeaponClass.Melee => _configuration.Advanced.Melee.Duration + _configuration.Advanced.Melee.FadeTime,
            _ => 200
        };
    }
    
    private int CalculateMissileDuration(WeaponFireEvent e)
    {
        if (e.FiringDelay > 0.01f)
        {
            int missileCount = (int)e.ProjectileMass;
            float totalFiringTime = e.FiringDelay * (missileCount - 1);
            return (int)(totalFiringTime * 1000);
        }
        else
        {
            return _configuration.Advanced.Missiles.Duration;
        }
    }
    
    public void Dispose()
    {
        Stop();
        _hapticManager?.Dispose();
        _telemetryReader?.Dispose();
    }
}
