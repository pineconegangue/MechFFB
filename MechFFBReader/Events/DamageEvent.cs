using System.Numerics;

namespace MechFFBReader.Events;

/// <summary>
/// Event fired when the player's mech takes damage
/// </summary>
public class DamageEvent : TelemetryEvent
{
    public override string EventType => "Damage";
    
    /// <summary>
    /// Amount of damage taken
    /// </summary>
    public float DamageAmount { get; set; }
    
    /// <summary>
    /// Direction the hit came from (normalized vector)
    /// Used for directional force feedback
    /// </summary>
    public Vector3 HitDirection { get; set; }
    
    /// <summary>
    /// Which component was hit (Left Torso, Right Arm, etc.)
    /// </summary>
    public string ComponentHit { get; set; } = string.Empty;
    
    /// <summary>
    /// Type of damage: Ballistic, Energy, Missile, Melee
    /// </summary>
    public DamageType DamageType { get; set; }
    
    /// <summary>
    /// Was this a critical hit?
    /// </summary>
    public bool IsCritical { get; set; }
    
    /// <summary>
    /// Remaining armor on the hit component (0-1 normalized)
    /// </summary>
    public float RemainingArmor { get; set; }
    
    /// <summary>
    /// Did this hit destroy the component?
    /// </summary>
    public bool ComponentDestroyed { get; set; }
}

public enum DamageType
{
    Trace = 0,      // Lasers, flamers, machine guns
    Projectile = 1, // Autocannons, PPCs
    Missile = 2,    // Missiles
    Melee = 3,      // Melee attacks
    Explosion = 4,  // Explosions
    Other = 5
}
