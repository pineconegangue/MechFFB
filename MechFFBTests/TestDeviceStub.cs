using MechFFBEngine.Configuration;

namespace MechFFBEngine.Haptic;

/// <summary>
/// Stand-in for the SharpDX-backed DirectInputHapticManager, with the same public surface
/// FFBEngine uses. Records effect lifetimes and detects overlapping (concurrent) device calls.
/// </summary>
public class DirectInputHapticManager : IDisposable
{
    public class HapticDeviceInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public Guid InstanceGuid { get; set; }
    }

    public IntPtr WindowHandle { get; set; }
    public AxisMapping AxisMapping { get; set; } = new();
    public int ForceAxisCount => 2;
    public bool EnableExclusiveMode { get; set; }
    public bool HasDeviceSelected => true;

    // ── Instrumentation ──
    public int Creates;
    public int Overlaps;
    private int _inFlight;
    private int _nextId;
    private readonly HashSet<int> _live = new();
    private readonly List<int> _impactDirections = new();
    public int[] ImpactDirections { get { lock (_impactDirections) return _impactDirections.ToArray(); } }
    public void ClearImpactDirections() { lock (_impactDirections) _impactDirections.Clear(); }
    public int LiveCount { get { lock (_live) return _live.Count; } }

    private void Enter()
    {
        if (Interlocked.Increment(ref _inFlight) > 1) Interlocked.Increment(ref Overlaps);
        BusyWait(0.2); // widen the window so any overlap is actually caught
    }

    // Busy-wait instead of Thread.Sleep: on Windows, Sleep(1) lasts ~15 ms unless something
    // raises the timer resolution, which would make the stub ~15x slower than intended.
    private static void BusyWait(double ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < ms) Thread.SpinWait(50);
    }
    private void Exit() => Interlocked.Decrement(ref _inFlight);

    private int Make(int extraDelayMs = 0)
    {
        Enter();
        try
        {
            if (extraDelayMs > 0) BusyWait(extraDelayMs); // simulate slow COM create
            Interlocked.Increment(ref Creates);
            int id = Interlocked.Increment(ref _nextId);
            lock (_live) _live.Add(id);
            return id;
        }
        finally { Exit(); }
    }

    public bool Initialize() => true;
    public List<HapticDeviceInfo> GetHapticDevices() => new();
    public bool SelectDevice(int index) => true;
    public bool TestDevice() => true;

    public int CreateImpactEffect(int magnitude, int duration, int attackMs = 20, int fadeMs = 150, int direction = 0, bool applyMapping = true)
    {
        lock (_impactDirections) _impactDirections.Add(direction); // canonical direction from the engine
        return Make();
    }
    public bool PlayRawPush(int canonicalDirection) => true;
    public int CreatePeriodicEffect(int magnitude, int duration, int frequency = 30, int attackMs = 50, int fadeMs = 100) => Make(duration >= 10000 ? 5 : 0);
    public int[] CreateExplosionEffect(int baseMagnitude, int impactDuration, int rumbleDuration, int impactAttackMs, int impactFadeMs,
        int rumbleAttackMs, int rumbleFadeMs, int direction, int rumbleFreq, float impactMultiplier, float rumbleMultiplier)
        => new[] { Make(), Make() };

    public bool PlayEffect(int effectId, int iterations = 1) { Enter(); Exit(); return true; }
    public void StopEffect(int effectId) { Enter(); Exit(); }
    public void DestroyEffect(int effectId) { Enter(); try { lock (_live) _live.Remove(effectId); } finally { Exit(); } }
    public void DestroyAllEffects() { lock (_live) _live.Clear(); }
    public void Dispose() { }
}
