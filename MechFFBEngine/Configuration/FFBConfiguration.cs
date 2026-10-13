using System.Text.Json;
using System.Text.Json.Serialization;

namespace MechFFBEngine.Configuration;

/// <summary>
/// Master configuration for all FFB effects
/// </summary>
public class FFBConfiguration
{
    /// <summary>
    /// Master intensity multiplier (0.0 to 1.0)
    /// </summary>
    public float MasterIntensity { get; set; } = 1.0f;

    /// <summary>
    /// Configuration mode: Simple or Advanced
    /// </summary>
    public ConfigurationMode Mode { get; set; } = ConfigurationMode.Simple;

    // Simple mode settings
    public SimpleModeSettings Simple { get; set; } = new();

    // Advanced mode settings
    public AdvancedModeSettings Advanced { get; set; } = new();

    /// <summary>
    /// Instance GUID of the last device you selected - re-selected automatically on startup
    /// when it's connected, even if several FFB devices are present.
    /// </summary>
    public Guid? SelectedDeviceGuid { get; set; }

    /// <summary>
    /// Axis mapping for devices that don't have their own saved mapping. The default (no flips)
    /// is the reference behaviour - what "Invert Axis" ON used to do. See AxisMapping.
    /// </summary>
    public AxisMapping DefaultAxisMapping { get; set; } = new();

    /// <summary>Per-device axis mapping, keyed by device instance GUID (set via Flip X/Y or Calibrate).</summary>
    public Dictionary<Guid, AxisMapping> DeviceAxisMappings { get; set; } = new();

    /// <summary>The mapping for a device: its own if saved, otherwise the default.</summary>
    public AxisMapping GetAxisMapping(Guid? device) =>
        device.HasValue && DeviceAxisMappings.TryGetValue(device.Value, out var m) ? m : DefaultAxisMapping;

    /// <summary>
    /// Legacy "InvertDirection" key - read once, never written. Invert ON was the reference
    /// (no flips); Invert OFF was a 180° turn, which is exactly Flip X + Flip Y.
    /// </summary>
    [JsonPropertyName("InvertDirection"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyInvertDirection
    {
        get => null;
        set
        {
            if (value.HasValue)
                DefaultAxisMapping = value.Value ? new AxisMapping() : new AxisMapping { FlipX = true, FlipY = true };
        }
    }

    /// <summary>
    /// Open the FFB device in exclusive mode. ON by default: DirectInput only lets most devices
    /// play force feedback when opened exclusively (otherwise Start() fails with
    /// NotExclusiveAcquired). Turn off only for devices that need it (e.g. Sidewinder FFB2).
    /// Saved as "ExclusiveMode"; a short-lived "EnableExclusiveMode" key (defaulted to off by
    /// mistake) is deliberately ignored so those files pick up the new default.
    /// </summary>
    [JsonPropertyName("ExclusiveMode")]
    public bool EnableExclusiveMode { get; set; } = true;

    /// <summary>
    /// Legacy "DisableExclusiveMode" key (older releases) - read once, never written.
    /// Disable = true (e.g. Sidewinder users) keeps exclusive mode off.
    /// </summary>
    [JsonPropertyName("DisableExclusiveMode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyDisableExclusiveMode
    {
        get => null;
        set { if (value.HasValue) EnableExclusiveMode = !value.Value; }
    }

    /// <summary>
    /// Log every telemetry event and effect to the debug window. Off by default.
    /// </summary>
    public bool VerboseLogging { get; set; } = false;

    /// <summary>
    /// Get the default configuration file path
    /// </summary>
    private static string GetConfigPath()
    {
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string mechFFBPath = Path.Combine(appDataPath, "MechFFB");
        Directory.CreateDirectory(mechFFBPath); // Ensure directory exists
        return Path.Combine(mechFFBPath, "settings.json");
    }

    /// <summary>
    /// Save configuration to file. Silent on success unless VerboseLogging is on.
    /// </summary>
    public void Save()
    {
        try
        {
            string path = GetConfigPath();
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            if (VerboseLogging)
                Console.WriteLine($"Configuration saved to {DisplayPath.Of(path)} ({json.Length} chars)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ ERROR saving configuration: {ex.Message}");
        }
    }

    /// <summary>
    /// Load configuration from file, or create default if none exists
    /// </summary>
    public static FFBConfiguration Load()
    {
        try
        {
            string path = GetConfigPath();
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<FFBConfiguration>(File.ReadAllText(path));
                if (config != null)
                {
                    Console.WriteLine($"✓ Settings loaded from {DisplayPath.Of(path)}");
                    if (config.VerboseLogging)
                        Console.WriteLine($"  MasterIntensity={config.MasterIntensity}, DefaultAxisMapping={config.DefaultAxisMapping}, EnableExclusiveMode={config.EnableExclusiveMode}");
                    return config;
                }
                Console.WriteLine("✗ Settings file was empty or invalid - using defaults");
            }
            else
            {
                Console.WriteLine($"No settings file at {DisplayPath.Of(path)} - using defaults");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Error loading settings: {ex.Message} - using defaults");
        }

        return new FFBConfiguration();
    }
}

public enum ConfigurationMode
{
    Simple,
    Advanced
}

/// <summary>
/// Simple mode - individual sliders for each major effect type
/// </summary>
public class SimpleModeSettings
{
    // Weapon recoil by type (default to ~60% for good starting feel)
    public float BallisticIntensity { get; set; } = 1.0f;
    public float LaserIntensity { get; set; } = 0.6f;
    public float PPCIntensity { get; set; } = 0.6f;
    public float MissileIntensity { get; set; } = 0.6f;
    public float MachineGunIntensity { get; set; } = 0.6f;
    public float MeleeIntensity { get; set; } = 0.6f;

    // Incoming damage by type
    // Laser & MG: the game reports incoming lasers and machine guns as the same (Trace) damage type
    public float LaserMGDamageIntensity { get; set; } = 0.6f;
    // Projectile: the game reports incoming autocannons and PPCs as the same damage type
    public float ProjectileDamageIntensity { get; set; } = 0.6f;
    public float MissileDamageIntensity { get; set; } = 0.6f;
    public float MeleeDamageIntensity { get; set; } = 0.6f;
    public float ExplosionDamageIntensity { get; set; } = 0.6f;

    // Impacts
    public float LandingIntensity { get; set; } = 0.6f;

    // Movement (usually more subtle)
    public float FootstepIntensity { get; set; } = 0.3f;
    public float JumpJetIntensity { get; set; } = 0.5f;

    // Legacy keys from older settings.json files - read once on load, never written back.
    // (PPCDamageIntensity is simply ignored now that the slider is gone.)
    [JsonPropertyName("LaserDamageIntensity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyLaserDamageIntensity { get => null; set { if (value.HasValue) LaserMGDamageIntensity = value.Value; } }

    [JsonPropertyName("BallisticDamageIntensity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyBallisticDamageIntensity { get => null; set { if (value.HasValue) ProjectileDamageIntensity = value.Value; } }
}

/// <summary>
/// Advanced mode - granular control over individual effect parameters
/// </summary>
public class AdvancedModeSettings
{
    // Weapon-specific settings with detailed control
    public BallisticSettings Ballistics { get; set; } = new();
    public LaserSettings Lasers { get; set; } = new();
    public PPCSettings PPCs { get; set; } = new();
    public MachineGunSettings MachineGuns { get; set; } = new();
    public MissileSettings Missiles { get; set; } = new();
    public MeleeSettings Melee { get; set; } = new();

    // Damage by type
    public DamageSettings LaserMGDamage { get; set; } = new();     // Incoming lasers + machine guns
    public DamageSettings ProjectileDamage { get; set; } = new();  // Incoming autocannons + PPCs
    public DamageSettings MissileDamage { get; set; } = new()
    {
        AttackTime = 15,
        FadeTime = 200
    };
    public DamageSettings MeleeDamage { get; set; } = new()
    {
        Duration = 400,
        AttackTime = 5,
        FadeTime = 300
    };
    public ExplosionDamageSettings ExplosionDamageAdvanced { get; set; } = new();

    // Movement and impacts
    public FootstepSettings Footsteps { get; set; } = new();
    public JumpJetSettings JumpJets { get; set; } = new();
    public LandingSettings Landing { get; set; } = new();

    // Legacy keys from older settings.json files - read once on load, never written back.
    // (PPCDamage is simply ignored now that its sliders are gone.)
    [JsonPropertyName("LaserDamage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DamageSettings? LegacyLaserDamage { get => null; set { if (value != null) LaserMGDamage = value; } }

    [JsonPropertyName("BallisticDamage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DamageSettings? LegacyBallisticDamage { get => null; set { if (value != null) ProjectileDamage = value; } }
}

// Explosion-specific settings with separate impact and rumble controls
public class ExplosionDamageSettings
{
    // Impact settings
    public int ImpactDuration { get; set; } = 300; // ms
    public int ImpactAttackTime { get; set; } = 20; // ms
    public int ImpactFadeTime { get; set; } = 400; // ms
    public float ImpactMultiplier { get; set; } = 1.0f; // 100% of base damage magnitude

    // Rumble settings
    public int RumbleDuration { get; set; } = 500; // ms
    public int RumbleFrequency { get; set; } = 25; // Hz
    public float RumbleMultiplier { get; set; } = 1.0f; // 100% = 32767 max
    public int RumbleAttackTime { get; set; } = 50; // ms
    public int RumbleFadeTime { get; set; } = 400; // ms
}

// Weapon fire effect settings
public class BallisticSettings
{
    public int Duration { get; set; } = 150; // ms
    public int AttackTime { get; set; } = 10; // ms
    public int FadeTime { get; set; } = 120; // ms
}

public class LaserSettings
{
    // Duration removed - now uses F3 beam duration from game data
    public int Frequency { get; set; } = 40; // Hz
    public int AttackTime { get; set; } = 30; // ms
    public int FadeTime { get; set; } = 80; // ms
}

public class PPCSettings
{
    public int Duration { get; set; } = 200; // ms
    public int AttackTime { get; set; } = 20; // ms
    public int FadeTime { get; set; } = 150; // ms
}

public class MachineGunSettings
{
    public int Duration { get; set; } = 60; // ms
    public int Frequency { get; set; } = 33; // Hz
    public int AttackTime { get; set; } = 10; // ms
    public int FadeTime { get; set; } = 30; // ms
}

public class MissileSettings
{
    public int Duration { get; set; } = 200; // ms (max 1500)
    public int Frequency { get; set; } = 35; // Hz - rumble frequency for missile launches
    public float RumbleMultiplier { get; set; } = 1.0f; // Missile launch rumble strength (1.0 = 100%), on top of Simple.MissileIntensity
    public int AttackTime { get; set; } = 20; // ms
    public int FadeTime { get; set; } = 150; // ms (max 600)
}

public class MeleeSettings
{
    public int Duration { get; set; } = 400; // ms
    public int AttackTime { get; set; } = 5; // ms
    public int FadeTime { get; set; } = 300; // ms
}

// Damage effect settings
public class DamageSettings
{
    public int Duration { get; set; } = 300; // ms
    public int Frequency { get; set; } = 50; // Hz (for periodic effects like lasers)
    public int AttackTime { get; set; } = 40; // ms
    public int FadeTime { get; set; } = 120; // ms
}

// Movement effect settings
public class FootstepSettings
{
    public int Duration { get; set; } = 100; // ms
    public int AttackTime { get; set; } = 5; // ms
    public int FadeTime { get; set; } = 80; // ms
}

public class JumpJetSettings
{
    public int Frequency { get; set; } = 25; // Hz
    public int AttackTime { get; set; } = 50; // ms
    public int FadeTime { get; set; } = 50; // ms
}

public class LandingSettings
{
    public int Duration { get; set; } = 300; // ms
    public int AttackTime { get; set; } = 10; // ms
    public int FadeTime { get; set; } = 200; // ms
}