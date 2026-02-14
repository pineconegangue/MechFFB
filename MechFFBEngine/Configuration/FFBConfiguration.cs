using System.Text.Json;

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
    /// Currently selected device GUID
    /// </summary>
    public Guid? SelectedDeviceGuid { get; set; }
    
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
    /// Save configuration to file
    /// </summary>
    public void Save()
    {
        try
        {
            string path = GetConfigPath();
            Console.WriteLine($"Attempting to save configuration to: {path}");
            
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            
            Console.WriteLine($"JSON generated, length: {json.Length} characters");
            
            File.WriteAllText(path, json);
            
            // Verify the file was written
            if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                Console.WriteLine($"✓ Configuration saved successfully! File size: {fileInfo.Length} bytes");
            }
            else
            {
                Console.WriteLine($"✗ WARNING: File does not exist after write!");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ ERROR saving configuration: {ex.Message}");
            Console.WriteLine($"  Stack trace: {ex.StackTrace}");
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
            Console.WriteLine($"Attempting to load configuration from: {path}");
            
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                Console.WriteLine($"Found config file, size: {json.Length} characters");
                Console.WriteLine($"First 200 chars: {json.Substring(0, Math.Min(200, json.Length))}");
                
                var config = JsonSerializer.Deserialize<FFBConfiguration>(json);
                if (config != null)
                {
                    Console.WriteLine($"✓ Configuration loaded successfully!");
                    Console.WriteLine($"  MasterIntensity: {config.MasterIntensity}");
                    Console.WriteLine($"  Simple.BallisticIntensity: {config.Simple.BallisticIntensity}");
                    Console.WriteLine($"  Simple.LaserIntensity: {config.Simple.LaserIntensity}");
                    return config;
                }
                else
                {
                    Console.WriteLine("✗ Deserialization returned null, using defaults");
                }
            }
            else
            {
                Console.WriteLine($"No config file found at {path}, using defaults");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Error loading configuration: {ex.Message}");
        }
        
        Console.WriteLine("Using default configuration");
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
    public float BallisticIntensity { get; set; } = 0.6f;
    public float LaserIntensity { get; set; } = 0.6f;
    public float PPCIntensity { get; set; } = 0.6f;
    public float MissileIntensity { get; set; } = 0.6f;
    public float MeleeIntensity { get; set; } = 0.6f;
    
    // Incoming damage by type
    public float LaserDamageIntensity { get; set; } = 0.6f;
    public float BallisticDamageIntensity { get; set; } = 0.6f;
    public float MissileDamageIntensity { get; set; } = 0.6f;
    public float MeleeDamageIntensity { get; set; } = 0.6f;
    public float ExplosionDamageIntensity { get; set; } = 0.6f;
    
    // Impacts
    public float LandingIntensity { get; set; } = 0.6f;
    
    // Movement (usually more subtle)
    public float FootstepIntensity { get; set; } = 0.3f;
    public float JumpJetIntensity { get; set; } = 0.5f;
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
    public DamageSettings LaserDamage { get; set; } = new();
    public DamageSettings BallisticDamage { get; set; } = new();
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
    public DamageSettings ExplosionDamage { get; set; } = new() 
    { 
        AttackTime = 20, 
        FadeTime = 400 
    };
    public ExplosionDamageSettings ExplosionDamageAdvanced { get; set; } = new();
    
    // Movement and impacts
    public FootstepSettings Footsteps { get; set; } = new();
    public JumpJetSettings JumpJets { get; set; } = new();
    public LandingSettings Landing { get; set; } = new();
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
    public float Intensity { get; set; } = 0.6f;
    public int Duration { get; set; } = 150; // ms
    public int AttackTime { get; set; } = 10; // ms
    public int FadeTime { get; set; } = 120; // ms
}

public class LaserSettings
{
    public float Intensity { get; set; } = 0.6f;
    public int Duration { get; set; } = 300; // ms
    public int Frequency { get; set; } = 40; // Hz
    public int AttackTime { get; set; } = 30; // ms
    public int FadeTime { get; set; } = 80; // ms
}

public class PPCSettings
{
    public float Intensity { get; set; } = 0.6f;
    public int Duration { get; set; } = 200; // ms
    public int AttackTime { get; set; } = 20; // ms
    public int FadeTime { get; set; } = 150; // ms
}

public class MachineGunSettings
{
    public float Intensity { get; set; } = 0.6f;
    public int Duration { get; set; } = 60; // ms
    public int Frequency { get; set; } = 33; // Hz
    public int AttackTime { get; set; } = 10; // ms
    public int FadeTime { get; set; } = 30; // ms
}

public class MissileSettings
{
    public float Intensity { get; set; } = 0.6f;
    public int Duration { get; set; } = 200; // ms (max 1500)
    public int Frequency { get; set; } = 35; // Hz - rumble frequency for missile launches
    public float RumbleMultiplier { get; set; } = 1.0f; // 100% = 32767 max magnitude
    public int AttackTime { get; set; } = 20; // ms
    public int FadeTime { get; set; } = 150; // ms (max 600)
}

public class MeleeSettings
{
    public float Intensity { get; set; } = 0.6f;
    public int Duration { get; set; } = 400; // ms
    public int AttackTime { get; set; } = 5; // ms
    public int FadeTime { get; set; } = 300; // ms
}

// Damage effect settings
public class DamageSettings
{
    public float Intensity { get; set; } = 0.6f;
    public int Duration { get; set; } = 300; // ms
    public int Frequency { get; set; } = 50; // Hz (for periodic effects like lasers)
    public int AttackTime { get; set; } = 40; // ms
    public int FadeTime { get; set; } = 120; // ms
    public float RumbleMultiplier { get; set; } = 2.1f; // Multiplier for explosion rumble effect (default 2.1x = 210%)
}

// Movement effect settings
public class FootstepSettings
{
    public float Intensity { get; set; } = 0.3f;
    public int Duration { get; set; } = 100; // ms
    public int AttackTime { get; set; } = 5; // ms
    public int FadeTime { get; set; } = 80; // ms
}

public class JumpJetSettings
{
    public float Intensity { get; set; } = 0.5f;
    public int Frequency { get; set; } = 25; // Hz
    public int AttackTime { get; set; } = 50; // ms
    public int FadeTime { get; set; } = 50; // ms
}

public class LandingSettings
{
    public float Intensity { get; set; } = 0.6f;
    public int Duration { get; set; } = 300; // ms
    public int AttackTime { get; set; } = 10; // ms
    public int FadeTime { get; set; } = 200; // ms
}
