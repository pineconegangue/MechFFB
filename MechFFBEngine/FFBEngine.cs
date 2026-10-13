using System.Threading.Channels;
using MechFFBReader;
using MechFFBReader.Events;
using MechFFBEngine.Configuration;
using MechFFBEngine.Haptic;

namespace MechFFBEngine;

/// <summary>
/// Main FFB engine - coordinates telemetry reading and force feedback output.
///
/// Architecture (Phase 1 + Phase 2):
///   Telemetry read thread → Handle* (AccumulateBatch) → BatchTimer flushes →
///   EnqueueEffect (Channel&lt;Action&gt;) → single serialized FFB queue → DirectInput
///
/// Phase 1: All DirectInput calls are serialized through one queue with a single consumer
///          (non-blocking read loop). The async consumer may resume on different pool threads,
///          but actions never overlap - that, not thread identity, is what keeps it safe.
/// Phase 2: Events are accumulated in 10ms windows per batch-key. Same weapon/damage type
///          within the window is combined into a single effect (sum magnitudes, clamp to 32767).
///          This collapses e.g. 10 LBX pellets → 1 effect, cutting FFB thread work ~10×.
///
/// Continuous effects (machine guns, lasers, jump jets) are singletons — not batched.
/// </summary>
public class FFBEngine : IDisposable
{
    private readonly TelemetryReader _telemetryReader;
    private readonly FFBConfiguration _configuration;
    private readonly WeaponProfiles _weaponProfiles;
    private DirectInputHapticManager _hapticManager;

    // ── Phase 1: single serialized FFB queue ─────────────────────────────────
    // All DirectInput calls are enqueued here and drained on a single thread.
    private Channel<Action> _effectQueue = Channel.CreateUnbounded<Action>( // Recreated on restart - Stop() completes it for good
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private Task? _effectTask;
    private CancellationTokenSource? _effectLoopCts;

    // ── Async log queue ──────────────────────────────────────────────────────
    // Console.WriteLine blocks for 1-5ms on Windows (stdout lock + flush).
    // All hot-path logging is enqueued here and written by a dedicated background
    // thread so the read thread and Handle* methods are never stalled by I/O.
    private Channel<string> _logQueue = Channel.CreateUnbounded<string>( // Recreated on restart - Stop() completes it for good
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private Task? _logTask;
    private bool _queuesCompleted; // Set by Stop(); Start() then builds fresh queues

    // While stopped the queue is closed, so fall back to writing directly (rare, non-hot-path).
    private void Log(string message)
    {
        if (!_logQueue.Writer.TryWrite(message))
            Console.WriteLine(message);
    }

    private async Task LogLoop(CancellationToken token)
    {
        try
        {
            await foreach (var msg in _logQueue.Reader.ReadAllAsync(token))
                Console.WriteLine(msg);
        }
        catch (OperationCanceledException) { }
    }

    // ── Phase 2: event batching ───────────────────────────────────────────────
    // PendingBatch holds accumulated parameters for one logical weapon/damage event.
    // BatchKey uniquely identifies a "slot" — same slot within the batch window merges.

    private enum BatchKind { WeaponImpact, WeaponPeriodic, DamageImpact, DamagePeriodic, ExplosionDamage }

    private readonly record struct BatchKey(
        BatchKind Kind,
        int SubType,        // WeaponClass, DamageType, or special code
        int Direction       // centidegrees (bucketed for damage, fixed for recoil)
    );

    private class PendingBatch
    {
        public int Count;               // Number of events merged into this batch
        public long BaseMagnitudeSum;   // Sum of raw per-event magnitudes (unscaled, unclamped)
        public float RawDamageSum;      // Sum of raw DamageAmount values — used for damage-received magnitude
        public float DamageTypeIntensity; // TypeIntensity × MasterIntensity captured at event time for quadratic path
        public int Duration;            // First-event value; kept consistent per weapon type
        public int AttackMs;
        public int FadeMs;
        public int Direction;
        public int FrequencyHz;         // Periodic effects only
        public bool ApplyWeaponScaling; // True for weapon-fire batches, false for damage received
        public System.Threading.Timer? Timer;
    }

    private readonly Dictionary<BatchKey, PendingBatch> _pendingBatches = new();
    private readonly object _batchLock = new();
    private const int BATCH_WINDOW_MS = 10;         // Default flush window
    private const int MISSILE_BATCH_WINDOW_MS = 10;  // Same as default — logging was the delay, not the game

    // ── Continuous-effect state (not batched) ─────────────────────────────────
    private bool _isLeftFoot = true;

    // Canonical push directions in centidegrees, BEFORE the per-device axis mapping.
    // Reference = the original "Invert Axis ON" output, verified physically on the author's stick.
    // Device differences are handled only by AxisMapping in the haptic manager, never here.
    private const int DIR_LEFT = 0;
    private const int DIR_TOWARD_PILOT = 9000;
    private const int DIR_RIGHT = 18000;
    private const int DIR_AWAY_FROM_PILOT = 27000;

    // Sentinel for singleton effects: claimed on the read thread before EnqueueEffect, replaced
    // by the real ID on the FFB thread. Stops a second START (arriving before the FFB thread
    // runs) from creating a duplicate, and lets a STOP that arrives first cancel the create.
    private const int EFFECT_PENDING = int.MaxValue;

    private int _activeJumpJetEffectId = -1;
    private System.Threading.Timer? _machineGunTimer = null;
    private bool _machineGunsActive = false;
    private int _currentMachineGunMagnitude = 0;
    private readonly object _machineGunLock = new(); // Read thread and MG timer both start/stop the MG pulse train
    private DateTime _lastMachineGunActiveTime = DateTime.MinValue;
    private const int MACHINE_GUN_STALE_MS = 10000; // Safety stop if no "firing" event arrives for this long

    private int _activeContinuousBeamEffectId = -1;
    private int _currentContinuousBeamMagnitude = 0;

    private int _activeLaserDamageEffectId = -1;
    private const int LASER_DAMAGE_PENDING = int.MaxValue; // Sentinel: EnqueueEffect sent, FFB thread not yet assigned real ID
    private System.Threading.Timer? _laserDamageCleanupTimer = null;
    private DateTime _laserBeamEndTime = DateTime.MinValue; // Absolute time the current laser beam session should stop
    private const int LASER_DAMAGE_FALLBACK_MS = 500; // Fallback beam duration if telemetry omits it

    private int _activeMachineGunDamageEffectId = -1;
    private DateTime _lastMachineGunDamageTime = DateTime.MinValue;
    private const int MACHINE_GUN_DAMAGE_WINDOW_MS = 50;

    // ── Streak missile tracking ───────────────────────────────────────────────
    private readonly List<int> _activeStreakEffects = new();

    // ── Public API ────────────────────────────────────────────────────────────
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Per-event detail logging (every weapon fire, damage hit, batch flush, footstep...).
    /// Off by default - each line costs the UI a rebuild of the debug log. Setup, connection,
    /// errors and safety stops are always logged.
    /// </summary>
    public bool VerboseLogging
    {
        get => _verbose;
        set
        {
            _verbose = value;
            _telemetryReader.VerboseLogging = value;
            Log($"Verbose logging: {(value ? "ON" : "OFF")}");
        }
    }
    private volatile bool _verbose;
    public FFBConfiguration Configuration => _configuration;

    /// <summary>Telemetry events processed since the app started (written by the read thread, polled by the UI).</summary>
    public int EventsProcessed => _telemetryReader.EventsProcessed;

    /// <summary>True while attached to the MechShakerBridge shared memory, i.e. MW5 is running with the mod.</summary>
    public bool IsTelemetryConnected => _telemetryReader.IsConnected;
    public event EventHandler<string>? OnStatusChanged;
    public event EventHandler<string>? OnError;

    // ─────────────────────────────────────────────────────────────────────────
    // Construction / device API
    // ─────────────────────────────────────────────────────────────────────────

    public FFBEngine()
    {
        Console.WriteLine("=== MechFFB Engine Initializing (DirectInput) ===");
        _telemetryReader = new TelemetryReader();
        Console.WriteLine("TelemetryReader created");
        _configuration = FFBConfiguration.Load();
        Console.WriteLine("Configuration loaded");
        _weaponProfiles = WeaponProfiles.Load();
        _hapticManager = new DirectInputHapticManager();
        Console.WriteLine("DirectInputHapticManager created");
    }

    public void SetWindowHandle(IntPtr windowHandle)
    {
        _hapticManager.WindowHandle = windowHandle;
        Log($"Window handle set: {windowHandle}");
    }

    /// <summary>Set how canonical directions map onto the selected device's axes (see AxisMapping).</summary>
    public void SetAxisMapping(AxisMapping mapping)
    {
        _hapticManager.AxisMapping = mapping.Clone();
        Log($"Axis mapping: {mapping}");
    }

    /// <summary>Number of force axes on the selected device (1 = direction can't be expressed).</summary>
    public int ForceAxisCount => _hapticManager.ForceAxisCount;

    /// <summary>
    /// Calibration: a short push along a canonical direction, bypassing the axis mapping.
    /// Blocks for ~0.65 s - call off the UI thread. Refused while the engine is running.
    /// </summary>
    public bool PlayCalibrationPush(int canonicalDirection)
        => !IsRunning && _hapticManager.PlayRawPush(canonicalDirection);

    public void SetEnableExclusiveMode(bool enable)
    {
        _hapticManager.EnableExclusiveMode = enable;
        Log($"Exclusive mode: {(enable ? "ENABLED" : "DISABLED")} (applies when the device is next opened)");
    }

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

    public List<DirectInputHapticManager.HapticDeviceInfo> GetAvailableDevices()
        => _hapticManager.GetHapticDevices();

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

    public void TestDevice()
    {
        try
        {
            OnStatusChanged?.Invoke(this, "Running test effect...");
            if (_hapticManager.TestDevice())
                OnStatusChanged?.Invoke(this, "Test effect completed - you should have felt it!");
            else
                OnError?.Invoke(this, "Test effect failed - check console for details");
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, $"Test failed: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Start / Stop
    // ─────────────────────────────────────────────────────────────────────────

    public void Start()
    {
        if (IsRunning) return;

        if (!_hapticManager.HasDeviceSelected)
        {
            OnError?.Invoke(this, "No FFB device selected");
            return;
        }

        // Stop() permanently completes both queues, so a restart needs fresh ones.
        // (The first Start keeps the original log queue so pre-start log lines still flush.)
        if (_queuesCompleted)
        {
            _effectQueue = Channel.CreateUnbounded<Action>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            _logQueue = Channel.CreateUnbounded<string>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            _queuesCompleted = false;
        }

        // Start the FFB queue consumer before subscribing to events (Phase 1)
        var cts = new CancellationTokenSource();
        _effectTask = Task.Factory.StartNew(
            () => EffectLoop(cts.Token),
            cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        _effectLoopCts = cts;

        // Start background log writer — keeps Console.WriteLine off the hot path
        _logTask = Task.Factory.StartNew(
            () => LogLoop(cts.Token),
            cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        _telemetryReader.OnWeaponFire += HandleWeaponFire;
        _telemetryReader.OnDamage += HandleDamage;
        _telemetryReader.OnMovement += HandleMovement;
        _telemetryReader.OnJumpJet += HandleJumpJet;
        _telemetryReader.OnImpact += HandleImpact;
        _telemetryReader.Start();

        IsRunning = true;
        OnStatusChanged?.Invoke(this, "FFB engine started - waiting for MW5 telemetry...");
    }

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

        // Cancel all pending batch timers so they don't fire after Stop()
        lock (_batchLock)
        {
            foreach (var batch in _pendingBatches.Values)
                batch.Timer?.Dispose();
            _pendingBatches.Clear();
        }

        // Drain and shut down the effect queue, then the log queue
        _effectQueue.Writer.TryComplete();
        _logQueue.Writer.TryComplete();
        _effectLoopCts?.Cancel();
        _effectTask?.Wait(TimeSpan.FromSeconds(2));
        _logTask?.Wait(TimeSpan.FromSeconds(2));
        _effectLoopCts?.Dispose();
        _effectLoopCts = null;
        _queuesCompleted = true;

        // The FFB thread has exited, so it's safe to touch the device directly here.
        // Reset all continuous-effect state so a later Start() begins clean, then release every
        // effect still held. Dispose-only (no Stop) per the DestroyEffect invariant; this also
        // catches effects whose delayed cleanup was dropped because the queue had closed.
        lock (_machineGunLock)
        {
            _machineGunsActive = false;
            _machineGunTimer?.Dispose();
            _machineGunTimer = null;
            _currentMachineGunMagnitude = 0;
        }
        _laserDamageCleanupTimer?.Dispose();
        _laserDamageCleanupTimer = null;
        _laserBeamEndTime = DateTime.MinValue;
        _activeJumpJetEffectId = -1;
        _activeContinuousBeamEffectId = -1;
        _currentContinuousBeamMagnitude = 0;
        _activeLaserDamageEffectId = -1;
        _activeMachineGunDamageEffectId = -1;
        lock (_activeStreakEffects) _activeStreakEffects.Clear();

        _hapticManager.DestroyAllEffects();
        OnStatusChanged?.Invoke(this, "FFB engine stopped");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Phase 1: single serialized FFB queue
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enqueue a DirectInput action onto the serialized FFB queue.
    /// Handle* methods must never call _hapticManager directly — always via here.
    /// </summary>
    private void EnqueueEffect(Action action)
        => _effectQueue.Writer.TryWrite(action);

    private async Task EffectLoop(CancellationToken token)
    {
        try
        {
            await foreach (var action in _effectQueue.Reader.ReadAllAsync(token))
            {
                try { action(); }
                catch (Exception ex) { Log($"Effect error: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Phase 2: batching helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Accumulate a magnitude into an existing batch or start a new one.
    /// When BATCH_WINDOW_MS elapses with no new events for this key,
    /// FlushBatch fires and creates exactly one DirectInput effect.
    ///
    /// applyWeaponScaling: when true, the final magnitude uses the compressive scaling curve
    ///   multiplier = 2 - 1/N^0.6  (N=1→1.0×, N=2→1.34×, N=10→1.75×, N=∞→2.0×)
    /// This is applied to weapon-fire batches only — damage received is already one event
    /// per hit so the raw sum is appropriate there.
    /// </summary>
    private void AccumulateBatch(
        BatchKey key,
        int magnitude,
        int duration,
        int attackMs,
        int fadeMs,
        int direction,
        int frequencyHz = 0,
        bool applyWeaponScaling = false,
        int batchWindowMs = BATCH_WINDOW_MS,
        float rawDamage = 0f,
        float damageTypeIntensity = 0f)
    {
        lock (_batchLock)
        {
            if (_pendingBatches.TryGetValue(key, out var existing))
            {
                // Accumulate raw magnitude and count — scaling applied at flush time
                existing.Count++;
                existing.BaseMagnitudeSum += magnitude;
                existing.RawDamageSum += rawDamage;
                // Keep the longest duration so a short weapon firing alongside a longer one
                // doesn't cut the effect off early (e.g. medium pulse laser + small laser)
                if (duration > existing.Duration)
                    existing.Duration = duration;
            }
            else
            {
                // New batch slot — arm a one-shot flush timer
                var batch = new PendingBatch
                {
                    Count = 1,
                    BaseMagnitudeSum = magnitude,
                    RawDamageSum = rawDamage,
                    DamageTypeIntensity = damageTypeIntensity,
                    Duration = duration,
                    AttackMs = attackMs,
                    FadeMs = fadeMs,
                    Direction = direction,
                    FrequencyHz = frequencyHz,
                    ApplyWeaponScaling = applyWeaponScaling
                };

                var captureKey = key;
                batch.Timer = new System.Threading.Timer(
                    _ => FlushBatch(captureKey),
                    null,
                    batchWindowMs,
                    Timeout.Infinite);

                _pendingBatches[key] = batch;
            }
        }
    }

    /// <summary>
    /// Called by the batch timer. Removes the batch and enqueues one DirectInput effect.
    /// </summary>
    private void FlushBatch(BatchKey key)
    {
        PendingBatch? batch;
        lock (_batchLock)
        {
            if (!_pendingBatches.TryGetValue(key, out batch))
                return;

            batch.Timer?.Dispose();
            _pendingBatches.Remove(key);
        }

        // Compute final magnitude.
        //
        // Weapon-fire batches: use count-scaling curve (N simultaneous weapons firing)
        //   Formula: multiplier = 2 - 1/N^0.6
        //   N=1→1.00×  N=2→1.34×  N=5→1.62×  N=10→1.75×  N→∞→2.00×
        //
        // Damage-received batches: use quadratic formula on total damage sum so that
        //   LBX/10 (10×1dmg = 10 total) feels identical to AC/10 (1×10dmg = 10 total).
        //   Formula: max(slope × totalDamage^power, floor) × TypeIntensity
        //   slope=5200.8, power=0.585, floor=10000 (see DAMAGE_* constants below)
        int count = batch.Count;
        double scaledMag;
        if (batch.ApplyWeaponScaling && count > 1)
        {
            double multiplier = 2.0 - 1.0 / Math.Pow(count, 0.6);
            double avgMag = (double)batch.BaseMagnitudeSum / count;
            scaledMag = avgMag * multiplier;
        }
        else if (!batch.ApplyWeaponScaling && batch.RawDamageSum > 0)
        {
            // Damage-received: quadratic on total damage, independent of pellet count
            const double DAMAGE_SLOPE = 5200.8;
            const double DAMAGE_POWER = 0.585;
            const double DAMAGE_FLOOR = 10000.0;
            scaledMag = Math.Max(DAMAGE_SLOPE * Math.Pow(batch.RawDamageSum, DAMAGE_POWER), DAMAGE_FLOOR);
            // Apply type intensity and master intensity (captured at event time)
            if (batch.DamageTypeIntensity > 0)
                scaledMag *= batch.DamageTypeIntensity;
        }
        else
        {
            scaledMag = batch.BaseMagnitudeSum;
        }
        int mag = Math.Clamp((int)scaledMag, 0, 32767);

        // Snapshot remaining fields for lambda capture
        int dur = batch.Duration;
        int atk = batch.AttackMs;
        int fade = batch.FadeMs;
        int dir = batch.Direction;
        int freq = batch.FrequencyHz;
        var kind = key.Kind;

        if (batch.ApplyWeaponScaling && count > 1)
        {
            double multiplier = 2.0 - 1.0 / Math.Pow(count, 0.6);
            if (_verbose) Log($"[Batch FLUSH] {key.Kind}/{key.SubType} N={count} scale={multiplier:F2}× mag={mag}");
        }
        else
        {
            if (_verbose) Log($"[Batch FLUSH] {key.Kind}/{key.SubType} dir={dir / 100}° mag={mag}");
        }

        switch (kind)
        {
            case BatchKind.WeaponImpact:
            case BatchKind.DamageImpact:
                EnqueueEffect(() =>
                {
                    int id = _hapticManager.CreateImpactEffect(mag, dur, atk, fade, dir);
                    if (id >= 0)
                    {
                        _hapticManager.PlayEffect(id, 1);
                        Task.Delay(dur + fade + 50).ContinueWith(_ => EnqueueEffect(() => _hapticManager.DestroyEffect(id)));
                    }
                });
                break;

            case BatchKind.WeaponPeriodic:
            case BatchKind.DamagePeriodic:
                EnqueueEffect(() =>
                {
                    int id = _hapticManager.CreatePeriodicEffect(mag, dur, freq, atk, fade);
                    if (id >= 0)
                    {
                        _hapticManager.PlayEffect(id, 1);
                        Task.Delay(dur + fade + 50).ContinueWith(_ => EnqueueEffect(() => _hapticManager.DestroyEffect(id)));
                    }
                });
                break;

            case BatchKind.ExplosionDamage:
                // Re-read explosion config at flush time so slider tweaks take effect immediately
                var explosionAdv = _configuration.Advanced.ExplosionDamageAdvanced;
                EnqueueEffect(() =>
                {
                    var ids = _hapticManager.CreateExplosionEffect(
                        mag,
                        explosionAdv.ImpactDuration,
                        explosionAdv.RumbleDuration,
                        explosionAdv.ImpactAttackTime,
                        explosionAdv.ImpactFadeTime,
                        explosionAdv.RumbleAttackTime,
                        explosionAdv.RumbleFadeTime,
                        dir,
                        explosionAdv.RumbleFrequency,
                        explosionAdv.ImpactMultiplier,
                        explosionAdv.RumbleMultiplier
                    );

                    if (ids[0] >= 0 && ids[1] >= 0)
                    {
                        _hapticManager.PlayEffect(ids[0], 1);
                        _hapticManager.PlayEffect(ids[1], 1);

                        int maxDur = Math.Max(
                            explosionAdv.ImpactDuration + explosionAdv.ImpactFadeTime,
                            explosionAdv.RumbleDuration + explosionAdv.RumbleFadeTime);

                        Task.Delay(maxDur + 50).ContinueWith(_ => EnqueueEffect(() =>
                        {
                            _hapticManager.DestroyEffect(ids[0]);
                            _hapticManager.DestroyEffect(ids[1]);
                        }));
                    }
                    else
                    {
                        // One half failed to create - don't leak the other (DestroyEffect ignores -1)
                        _hapticManager.DestroyEffect(ids[0]);
                        _hapticManager.DestroyEffect(ids[1]);
                    }
                });
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Event handlers
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleWeaponFire(object? sender, WeaponFireEvent e)
    {
        int magnitude = CalculateRecoilMagnitude(e);

        // Recoil always pulls the stick toward the pilot (device differences: AxisMapping)
        const int recoilDirection = DIR_TOWARD_PILOT;

        switch (e.WeaponClass)
        {
            // ── Ballistic ────────────────────────────────────────────────────
            case WeaponClass.Ballistic:
                {
                    var ballistic = _configuration.Advanced.Ballistics;

                    // Burst-fire weapons (UAC/RAC): create individual timed effects
                    if (e.NumberOfShots > 1 && e.DelayBetweenShots > 0)
                    {
                        int shotCount = e.NumberOfShots;
                        float delaySeconds = e.DelayBetweenShots;
                        int delayMs = (int)(delaySeconds * 1000);

                        // For burst weapons: effect duration = shot spacing (no overlap)
                        // This creates distinct rapid kicks instead of blended rumble
                        int burstDuration = (int)(delayMs * 0.7);     // 70% of spacing for duration
                        int burstAttack = Math.Max(5, delayMs / 8);   // Quick attack (~12% of spacing)
                        int burstFade = (int)(delayMs * 0.3);         // 30% of spacing for fade

                        // Clamp to reasonable minimums
                        burstDuration = Math.Max(20, burstDuration);
                        burstAttack = Math.Max(5, burstAttack);
                        burstFade = Math.Max(10, burstFade);

                        if (_verbose) Log($"BURST WEAPON: {shotCount} shots @ {delayMs}ms intervals, mag={magnitude}, dur={burstDuration}ms, atk={burstAttack}ms, fade={burstFade}ms");

                        // Fire each shot with progressive delay
                        for (int i = 0; i < shotCount; i++)
                        {
                            int shotIndex = i;
                            int shotDelay = shotIndex * delayMs;
                            int captureMag = magnitude;
                            int captureDur = burstDuration;
                            int captureAtk = burstAttack;
                            int captureFade = burstFade;

                            // Schedule this shot's effect
                            Task.Delay(shotDelay).ContinueWith(_ =>
                            {
                                EnqueueEffect(() =>
                                {
                                    int id = _hapticManager.CreateImpactEffect(captureMag, captureDur, captureAtk, captureFade, recoilDirection);
                                    if (id >= 0)
                                    {
                                        _hapticManager.PlayEffect(id, 1);
                                        Task.Delay(captureDur + captureFade + 50).ContinueWith(__ =>
                                            EnqueueEffect(() => _hapticManager.DestroyEffect(id)));
                                    }
                                });
                            });
                        }
                        return;
                    }

                    // Single-shot ballistics: use batching as normal
                    var key = new BatchKey(BatchKind.WeaponImpact, (int)WeaponClass.Ballistic, recoilDirection);
                    AccumulateBatch(key, magnitude, ballistic.Duration, ballistic.AttackTime, ballistic.FadeTime, recoilDirection, applyWeaponScaling: true);
                    return;
                }

            case WeaponClass.Energy:
                {
                    // ── PPC ──────────────────────────────────────────────────────
                    if (e.IsPPC)
                    {
                        var ppc = _configuration.Advanced.PPCs;
                        // SubType 100 distinguishes PPC from plain ballistic in the same direction slot
                        var key = new BatchKey(BatchKind.WeaponImpact, SubType: 100, recoilDirection);
                        AccumulateBatch(key, magnitude, ppc.Duration, ppc.AttackTime, ppc.FadeTime, recoilDirection, applyWeaponScaling: true);
                        return;
                    }

                    // ── Machine Gun (continuous singleton — NOT batched) ──────────
                    if (e.IsMachineGun)
                    {
                        lock (_machineGunLock)
                        {
                            _currentMachineGunMagnitude = magnitude;

                            if (e.IsActive)
                            {
                                _lastMachineGunActiveTime = DateTime.Now;
                                var mg = _configuration.Advanced.MachineGuns;
                                if (!_machineGunsActive || _machineGunTimer == null)
                                {
                                    _machineGunsActive = true;
                                    _machineGunTimer?.Dispose();
                                    FireMachineGunPulse(_currentMachineGunMagnitude, mg);
                                    _machineGunTimer = new System.Threading.Timer(_ => MachineGunTick(), null, 93, 93);
                                    if (_verbose) Log($"Machine Gun: START/RESTART continuous fire, magnitude={_currentMachineGunMagnitude}");
                                }
                                // else: already firing, magnitude field updated above
                            }
                            else if (_machineGunsActive)
                            {
                                StopMachineGunsLocked("STOP");
                            }
                        }
                        return;
                    }

                    // ── Continuous beam laser (START/STOP singleton — NOT batched) ─
                    if (e.BeamDuration == 0 && e.IsActive)
                    {
                        _currentContinuousBeamMagnitude = magnitude;
                        // Claim the slot on the read thread (-1 → PENDING) so a second START that
                        // arrives before the FFB thread runs can't create a duplicate effect.
                        if (Interlocked.CompareExchange(ref _activeContinuousBeamEffectId, EFFECT_PENDING, -1) == -1)
                        {
                            var laser = _configuration.Advanced.Lasers;
                            int captureMag = _currentContinuousBeamMagnitude;
                            EnqueueEffect(() =>
                            {
                                if (_activeContinuousBeamEffectId != EFFECT_PENDING)
                                    return; // STOP arrived first - nothing to start

                                int id = _hapticManager.CreatePeriodicEffect(captureMag, 10000, laser.Frequency, laser.AttackTime, laser.FadeTime);
                                if (id < 0)
                                {
                                    Interlocked.CompareExchange(ref _activeContinuousBeamEffectId, -1, EFFECT_PENDING); // allow retry
                                    return;
                                }
                                if (Interlocked.CompareExchange(ref _activeContinuousBeamEffectId, id, EFFECT_PENDING) != EFFECT_PENDING)
                                {
                                    _hapticManager.DestroyEffect(id); // STOP landed while we were creating
                                    return;
                                }
                                _hapticManager.PlayEffect(id, 1);
                                if (_verbose) Log($"Continuous Beam: START, Frequency={laser.Frequency}Hz, Magnitude={captureMag}");
                            });
                        }
                        return;
                    }

                    if (e.BeamDuration == 0 && !e.IsActive)
                    {
                        int captureId = Interlocked.Exchange(ref _activeContinuousBeamEffectId, -1);
                        if (captureId >= 0)
                        {
                            _currentContinuousBeamMagnitude = 0;
                            if (_verbose) Log("Continuous Beam: STOP");
                            if (captureId != EFFECT_PENDING) // PENDING: the queued create sees -1 and skips
                            {
                                EnqueueEffect(() =>
                                {
                                    _hapticManager.StopEffect(captureId);
                                    _hapticManager.DestroyEffect(captureId);
                                });
                            }
                        }
                        return;
                    }

                    // ── Regular laser (batched periodic) ─────────────────────────
                    if (e.BeamDuration > 0)
                    {
                        var laser = _configuration.Advanced.Lasers;
                        int duration = (int)(e.BeamDuration * 1000);
                        // SubType 200 = laser periodic — direction irrelevant, use 0
                        var key = new BatchKey(BatchKind.WeaponPeriodic, SubType: 200, Direction: 0);
                        AccumulateBatch(key, magnitude, duration, laser.AttackTime, laser.FadeTime, 0, laser.Frequency, applyWeaponScaling: true);
                    }
                    return;
                }

            // ── Missile ──────────────────────────────────────────────────────
            case WeaponClass.Missile:
                {
                    var missile = _configuration.Advanced.Missiles;
                    bool isPotentialStreak = e.FiringDelay > 0.01f;

                    if (isPotentialStreak)
                    {
                        // Streak missiles cancel each other — individual effects, not batched
                        int captureMag = magnitude;
                        EnqueueEffect(() =>
                        {
                            lock (_activeStreakEffects)
                            {
                                foreach (var sid in _activeStreakEffects)
                                {
                                    _hapticManager.StopEffect(sid);
                                    _hapticManager.DestroyEffect(sid);
                                }
                                _activeStreakEffects.Clear();
                            }
                        });

                        EnqueueEffect(() =>
                        {
                            int id = _hapticManager.CreatePeriodicEffect(captureMag, missile.Duration, missile.Frequency, missile.AttackTime, missile.FadeTime);
                            if (id >= 0)
                            {
                                _hapticManager.PlayEffect(id, 1);
                                lock (_activeStreakEffects) _activeStreakEffects.Add(id);

                                int totalDuration = missile.Duration + missile.FadeTime;
                                Task.Delay(totalDuration + 10).ContinueWith(_ => EnqueueEffect(() =>
                                {
                                    lock (_activeStreakEffects) _activeStreakEffects.Remove(id);
                                    _hapticManager.DestroyEffect(id);
                                }));
                            }
                        });
                    }
                    else
                    {
                        // Non-streak missiles — batched periodic
                        var key = new BatchKey(BatchKind.WeaponPeriodic, SubType: (int)WeaponClass.Missile, Direction: 0);
                        AccumulateBatch(key, magnitude, missile.Duration, missile.AttackTime, missile.FadeTime, 0, missile.Frequency, applyWeaponScaling: true, batchWindowMs: MISSILE_BATCH_WINDOW_MS);
                    }
                    return;
                }

            // ── Melee ────────────────────────────────────────────────────────
            case WeaponClass.Melee:
                {
                    var melee = _configuration.Advanced.Melee;
                    var key = new BatchKey(BatchKind.WeaponImpact, (int)WeaponClass.Melee, recoilDirection);
                    AccumulateBatch(key, magnitude, melee.Duration, melee.AttackTime, melee.FadeTime, recoilDirection, applyWeaponScaling: true);
                    return;
                }
        }
    }

    private void HandleDamage(object? sender, DamageEvent e)
    {
        var now = DateTime.Now;

        // Direction mapping: Blueprint gives Rear=0°, Left=90°, Front=180°, Right=270° (where the
        // hit came from). The stick is pushed AWAY from the hit: front → toward pilot (9000),
        // right → left (0), rear → away (27000), left → right (18000). Device differences: AxisMapping.
        float diAngle = (270 - e.DirectionAngle + 360) % 360;
        int direction = (int)(diAngle * 100);

        // Magnitude placeholder — damage-received events pass RawDamageSum through the batch
        // and compute final magnitude in FlushBatch using a quadratic formula on total damage.
        // This ensures LBX/10 (10×1dmg) feels the same as AC/10 (1×10dmg) because both
        // accumulate the same total damage sum. We still need a magnitude value here for the
        // per-type intensity multiplier path below, so use a simple linear formula.
        float baseMag = e.DamageAmount * 1500;
        int magnitude = Math.Clamp((int)(baseMag * _configuration.MasterIntensity), 0, 32767);

        // ── Explosion ─────────────────────────────────────────────────────────
        if ((int)e.DamageType == 4)
        {
            float intensity = _configuration.Simple.ExplosionDamageIntensity;
            int explosionMag = Math.Clamp((int)(magnitude * intensity), 0, 32767);

            if (_verbose) Log($"DAMAGE: Type=Explosion, Amount={e.DamageAmount:F1}, GameDir={e.DirectionAngle:F1}°, DIDir={direction / 100:F1}°, Mag={explosionMag}");

            // Bucket direction so nearby angles on the same attack merge
            int dirBucket = BucketDirection(direction);
            var key = new BatchKey(BatchKind.ExplosionDamage, SubType: 4, dirBucket);
            AccumulateBatch(key, explosionMag, duration: 0, attackMs: 0, fadeMs: 0, dirBucket);
            return;
        }

        // Per-type intensity multiplier
        float damageIntensity = (int)e.DamageType switch
        {
            0 => _configuration.Simple.LaserMGDamageIntensity,
            1 => _configuration.Simple.ProjectileDamageIntensity,
            2 => _configuration.Simple.MissileDamageIntensity,
            3 => _configuration.Simple.MeleeDamageIntensity,
            _ => 0.6f
        };

        magnitude = Math.Clamp((int)(magnitude * damageIntensity), 0, 32767);

        if (_verbose) Log($"DAMAGE: Type={e.DamageType}, Amount={e.DamageAmount:F1}, GameDir={e.DirectionAngle:F1}°, DIDir={direction / 100:F1}°, Mag={magnitude}");

        var settings = e.DamageType switch
        {
            (DamageType)0 => _configuration.Advanced.LaserMGDamage,
            (DamageType)1 => _configuration.Advanced.ProjectileDamage,
            (DamageType)2 => _configuration.Advanced.MissileDamage,
            (DamageType)3 => _configuration.Advanced.MeleeDamage,
            _ => _configuration.Advanced.ProjectileDamage
        };

        switch ((int)e.DamageType)
        {
            // ── Trace: Laser / Machine Gun damage ────────────────────────────
            case 0:
                {
                    // Lasers always carry a BeamDuration (F2 > 0); MG bullets never do.
                    // Using damage amount (< 1.0f) was unreliable — laser beams can arrive
                    // with very low damage on their first tick (~0.07) which incorrectly
                    // triggered the MG path, causing a false MG burst at the start of every
                    // laser hit and interrupting the beam feel with a hiccup.
                    bool isMachineGunDamage = e.BeamDuration <= 0;

                    if (isMachineGunDamage)
                    {
                        // MG damage — continuous singleton, not batched
                        bool isMGBurstContinuing = (now - _lastMachineGunDamageTime).TotalMilliseconds < MACHINE_GUN_DAMAGE_WINDOW_MS;
                        _lastMachineGunDamageTime = now;

                        if (!isMGBurstContinuing)
                        {
                            // Previous burst ended - release its effect (if it has been created yet)
                            int previousId = Interlocked.Exchange(ref _activeMachineGunDamageEffectId, -1);
                            if (previousId >= 0 && previousId != EFFECT_PENDING)
                            {
                                if (_verbose) Log("Machine Gun Damage: Previous burst ended, creating new effect");
                                EnqueueEffect(() =>
                                {
                                    _hapticManager.StopEffect(previousId);
                                    _hapticManager.DestroyEffect(previousId);
                                });
                            }
                        }

                        // Claim the slot on the read thread (-1 → PENDING) - see EFFECT_PENDING.
                        // Two hits arriving before the FFB thread runs no longer create two effects.
                        if (Interlocked.CompareExchange(ref _activeMachineGunDamageEffectId, EFFECT_PENDING, -1) == -1)
                        {
                            var mgDmg = _configuration.Advanced.LaserMGDamage;
                            int captureMag = magnitude;
                            EnqueueEffect(() =>
                            {
                                if (_activeMachineGunDamageEffectId != EFFECT_PENDING)
                                    return; // Slot released or already filled before we ran

                                int id = _hapticManager.CreatePeriodicEffect(captureMag, 200, mgDmg.Frequency, mgDmg.AttackTime, mgDmg.FadeTime);
                                if (id < 0)
                                {
                                    Interlocked.CompareExchange(ref _activeMachineGunDamageEffectId, -1, EFFECT_PENDING); // allow retry
                                    return;
                                }
                                if (Interlocked.CompareExchange(ref _activeMachineGunDamageEffectId, id, EFFECT_PENDING) != EFFECT_PENDING)
                                {
                                    _hapticManager.DestroyEffect(id);
                                    return;
                                }
                                _hapticManager.PlayEffect(id, 1);
                                if (_verbose) Log($"Machine Gun Damage: NEW burst, Mag={captureMag}");

                                Task.Delay(MACHINE_GUN_DAMAGE_WINDOW_MS + 100).ContinueWith(_ => EnqueueEffect(() =>
                                {
                                    // Release only if this effect still owns the slot
                                    if (Interlocked.CompareExchange(ref _activeMachineGunDamageEffectId, -1, id) == id)
                                        _hapticManager.DestroyEffect(id);
                                }));
                            });
                        }
                        else
                        {
                            if (_verbose) Log($"Machine Gun Damage: CONTINUING burst, Mag={magnitude}");
                        }
                        return;
                    }
                    else
                    {
                        // Laser damage — continuous singleton effect.
                        //
                        // Design:
                        //   - The DirectInput effect is always created with a 10-second duration so
                        //     it NEVER self-expires. Our cleanup timer is the sole authority on when
                        //     the effect stops. This prevents the mid-beam pause caused by DirectInput
                        //     killing a short-duration effect while the timer was still extending.
                        //   - Each damage tick carries the full beam duration in BeamDuration (Float2).
                        //     We track _laserBeamEndTime = the absolute time the current beam session
                        //     should end. On every tick we push it forward to max(current end, now +
                        //     beamDuration), then set the cleanup timer to fire at exactly that time.
                        //   - _activeLaserDamageEffectId is only cleared on the FFB thread (inside
                        //     the EnqueueEffect lambda), never on the timer thread, eliminating the
                        //     race where a new hit could create a second effect that the queued cleanup
                        //     then immediately destroyed.

                        int beamDurMs = e.BeamDuration > 0
                            ? (int)(e.BeamDuration * 1000)
                            : LASER_DAMAGE_FALLBACK_MS;

                        if (_activeLaserDamageEffectId == -1)
                        {
                            // No beam running and none pending — start one.
                            //
                            // Set sentinel IMMEDIATELY on the read thread before EnqueueEffect.
                            // This prevents a second simultaneous damage event (e.g. 2 lasers
                            // arriving within microseconds) from also taking this branch and
                            // enqueuing a second CreatePeriodicEffect. Without this, both events
                            // see _activeLaserDamageEffectId == -1, both enqueue a Create, and
                            // two 10-second effects play — only one gets stopped by the timer.
                            _activeLaserDamageEffectId = LASER_DAMAGE_PENDING;

                            // End time is set ONCE here to now+beamDuration and never moved.
                            // Subsequent ticks are heartbeats only — they do not extend the end
                            // time, preventing N-tick × interval linger accumulation.
                            _laserBeamEndTime = now.AddMilliseconds(beamDurMs);
                            int remainingMs = beamDurMs;

                            var laserDmg = _configuration.Advanced.LaserMGDamage;
                            // Use quadratic formula on raw damage for consistency with other damage types
                            const double LASER_SLOPE = 5200.8;
                            const double LASER_POWER = 0.585;
                            const int LASER_FLOOR = 10000;
                            int laserRawMag = (int)Math.Max(
                                LASER_SLOPE * Math.Pow(e.DamageAmount, LASER_POWER),
                                LASER_FLOOR);
                            int captureMag = Math.Clamp(
                                (int)(laserRawMag * _configuration.Simple.LaserMGDamageIntensity * _configuration.MasterIntensity),
                                0, 32767);
                            EnqueueEffect(() =>
                            {
                                int id = _hapticManager.CreatePeriodicEffect(captureMag, 10000, laserDmg.Frequency, laserDmg.AttackTime, laserDmg.FadeTime);
                                if (id >= 0)
                                {
                                    _activeLaserDamageEffectId = id;
                                    _hapticManager.PlayEffect(id, 1);
                                    if (_verbose) Log($"Laser Damage: START beam, Mag={captureMag}, Dur={remainingMs}ms");
                                }
                                else
                                {
                                    _activeLaserDamageEffectId = -1; // Creation failed — allow retry
                                    Log("Laser Damage: effect creation failed");
                                }
                            });

                            // Persistent timer — never self-disposes inside its own callback.
                            // This avoids the race where Change() is called on a self-disposed
                            // timer and silently fails, leaving the 10s effect running forever.
                            _laserDamageCleanupTimer?.Dispose();
                            _laserDamageCleanupTimer = new System.Threading.Timer(_ =>
                            {
                                int msRemaining = (int)(_laserBeamEndTime - DateTime.Now).TotalMilliseconds;
                                if (msRemaining > 5)
                                {
                                    _laserDamageCleanupTimer?.Change(msRemaining, Timeout.Infinite);
                                    return;
                                }
                                EnqueueEffect(() =>
                                {
                                    int idToStop = _activeLaserDamageEffectId;
                                    _activeLaserDamageEffectId = -1;
                                    _laserBeamEndTime = DateTime.MinValue;
                                    if (idToStop >= 0 && idToStop != LASER_DAMAGE_PENDING)
                                    {
                                        // DestroyEffect only — no StopEffect first.
                                        // Calling effect.Stop() before Dispose() issues a
                                        // driver-level Stop command that can pause the device's
                                        // FF output, silencing the next effect that starts
                                        // immediately after. DirectInput stops a playing effect
                                        // automatically when it is disposed.
                                        _hapticManager.DestroyEffect(idToStop);
                                        if (_verbose) Log("Laser Damage: STOP beam");
                                    }
                                });
                            }, null, remainingMs, Timeout.Infinite);
                        }
                        else
                        {
                            // Beam already running (or PENDING — FFB thread hasn't assigned ID yet).
                            // Ticks are heartbeats only — we do not update magnitude in-place because
                            // SetParameters restarts the effect's attack envelope on most drivers,
                            // causing rapid successive ticks to continuously reset the motor to zero
                            // force, making the beam unfelt entirely. The magnitude variation between
                            // ticks (~18%) is not perceptible enough to justify this cost.
                            if (_verbose) Log($"Laser Damage: beam active, Mag={magnitude}");
                        }
                        return;
                    }
                }

            // ── Projectile (Ballistic / PPC damage) ──────────────────────────
            case 1:
                {
                    // EVENT_DAMAGED type=1 covers all projectiles: AC, LBX, and PPC alike.
                    // There is no PPC flag in the damage event — the game does not distinguish
                    // them on the receiving end. A damage-amount heuristic (9-11 dmg = PPC) was
                    // previously used but incorrectly boosted AC/10 hits too. Treat all
                    // projectile damage uniformly; PPCs are differentiated on the firing side.
                    int dirBucket = BucketDirection(direction);
                    var ballisticDmg = _configuration.Advanced.ProjectileDamage;
                    var key = new BatchKey(BatchKind.DamageImpact, SubType: 1, dirBucket);
                    AccumulateBatch(key, magnitude, ballisticDmg.Duration, ballisticDmg.AttackTime, ballisticDmg.FadeTime, dirBucket, rawDamage: e.DamageAmount, damageTypeIntensity: _configuration.Simple.ProjectileDamageIntensity * _configuration.MasterIntensity);
                    return;
                }

            // ── Missile damage ───────────────────────────────────────────────
            case 2:
                {
                    int dirBucket = BucketDirection(direction);
                    var missileDmg = _configuration.Advanced.MissileDamage;
                    var key = new BatchKey(BatchKind.DamageImpact, SubType: 2, dirBucket);
                    AccumulateBatch(key, magnitude, missileDmg.Duration, missileDmg.AttackTime, missileDmg.FadeTime, dirBucket, rawDamage: e.DamageAmount, damageTypeIntensity: _configuration.Simple.MissileDamageIntensity * _configuration.MasterIntensity);
                    return;
                }

            // ── Melee damage ─────────────────────────────────────────────────
            case 3:
                {
                    int dirBucket = BucketDirection(direction);
                    var meleeDmg = _configuration.Advanced.MeleeDamage;
                    var key = new BatchKey(BatchKind.DamageImpact, SubType: 3, dirBucket);
                    AccumulateBatch(key, magnitude, meleeDmg.Duration, meleeDmg.AttackTime, meleeDmg.FadeTime, dirBucket, rawDamage: e.DamageAmount, damageTypeIntensity: _configuration.Simple.MeleeDamageIntensity * _configuration.MasterIntensity);
                    return;
                }

            // ── Unknown damage ───────────────────────────────────────────────
            default:
                {
                    int dirBucket = BucketDirection(direction);
                    var key = new BatchKey(BatchKind.DamageImpact, SubType: 99, dirBucket);
                    AccumulateBatch(key, magnitude, settings.Duration, settings.AttackTime, settings.FadeTime, dirBucket, rawDamage: e.DamageAmount, damageTypeIntensity: 0.6f * _configuration.MasterIntensity);
                    return;
                }
        }
    }

    private void HandleMovement(object? sender, MovementEvent e)
    {
        if (e.Type != MovementType.Footstep)
            return;

        _isLeftFoot = !_isLeftFoot;
        int direction = _isLeftFoot ? DIR_LEFT : DIR_RIGHT;

        if (_verbose) Log($"FOOTSTEP: {(_isLeftFoot ? "LEFT" : "RIGHT")}, Tonnage={e.MechTonnage}, Speed={e.Velocity:F1}");

        int magnitude = Math.Clamp((int)(32767 * _configuration.Simple.FootstepIntensity * _configuration.MasterIntensity), 0, 32767);
        var footstep = _configuration.Advanced.Footsteps;

        // Direction included in key so L/R footsteps don't merge with each other
        var key = new BatchKey(BatchKind.WeaponImpact, SubType: 999, direction);
        AccumulateBatch(key, magnitude, footstep.Duration, footstep.AttackTime, footstep.FadeTime, direction);
    }

    private void HandleJumpJet(object? sender, JumpJetEvent e)
    {
        if (_verbose) Log($"JUMP JET: State={e.State}");

        // Jump jets are continuous singletons — never batched
        if (e.State == JumpJetState.Active)
        {
            // Claim the slot on the read thread (-1 → PENDING) - see EFFECT_PENDING
            if (Interlocked.CompareExchange(ref _activeJumpJetEffectId, EFFECT_PENDING, -1) == -1)
            {
                int magnitude = Math.Clamp((int)(32767 * _configuration.Simple.JumpJetIntensity * _configuration.MasterIntensity), 0, 32767);
                var jumpJet = _configuration.Advanced.JumpJets;
                int captureMag = magnitude;
                EnqueueEffect(() =>
                {
                    if (_activeJumpJetEffectId != EFFECT_PENDING)
                        return; // Jets cut before we got here - nothing to start

                    int id = _hapticManager.CreatePeriodicEffect(captureMag, 10000, jumpJet.Frequency, jumpJet.AttackTime, jumpJet.FadeTime);
                    if (id < 0)
                    {
                        Interlocked.CompareExchange(ref _activeJumpJetEffectId, -1, EFFECT_PENDING); // allow retry
                        return;
                    }
                    if (Interlocked.CompareExchange(ref _activeJumpJetEffectId, id, EFFECT_PENDING) != EFFECT_PENDING)
                    {
                        _hapticManager.DestroyEffect(id); // Jets cut while we were creating
                        return;
                    }
                    _hapticManager.PlayEffect(id, 1);
                });
            }
        }
        else
        {
            int captureId = Interlocked.Exchange(ref _activeJumpJetEffectId, -1);
            if (captureId >= 0 && captureId != EFFECT_PENDING) // PENDING: the queued create sees -1 and skips
            {
                EnqueueEffect(() =>
                {
                    _hapticManager.StopEffect(captureId);
                    _hapticManager.DestroyEffect(captureId);
                });
            }
        }
    }

    private void HandleImpact(object? sender, ImpactEvent e)
    {
        if (_verbose) Log($"LANDING: Velocity={e.ImpactVelocity:F1}, Tonnage={e.MechTonnage}");

        int magnitude = Math.Clamp((int)(32767 * _configuration.Simple.LandingIntensity * _configuration.MasterIntensity), 0, 32767);
        var landing = _configuration.Advanced.Landing;

        // Landing pushes the stick away from the pilot - the opposite of weapon recoil
        const int landingDirection = DIR_AWAY_FROM_PILOT;

        // Landing rarely fires multiple times in 10ms but AccumulateBatch handles it safely
        var key = new BatchKey(BatchKind.WeaponImpact, SubType: 998, Direction: landingDirection);
        AccumulateBatch(key, magnitude, landing.Duration, landing.AttackTime, landing.FadeTime, landingDirection);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MG pulse timer tick (every 93ms while firing). Includes a safety stop: if no "firing"
    /// event has arrived for MACHINE_GUN_STALE_MS (e.g. the mech was destroyed mid-burst, or the
    /// game paused / lost focus before sending STOP), the buzz ends instead of running forever.
    /// </summary>
    private void MachineGunTick()
    {
        lock (_machineGunLock)
        {
            if (!_machineGunsActive)
                return;

            if ((DateTime.Now - _lastMachineGunActiveTime).TotalMilliseconds > MACHINE_GUN_STALE_MS)
            {
                StopMachineGunsLocked($"safety stop - no firing event for {MACHINE_GUN_STALE_MS / 1000}s");
                return;
            }

            FireMachineGunPulse(_currentMachineGunMagnitude, _configuration.Advanced.MachineGuns);
        }
    }

    /// <summary>Stop the MG pulse train. Caller must hold _machineGunLock.</summary>
    private void StopMachineGunsLocked(string reason)
    {
        _machineGunsActive = false;
        _machineGunTimer?.Dispose();
        _machineGunTimer = null;
        _currentMachineGunMagnitude = 0;
        if (_verbose || reason != "STOP") Log($"Machine Gun: {reason}"); // safety stops always logged
    }

    /// <summary>
    /// Machine gun pulses still fire individually — the timer owns the cadence.
    /// </summary>
    private void FireMachineGunPulse(int magnitude, MachineGunSettings mg)
    {
        EnqueueEffect(() =>
        {
            int effectId = _hapticManager.CreatePeriodicEffect(magnitude, mg.Duration, mg.Frequency, mg.AttackTime, mg.FadeTime);
            if (effectId >= 0)
            {
                _hapticManager.PlayEffect(effectId, 1);
                Task.Delay(mg.Duration + 20).ContinueWith(_ => EnqueueEffect(() => _hapticManager.DestroyEffect(effectId)));
            }
        });
    }

    /// <summary>
    /// Bucket a centidegree direction to the nearest 45° sector (8 buckets total).
    /// Damage events from the same attack hitting slightly different angles will merge.
    /// </summary>
    private static int BucketDirection(int centidegrees)
    {
        int sector = (int)Math.Round(centidegrees / 4500.0) % 8;
        return sector * 4500;
    }

    private int CalculateRecoilMagnitude(WeaponFireEvent e)
    {
        float baseMag = 0;
        float weaponIntensity = 1.0f;

        switch (e.WeaponClass)
        {
            case WeaponClass.Ballistic:
                {
                    // Power curve tuned to F5 impulse values from game telemetry:
                    // AC/2 (700) → ~10000, AC/5 (1250) → ~13826, AC/10 (2500) → ~20366, AC/20 (5000) → ~30000
                    //
                    // The game's raw per-shot impulse is an unreliable proxy and the burst remap
                    // below overflows for outliers, so first substitute the weapon's canonical
                    // impulse from the profile table. Unknown/modded weapons return their raw
                    // impulse unchanged (and the clamps below keep them safe).
                    float sourceImpulse = _weaponProfiles.ResolveImpulse(e);
                    float effectiveDamage = sourceImpulse;

                    if (e.NumberOfShots > 1 && e.DelayBetweenShots > 0)
                    {
                        // Burst weapons compress per-shot impulse into a narrow band (350–500) regardless
                        // of weapon size. We remap onto 85% of each weapon's non-burst magnitude:
                        //   350 → 523  → mag ~8500  (85% of AC/2's ~10000)
                        //   400 → 1008 → mag ~12259 (89% of AC/5's ~13826)
                        //   450 → 1941 → mag ~17681 (87% of AC/10's ~20366)
                        //   500 → 3738 → mag ~25500 (85% of AC/20's ~30000)
                        // Exponential interpolation on t = (D - 350) / 150 (0=AC/2, 1=AC/20).
                        // The input is clamped to the 350–500 band so an unaccounted weapon outside
                        // it maps to the nearest tier instead of exploding the exponential (which
                        // would overflow the int cast below and wrap to 0). Known weapons are already
                        // substituted to an in-band canonical value, so this never affects them.
                        double t = (Math.Clamp(sourceImpulse, 350f, 500f) - 350.0) / 150.0;
                        effectiveDamage = (float)(523.34 * Math.Pow(7.1429, t));
                    }

                    baseMag = 257.18f * (float)Math.Pow(effectiveDamage, 0.5588);
                    weaponIntensity = _configuration.Simple.BallisticIntensity;
                    break;
                }

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
                    // Power curve tuned to F0 (DamageOverDuration) values from game telemetry:
                    // Small Laser (6.12) → ~2000, Medium Pulse (8.57) → ~2944, Large Laser (15.93) → ~6000
                    baseMag = 249.76f * (float)Math.Pow(e.Damage, 1.1484);
                    weaponIntensity = _configuration.Simple.LaserIntensity;
                }
                break;

            case WeaponClass.Missile:
                baseMag = (e.Damage * 15) + 10000;
                // Advanced "Rumble Intensity (%)" scales on top of the Simple missile slider
                weaponIntensity = _configuration.Simple.MissileIntensity * _configuration.Advanced.Missiles.RumbleMultiplier;
                break;

            case WeaponClass.Melee:
                baseMag = (e.Damage * 12) + 20000;
                weaponIntensity = _configuration.Simple.MeleeIntensity;
                break;
        }

        baseMag *= weaponIntensity * _configuration.MasterIntensity;
        // Clamp in float space BEFORE the int cast. A float larger than int.MaxValue
        // would overflow the cast and wrap to int.MinValue (→ 0 after Clamp) on this
        // runtime — the cause of the burst "silent weapon" bug. Clamping the float to
        // [0, 32767] first makes the cast always in-range, so no formula can ever
        // overflow regardless of input (incl. unaccounted modded weapons). NaN → 0.
        int magnitude = float.IsNaN(baseMag) ? 0 : (int)Math.Clamp(baseMag, 0f, 32767f);

        string weaponType = e.IsPPC ? "PPC" : e.WeaponClass.ToString();
        if (_verbose) Log($"Weapon: {weaponType}, RawDamage={e.Damage:F1}, Intensity={weaponIntensity:F2}, FinalMag={magnitude}");

        return magnitude;
    }

    public void Dispose()
    {
        Stop();
        _hapticManager?.Dispose();
        _telemetryReader?.Dispose();
    }
}