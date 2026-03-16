using MechFFBReader.Events;
using MechFFBReader.Infrastructure;
using System.Text.Json;
using System.Threading.Channels;

namespace MechFFBReader;

/// <summary>
/// Main telemetry reader that polls the MMF and raises events
/// This is the core component that other parts of MechFFB will consume
/// </summary>
public class TelemetryReader : IDisposable
{
    private readonly MemoryMappedFileHandler _mmfHandler;
    private readonly CancellationTokenSource _cancellationSource;
    private Task? _readTask;
    private bool _isRunning;

    // Events that consumers can subscribe to
    public event EventHandler<WeaponFireEvent>? OnWeaponFire;
    public event EventHandler<DamageEvent>? OnDamage;
    public event EventHandler<MovementEvent>? OnMovement;
    public event EventHandler<JumpJetEvent>? OnJumpJet;
    public event EventHandler<ImpactEvent>? OnImpact;
    public event EventHandler<string>? OnConnectionStateChanged;

    // Statistics
    public int EventsProcessed { get; private set; }
    public DateTime? LastEventTime { get; private set; }
    public bool IsConnected => _mmfHandler.IsConnected;

    // Async log queue — keeps Console.WriteLine off the MMF read thread
    private readonly Channel<string> _logQueue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private Task? _logTask;
    private CancellationTokenSource? _logCts;

    private void Log(string message) => _logQueue.Writer.TryWrite(message);

    private async Task LogLoop(CancellationToken token)
    {
        try
        {
            await foreach (var msg in _logQueue.Reader.ReadAllAsync(token))
                Console.WriteLine(msg);
        }
        catch (OperationCanceledException) { }
    }

    public TelemetryReader()
    {
        _mmfHandler = new MemoryMappedFileHandler();
        _cancellationSource = new CancellationTokenSource();
    }

    /// <summary>
    /// Start reading telemetry data from the MMF
    /// This will run on a background thread and raise events as data comes in
    /// </summary>
    public void Start()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        _logCts = new CancellationTokenSource();
        _logTask = Task.Factory.StartNew(
            () => LogLoop(_logCts.Token),
            _logCts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        _readTask = Task.Factory.StartNew(
            ReadLoop,
            _cancellationSource.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Stop reading telemetry data
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _cancellationSource.Cancel();
        _readTask?.Wait(TimeSpan.FromSeconds(2));
        _logQueue.Writer.TryComplete();
        _logCts?.Cancel();
        _logTask?.Wait(TimeSpan.FromSeconds(1));
        _logCts?.Dispose();
        _logCts = null;
    }

    private void ReadLoop()
    {
        var token = _cancellationSource.Token;
        bool wasConnected = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Try to connect if not connected
                if (!_mmfHandler.IsConnected)
                {
                    bool connected = _mmfHandler.Connect();

                    if (connected && !wasConnected)
                    {
                        wasConnected = true;
                        OnConnectionStateChanged?.Invoke(this, "Connected to MechWarrior 5");
                        Log("Connected to MechWarrior 5 telemetry");
                    }
                    else if (!connected && wasConnected)
                    {
                        wasConnected = false;
                        OnConnectionStateChanged?.Invoke(this, "Disconnected from MechWarrior 5");
                        Log("Disconnected from MechWarrior 5 telemetry");
                    }

                    if (!connected)
                    {
                        // Not connected — sleep 1s before retrying (no need for precision here)
                        Thread.Sleep(1000);
                        continue;
                    }
                }

                // Read all new events from the circular buffer
                var events = _mmfHandler.ReadNewEvents();

                foreach (var eventData in events)
                {
                    ProcessEvent(eventData);
                }

                // Adaptive sleep:
                // - If events were present, yield without sleeping (Thread.Sleep(0)) so we
                //   catch any further events in the same game-write batch immediately.
                // - If the buffer was empty, sleep 1ms. This keeps CPU usage near-zero
                //   during idle (between shots, footsteps etc.) while still polling fast
                //   enough that we never miss the start of a new event batch.
                //
                // Note: Thread.Sleep(1) on Windows wakes at the multimedia timer resolution
                // (~1ms when timeBeginPeriod(1) is active, which MW5 sets while running).
                // Even without that, worst-case 15.6ms idle latency is fine — we only care
                // about sub-millisecond latency when events are actually flowing.
                if (events.Count > 0)
                    Thread.Sleep(0);
                else
                    Thread.Sleep(1);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                Log($"Error in read loop: {ex.Message}");
                Thread.Sleep(100);
            }
        }

        _mmfHandler.Disconnect();
    }

    private void ProcessEvent(MemoryMappedFileHandler.EventData eventData)
    {
        try
        {
            // MechShaker event codes from EventCode.cs
            const int EVENT_TORSO_TWIST = 1;
            const int EVENT_FOOTSTEP = 2;
            const int EVENT_TRACE = 3;        // Lasers, flamers, machine guns
            const int EVENT_PROJECTILE = 4;   // Autocannons, PPCs
            const int EVENT_MISSILES = 5;
            const int EVENT_AMS = 6;
            const int EVENT_MELEE = 7;
            const int EVENT_DROPSHIP = 8;
            const int EVENT_JUMPJETS = 9;
            const int EVENT_AIRBORNE = 10;
            const int EVENT_LANDED = 11;
            const int EVENT_MASC = 12;
            const int EVENT_POWERING = 13;
            const int EVENT_PART_DESTRUCTION = 14;
            const int EVENT_DAMAGED = 15;

            // Only log events that aren't spam (filter TorsoTwist, MASC, AMS)
            if (eventData.EventCode != EVENT_TORSO_TWIST &&
                eventData.EventCode != EVENT_MASC &&
                eventData.EventCode != EVENT_AMS)
            {
                Log($"Event: Code={eventData.EventCode}, Int0={eventData.Int0}, " +
                    $"F0={eventData.Float0:F2}, F1={eventData.Float1:F2}, F2={eventData.Float2:F2}, " +
                    $"F3={eventData.Float3:F2}, F4={eventData.Float4:F2}, F5={eventData.Float5:F2}");
            }

            switch (eventData.EventCode)
            {
                case EVENT_PROJECTILE:
                    // Autocannons and PPCs
                    // Int0 = WeaponId
                    // Float0 = NumberOfTimesToFire
                    // Float1 = DelayBetweenFiring
                    // Float2 = NumberOfProjectiles
                    // Float3 = Speed
                    // Float4 = IsPPC (0 = yes, other = no)
                    // Float5 = Impulse
                    var weaponEvent = new WeaponFireEvent
                    {
                        WeaponClass = eventData.Float4 == 0 ? WeaponClass.Energy : WeaponClass.Ballistic,
                        IsPPC = eventData.Float4 == 0,
                        NumberOfShots = (int)eventData.Float0,        // UAC/RAC burst count
                        DelayBetweenShots = eventData.Float1,         // Delay between burst shots
                        ProjectileMass = eventData.Float2,            // Number of projectiles per shot
                        MuzzleVelocity = eventData.Float3,            // Speed
                        Damage = eventData.Float5,                    // Impulse
                        Timestamp = 0
                    };
                    OnWeaponFire?.Invoke(this, weaponEvent);
                    EventsProcessed++;
                    LastEventTime = DateTime.Now;
                    break;

                case EVENT_TRACE:
                    // Lasers, flamers, machine guns
                    // Float0 = DamageOverDuration
                    // Float2 = RateOfFire (high for machine guns)
                    // Float3 = Beam duration in seconds
                    // Float5 = Active flag
                    var traceEvent = new WeaponFireEvent
                    {
                        WeaponClass = WeaponClass.Energy,
                        IsMachineGun = eventData.Float2 > 5.0f, // Machine guns have high RoF
                        IsActive = eventData.Float5 > 0.5f, // Active flag
                        ProjectileMass = 0.1f,
                        MuzzleVelocity = eventData.Float0, // DamageOverDuration
                        Damage = eventData.Float0,
                        BeamDuration = eventData.Float3, // Beam duration from F3
                        Timestamp = 0
                    };
                    OnWeaponFire?.Invoke(this, traceEvent);
                    EventsProcessed++;
                    LastEventTime = DateTime.Now;
                    break;

                case EVENT_MISSILES:
                    // Int0 = WeaponId
                    // Float0 = NumberOfMissiles
                    // Float1 = DelayBetweenMissiles (for streaks)
                    // Float3 = Impulse
                    var missileEvent = new WeaponFireEvent
                    {
                        WeaponClass = WeaponClass.Missile,
                        ProjectileMass = eventData.Float0, // Number of missiles
                        FiringDelay = eventData.Float1, // Delay between missiles
                        MuzzleVelocity = eventData.Float2, // Speed
                        Damage = eventData.Float3, // Impulse
                        Timestamp = 0
                    };
                    OnWeaponFire?.Invoke(this, missileEvent);
                    EventsProcessed++;
                    LastEventTime = DateTime.Now;
                    break;

                case EVENT_DAMAGED:
                    // Int0 = DamageType (0=Trace, 1=Projectile, 2=Missile, 3=Melee, 4=Explosion)
                    // Float0 = Damage amount
                    // Float2 = Beam duration in seconds (for laser/trace damage)
                    // Float3 = Direction angle in degrees (0-360°)
                    //   0° = Rear, 90° = Left, 180° = Front, 270° = Right

                    // Log all available data
                    Log($"DAMAGE EVENT: Type={eventData.Int0}, Amt={eventData.Float0:F2}, " +
                        $"F1={eventData.Float1:F2}, F2={eventData.Float2:F2}, F3={eventData.Float3:F2}, " +
                        $"F4={eventData.Float4:F2}, F5={eventData.Float5:F2}");

                    var damageEvent = new DamageEvent
                    {
                        DamageType = (DamageType)eventData.Int0,
                        DamageAmount = eventData.Float0,
                        BeamDuration = eventData.Float2, // Beam duration in seconds (laser/trace damage)
                        DirectionAngle = eventData.Float3, // Direction in degrees (0-360°)
                        Timestamp = 0
                    };
                    OnDamage?.Invoke(this, damageEvent);
                    EventsProcessed++;
                    LastEventTime = DateTime.Now;
                    break;


                case EVENT_FOOTSTEP:
                    // Int0 = MassInTons
                    // Float1 = SpeedInKmh
                    var movementEvent = new MovementEvent
                    {
                        Type = MovementType.Footstep,
                        MechTonnage = eventData.Int0,
                        Velocity = eventData.Float1,
                        FootSide = FootSide.Left, // TODO: Determine from data if available
                        Timestamp = 0
                    };
                    OnMovement?.Invoke(this, movementEvent);
                    EventsProcessed++;
                    LastEventTime = DateTime.Now;
                    break;

                case EVENT_JUMPJETS:
                    // Int0 = Active (1 = active)
                    var jumpEvent = new JumpJetEvent
                    {
                        State = eventData.Int0 == 1 ? JumpJetState.Active : JumpJetState.Inactive,
                        ThrustLevel = eventData.Int0 == 1 ? 1.0f : 0.0f,
                        MechTonnage = 0,
                        Timestamp = 0
                    };
                    OnJumpJet?.Invoke(this, jumpEvent);
                    EventsProcessed++;
                    LastEventTime = DateTime.Now;
                    break;

                case EVENT_LANDED:
                    // Int0 = MassInTons
                    // Float0 = AccelerationInKmh2
                    var impactEvent = new ImpactEvent
                    {
                        Type = ImpactType.Landing,
                        ImpactVelocity = eventData.Float0,
                        MechTonnage = eventData.Int0,
                        ImpactDirection = new System.Numerics.Vector3(0, -1, 0),
                        Timestamp = 0
                    };
                    OnImpact?.Invoke(this, impactEvent);
                    EventsProcessed++;
                    LastEventTime = DateTime.Now;
                    break;

                case EVENT_MELEE:
                    // Int0 = MassInTons
                    // Float0 = IsHit (1 = hit)
                    if (eventData.Float0 == 1)
                    {
                        var meleeEvent = new WeaponFireEvent
                        {
                            WeaponClass = WeaponClass.Melee,
                            ProjectileMass = eventData.Int0,
                            MuzzleVelocity = 10,
                            Damage = eventData.Int0 * 2,
                            Timestamp = 0
                        };
                        OnWeaponFire?.Invoke(this, meleeEvent);
                        EventsProcessed++;
                        LastEventTime = DateTime.Now;
                    }
                    break;

                case EVENT_TORSO_TWIST:
                    // Continuous event - ignore for FFB
                    break;

                case EVENT_AMS:
                case EVENT_DROPSHIP:
                case EVENT_AIRBORNE:
                case EVENT_MASC:
                case EVENT_POWERING:
                case EVENT_PART_DESTRUCTION:
                    // Not currently used for FFB
                    break;

                default:
                    if (eventData.EventCode != -1 && eventData.EventCode != -2)
                        Log($"Unknown event code: {eventData.EventCode}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"Error processing event: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        _mmfHandler.Dispose();
        _cancellationSource.Dispose();
        GC.SuppressFinalize(this);
    }
}