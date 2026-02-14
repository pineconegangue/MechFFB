namespace MechFFBReader.Events;

/// <summary>
/// Event for jump jet activation and thrust changes
/// </summary>
public class JumpJetEvent : TelemetryEvent
{
    public override string EventType => "JumpJet";
    
    public JumpJetState State { get; set; }
    
    /// <summary>
    /// Current thrust level (0.0 to 1.0)
    /// </summary>
    public float ThrustLevel { get; set; }
    
    /// <summary>
    /// Mech tonnage (affects thrust feel)
    /// </summary>
    public float MechTonnage { get; set; }
    
    /// <summary>
    /// Current vertical velocity in m/s
    /// </summary>
    public float VerticalVelocity { get; set; }
}

public enum JumpJetState
{
    Inactive,
    Igniting,
    Active,
    ShuttingDown
}
