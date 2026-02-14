using System.Numerics;

namespace MechFFBReader.Events;

/// <summary>
/// Event for landing impacts and collisions
/// </summary>
public class ImpactEvent : TelemetryEvent
{
    public override string EventType => "Impact";
    
    public ImpactType Type { get; set; }
    
    /// <summary>
    /// Impact velocity in m/s
    /// </summary>
    public float ImpactVelocity { get; set; }
    
    /// <summary>
    /// Mech tonnage
    /// </summary>
    public float MechTonnage { get; set; }
    
    /// <summary>
    /// Direction of impact (normalized)
    /// </summary>
    public Vector3 ImpactDirection { get; set; }
    
    /// <summary>
    /// Damage taken from impact (if any)
    /// </summary>
    public float DamageTaken { get; set; }
    
    /// <summary>
    /// Was this a catastrophic impact (knocked down, etc.)?
    /// </summary>
    public bool IsCatastrophic { get; set; }
}

public enum ImpactType
{
    Landing,
    Collision,
    Falling,
    Knockdown
}
