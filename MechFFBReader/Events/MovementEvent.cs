using System.Numerics;

namespace MechFFBReader.Events;

/// <summary>
/// Event for mech movement - footsteps, torso twist, etc.
/// </summary>
public class MovementEvent : TelemetryEvent
{
    public override string EventType => "Movement";
    
    public MovementType Type { get; set; }
    
    /// <summary>
    /// Mech tonnage (for calculating footstep intensity)
    /// </summary>
    public float MechTonnage { get; set; }
    
    /// <summary>
    /// Current velocity in m/s
    /// </summary>
    public float Velocity { get; set; }
    
    /// <summary>
    /// Which foot stepped (for footsteps)
    /// </summary>
    public FootSide FootSide { get; set; }
    
    /// <summary>
    /// Torso twist rate in degrees/second
    /// </summary>
    public float TorsoTwistRate { get; set; }
    
    /// <summary>
    /// Current torso twist angle relative to legs (-90 to +90 degrees)
    /// </summary>
    public float TorsoTwistAngle { get; set; }
}

public enum MovementType
{
    Footstep,
    TorsoTwist,
    Turning
}

public enum FootSide
{
    Left,
    Right
}
