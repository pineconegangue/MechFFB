namespace MechFFBReader.Events;

/// <summary>
/// Base class for all telemetry events from MechWarrior 5
/// </summary>
public abstract class TelemetryEvent
{
    /// <summary>
    /// Timestamp when the event occurred (game time)
    /// </summary>
    public float Timestamp { get; set; }
    
    /// <summary>
    /// Unique identifier for this event instance
    /// </summary>
    public Guid EventId { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// Event type identifier for routing
    /// </summary>
    public abstract string EventType { get; }
}
