using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.DirectInput;

namespace MechFFBEngine.Haptic;

/// <summary>
/// DirectInput Force Feedback implementation for VPForce Rhino compatibility
/// Replaces SDL2 with native DirectX FFB support
/// </summary>
public static class DirectInputFFB
{
    // Effect type GUIDs from DirectInput
    public static readonly Guid GUID_ConstantForce = new Guid("13541C20-8E33-11D0-9AD0-00A0C9A06E35");
    public static readonly Guid GUID_RampForce = new Guid("13541C21-8E33-11D0-9AD0-00A0C9A06E35");
    public static readonly Guid GUID_Square = new Guid("13541C22-8E33-11D0-9AD0-00A0C9A06E35");
    public static readonly Guid GUID_Sine = new Guid("13541C23-8E33-11D0-9AD0-00A0C9A06E35");
    public static readonly Guid GUID_Triangle = new Guid("13541C24-8E33-11D0-9AD0-00A0C9A06E35");
    public static readonly Guid GUID_SawtoothUp = new Guid("13541C25-8E33-11D0-9AD0-00A0C9A06E35");
    public static readonly Guid GUID_SawtoothDown = new Guid("13541C26-8E33-11D0-9AD0-00A0C9A06E35");
    public static readonly Guid GUID_Spring = new Guid("13541C27-8E33-11D0-9AD0-00A0C9A06E35");
    public static readonly Guid GUID_Damper = new Guid("13541C28-8E33-11D0-9AD0-00A0C9A06E35");
    public static readonly Guid GUID_Inertia = new Guid("13541C29-8E33-11D0-9AD0-00A0C9A06E35");
    public static readonly Guid GUID_Friction = new Guid("13541C2A-8E33-11D0-9AD0-00A0C9A06E35");
    public static readonly Guid GUID_CustomForce = new Guid("13541C2B-8E33-11D0-9AD0-00A0C9A06E35");

    // DirectInput constants
    public const int DI_DEGREES = 100;
    public const int DI_FFNOMINALMAX = 10000;
    public const int DI_SECONDS = 1000000;
    
    /// <summary>
    /// Convert degrees (0-35999 SDL2 format) to DirectInput angle (0-35999 hundredths of degrees)
    /// </summary>
    public static int DegreesToDirectInput(int degrees)
    {
        // SDL2 uses: 0 = North, 9000 = East, 18000 = South, 27000 = West
        // DirectInput uses same format but in hundredths of degrees
        return degrees; // Actually the same format!
    }
    
    /// <summary>
    /// Convert milliseconds to DirectInput microseconds
    /// </summary>
    public static int MillisecondsToMicroseconds(int milliseconds)
    {
        return milliseconds * 1000;
    }
    
    /// <summary>
    /// Convert magnitude (0-32767) to DirectInput scale (0-10000)
    /// </summary>
    public static int MagnitudeToDirectInput(int magnitude)
    {
        // SDL2: 0-32767
        // DirectInput: 0-10000
        return (int)((magnitude / 32767.0) * DI_FFNOMINALMAX);
    }
    
    /// <summary>
    /// Convert envelope level (0-32767) to DirectInput (0-10000)
    /// </summary>
    public static int EnvelopeLevelToDirectInput(int level)
    {
        return (int)((level / 32767.0) * DI_FFNOMINALMAX);
    }
}

/// <summary>
/// Helper class for creating DirectInput effect parameters
/// </summary>
public static class DirectInputEffectHelper
{
    /// <summary>
    /// Create a ConstantForce effect parameters
    /// </summary>
    public static EffectParameters CreateConstantForce(int magnitude, int direction, int durationMs, 
        int attackMs = 0, int fadeMs = 0, int attackLevel = 0)
    {
        var parameters = new EffectParameters();
        
        // Duration
        parameters.Duration = DirectInputFFB.MillisecondsToMicroseconds(durationMs);
        
        // Flags - Use Cartesian coordinates and ObjectOffsets (more compatible than ObjectIds)
        parameters.Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets;
        
        // Gain - always 100% (10000)
        parameters.Gain = DirectInputFFB.DI_FFNOMINALMAX;
        
        // Sample period - 0 means use default
        parameters.SamplePeriod = 0;
        
        // Start delay
        parameters.StartDelay = 0;
        
        // Trigger button - none
        parameters.TriggerButton = -1;
        parameters.TriggerRepeatInterval = 0;
        
        // Axes - Use axis offsets for X and Y
        parameters.Axes = new int[] { 0, 1 }; // X and Y axis offsets
        
        // Direction - convert angle to X,Y components
        double angleRad = direction * Math.PI / 18000.0;
        int dirX = (int)(Math.Cos(angleRad) * DirectInputFFB.DI_FFNOMINALMAX);
        int dirY = (int)(Math.Sin(angleRad) * DirectInputFFB.DI_FFNOMINALMAX);
        parameters.Directions = new int[] { dirX, dirY };
        
        // Envelope
        var envelope = new Envelope();
        envelope.AttackLevel = DirectInputFFB.EnvelopeLevelToDirectInput(attackLevel);
        envelope.AttackTime = DirectInputFFB.MillisecondsToMicroseconds(attackMs);
        envelope.FadeLevel = 0;
        envelope.FadeTime = DirectInputFFB.MillisecondsToMicroseconds(fadeMs);
        parameters.Envelope = envelope;
        
        // Type-specific parameters (ConstantForce)
        var constantForce = new ConstantForce();
        constantForce.Magnitude = DirectInputFFB.MagnitudeToDirectInput(magnitude);
        parameters.Parameters = constantForce;
        
        return parameters;
    }
    
    /// <summary>
    /// Create a Periodic effect (Sine wave) for rumble/vibration
    /// </summary>
    public static EffectParameters CreatePeriodicEffect(int magnitude, int durationMs, int frequencyHz,
        int attackMs = 0, int fadeMs = 0, int attackLevel = 0)
    {
        var parameters = new EffectParameters();
        
        // Duration
        parameters.Duration = DirectInputFFB.MillisecondsToMicroseconds(durationMs);
        
        // Flags - Use Cartesian coordinates and ObjectOffsets
        parameters.Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets;
        
        // Gain
        parameters.Gain = DirectInputFFB.DI_FFNOMINALMAX;
        
        parameters.SamplePeriod = 0;
        parameters.StartDelay = 0;
        parameters.TriggerButton = -1;
        parameters.TriggerRepeatInterval = 0;
        
        // Axes
        parameters.Axes = new int[] { 0, 1 }; // X and Y axis offsets
        
        // Direction - for periodic, usually straight ahead
        parameters.Directions = new int[] { DirectInputFFB.DI_FFNOMINALMAX, 0 };
        
        // Envelope
        var envelope = new Envelope();
        envelope.AttackLevel = DirectInputFFB.EnvelopeLevelToDirectInput(attackLevel > 0 ? attackLevel : magnitude / 2);
        envelope.AttackTime = DirectInputFFB.MillisecondsToMicroseconds(attackMs);
        envelope.FadeLevel = 0;
        envelope.FadeTime = DirectInputFFB.MillisecondsToMicroseconds(fadeMs);
        parameters.Envelope = envelope;
        
        // Periodic parameters
        var periodic = new PeriodicForce();
        periodic.Magnitude = DirectInputFFB.MagnitudeToDirectInput(magnitude);
        periodic.Offset = 0;
        periodic.Phase = 0;
        // Period in microseconds = 1/frequency
        periodic.Period = DirectInputFFB.MillisecondsToMicroseconds(1000 / frequencyHz);
        parameters.Parameters = periodic;
        
        return parameters;
    }
}
