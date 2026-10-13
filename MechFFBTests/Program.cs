using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Text.Json;
using MechFFBEngine;
using MechFFBEngine.Configuration;
using MechFFBEngine.Haptic;
using MechFFBReader.Events;
using MechFFBReader.Infrastructure;

// MechFFB self-tests - run with MW5 closed:  dotnet run --project MechFFBTests
// Reaches private engine members by reflection; if you rename those, update the names below.

// Results go to the console AND to test-results.txt next to the exe (handy when the console
// window closes on exit, e.g. when launched by double-click).
var resultsPath = Path.Combine(AppContext.BaseDirectory, "test-results.txt");
var resultsFile = new StreamWriter(resultsPath, append: false) { AutoFlush = true };
var output = new TeeWriter(Console.Out, resultsFile);
Console.SetOut(TextWriter.Null); // silence engine/reader logging
output.WriteLine($"Run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
int passed = 0, failed = 0, skipped = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) passed++; else failed++;
    output.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
}
void Skip(string name, string why) { skipped++; output.WriteLine($"SKIP  {name}  ({why})"); }

output.WriteLine("MechFFB self-tests - run with MW5 closed\n");

// ───────────────────────── Settings file ─────────────────────────
{
    string oldJson = """
    { "MasterIntensity": 0.9, "InvertDirection": true, "DisableExclusiveMode": true,
      "Simple": { "LaserDamageIntensity": 0.25, "BallisticDamageIntensity": 0.75, "PPCDamageIntensity": 0.4 },
      "Advanced": { "LaserDamage": { "Frequency": 77, "AttackTime": 11, "FadeTime": 222, "Intensity": 0.3 },
                    "BallisticDamage": { "Duration": 456 }, "PPCDamage": { "Duration": 111 },
                    "ExplosionDamage": { "Duration": 1 } } }
    """;
    var c = JsonSerializer.Deserialize<FFBConfiguration>(oldJson)!;
    Check("Settings: old laser/ballistic damage values carry over",
        c.Simple.LaserMGDamageIntensity == 0.25f && c.Simple.ProjectileDamageIntensity == 0.75f
        && c.Advanced.LaserMGDamage.Frequency == 77 && c.Advanced.ProjectileDamage.Duration == 456);
    Check("Settings: Invert Axis ON migrates to the reference mapping (no flips); verbose off",
        c.DefaultAxisMapping.IsIdentity && !c.VerboseLogging);
    Check("Settings: old 'Disable Exclusive Mode' = true keeps exclusive mode off", !c.EnableExclusiveMode);
    var fresh = new FFBConfiguration();
    var staleOff = JsonSerializer.Deserialize<FFBConfiguration>("""{ "EnableExclusiveMode": false }""")!;
    var oldDefault = JsonSerializer.Deserialize<FFBConfiguration>("""{ "DisableExclusiveMode": false }""")!;
    Check("Settings: exclusive mode on by default (new, old-default and short-lived 'off' files)",
        fresh.EnableExclusiveMode && staleOff.EnableExclusiveMode && oldDefault.EnableExclusiveMode);
    var cOff = JsonSerializer.Deserialize<FFBConfiguration>("""{ "InvertDirection": false }""")!;
    Check("Settings: Invert Axis OFF migrates to Flip X + Flip Y",
        cOff.DefaultAxisMapping.FlipX && cOff.DefaultAxisMapping.FlipY && !cOff.DefaultAxisMapping.SwapXY);
    var perDevice = new FFBConfiguration();
    var guid = Guid.NewGuid();
    perDevice.DeviceAxisMappings[guid] = new AxisMapping { FlipX = true };
    var roundTrip = JsonSerializer.Deserialize<FFBConfiguration>(JsonSerializer.Serialize(perDevice))!;
    Check("Settings: per-device axis mapping saves and loads",
        roundTrip.GetAxisMapping(guid).FlipX && roundTrip.GetAxisMapping(Guid.NewGuid()).IsIdentity);

    string saved = JsonSerializer.Serialize(c);
    string[] retired = { "\"LaserDamageIntensity\"", "\"BallisticDamageIntensity\"", "\"PPCDamage", "\"LaserDamage\"",
                         "\"BallisticDamage\"", "\"Legacy", "\"DisableExclusiveMode\"", "\"EnableExclusiveMode\"", "\"InvertDirection\"", "\"IsIdentity\"", "\"Intensity\"", "\"ExplosionDamage\"" };
    var leaked = retired.Where(saved.Contains).ToArray();
    Check("Settings: saved file contains only current keys", leaked.Length == 0, string.Join(", ", leaked));
}

// ───────────────────────── Axis mapping ─────────────────────────
{
    // Legacy equivalence: old "Invert OFF" rotated every direction 180°; Flip X + Flip Y must match exactly
    var flipBoth = new AxisMapping { FlipX = true, FlipY = true };
    bool equivalent = true;
    for (int angle = 0; angle < 36000; angle += 1500)
    {
        double r = angle * Math.PI / 18000.0, r180 = (angle + 18000) * Math.PI / 18000.0;
        var (x, y) = flipBoth.Apply(Math.Cos(r), Math.Sin(r));
        equivalent &= Math.Abs(x - Math.Cos(r180)) < 1e-9 && Math.Abs(y - Math.Sin(r180)) < 1e-9;
    }
    Check("Axis mapping: Flip X + Flip Y is identical to the old Invert OFF", equivalent);

    // Calibration solver: simulate all 8 possible device orientations. For each, play the two raw
    // pushes, "feel" where they go, solve, and check every canonical direction then lands correctly.
    static PushDirection Felt((int X, int Y) v) =>
        Enum.GetValues<PushDirection>().First(d => AxisMapping.CanonicalVector(d) == v);
    int solved = 0;
    foreach (bool swap in new[] { false, true })
    foreach (int sx in new[] { 1, -1 })
    foreach (int sy in new[] { 1, -1 })
    {
        // Device D: raw (x,y) -> felt direction in the canonical frame
        (int, int) D((int X, int Y) raw) => swap ? (sx * raw.Y, sy * raw.X) : (sx * raw.X, sy * raw.Y);
        var mapping = AxisMapping.FromCalibration(Felt(D((0, -1))), Felt(D((-1, 0))));
        bool ok = mapping != null && Enum.GetValues<PushDirection>().All(dir =>
        {
            var c = AxisMapping.CanonicalVector(dir);
            var (mx, my) = mapping.Apply(c.X, c.Y);
            return D(((int)mx, (int)my)) == c;
        });
        if (ok) solved++;
    }
    Check("Calibration: solves all 8 device orientations", solved == 8, $"{solved}/8");
    Check("Calibration: answers matching the reference stick give no flips",
        AxisMapping.FromCalibration(PushDirection.AwayFromYou, PushDirection.Right)!.IsIdentity);
    Check("Calibration: impossible answers (both pushes on one line) are rejected",
        AxisMapping.FromCalibration(PushDirection.AwayFromYou, PushDirection.TowardYou) == null);
}

// ───────────────────────── Paths ─────────────────────────
{
    string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MechFFB", "settings.json");
    string shown = DisplayPath.Of(settingsPath);
    Check("Paths: log shows %AppData% instead of the user's folder",
        shown.StartsWith("%AppData%") && !shown.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase), shown);
    Check("Paths: this build doesn't embed the local checkout folder",
        !File.ReadAllText(typeof(FFBEngine).Assembly.Location, System.Text.Encoding.Latin1)
            .Contains(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
}

// ───────────────────────── Weapon profiles ─────────────────────────
{
    var profiles = WeaponProfiles.Load();
    if (profiles.Count == 0)
    {
        Skip("Weapon profiles", "weapon_profiles.json not found next to the test exe");
    }
    else
    {
        float Resolve(int shots, int pellets, float impulse, float speed) => profiles.ResolveImpulse(new WeaponFireEvent
            { WeaponClass = WeaponClass.Ballistic, NumberOfShots = shots, ProjectileMass = pellets, Damage = impulse, MuzzleVelocity = speed });
        Check("Weapon profiles: shared key resolved by speed (Medium Rifle vs Gauss)",
            Resolve(0, 1, 3000, 90000 * 1.125f) == 2500 && Resolve(0, 1, 3000, 200000) == 4150);
        Check("Weapon profiles: unknown weapon falls back to raw impulse", Resolve(9, 9, 1234, 1) == 1234);
    }
}

// ───────────────────────── MMF reader (Windows only) ─────────────────────────
if (!OperatingSystem.IsWindows())
{
    Skip("MMF reader", "named shared memory needs Windows");
}
else if (MmfExists())
{
    Skip("MMF reader", "MechShakerBridgeMemory already exists - close MW5 and run again");
}
else
{
    const int CB = 16, SZ = 32, N = 128;
    using var mmf = MemoryMappedFile.CreateNew("MechShakerBridgeMemory", CB + N * SZ);
    using var w = mmf.CreateViewAccessor();
    long write = 0, packet = 0;
    void SetCounters(long wi, long pn) { write = wi; packet = pn; w.Write(0, wi); w.Write(8, pn); }
    void Publish(int code)
    {
        w.Write(CB + SZ * write, code);
        SetCounters((write + 1) % N, packet + 1);
    }

    using var h = new MemoryMappedFileHandler();
    bool connected = h.Connect();
    h.ReadNewEvents(); // first read only initialises the position
    Publish(4); Publish(5);
    var ev = h.ReadNewEvents();
    Check("MMF: reads new events", connected && ev.Count == 2 && ev[0].EventCode == 4 && ev[1].EventCode == 5);

    SetCounters(0, 0); // game restarted: bridge counter starts again
    var afterReset = h.ReadNewEvents();
    Publish(9);
    var next = h.ReadNewEvents();
    Check("MMF: re-syncs when MW5 restarts (counter reset)", afterReset.Count == 0 && next.Count == 1 && next[0].EventCode == 9);

    SetCounters((write + 200) % N, packet + 200); // 200 events in one poll > 128-slot ring
    var overrun = h.ReadNewEvents();
    Publish(7);
    var afterOverrun = h.ReadNewEvents();
    Check("MMF: drops the backlog on overrun", overrun.Count == 0 && afterOverrun.Count == 1 && afterOverrun[0].EventCode == 7);

    Publish(-1); // closing signal
    h.ReadNewEvents();
    Check("MMF: closing signal disconnects", !h.IsConnected);

    bool reconnected = h.Connect();
    h.ReadNewEvents();
    Publish(4);
    Check("MMF: reconnect starts from the current position", reconnected && h.ReadNewEvents().Count == 1);
}

static bool MmfExists()
{
    try { using var m = MemoryMappedFile.OpenExisting("MechShakerBridgeMemory"); return true; }
    catch (FileNotFoundException) { return false; }
}

// ───────────────────────── Engine ─────────────────────────
var engine = new FFBEngine();
var flags = BindingFlags.NonPublic | BindingFlags.Instance;
var device = (DirectInputHapticManager)typeof(FFBEngine).GetField("_hapticManager", flags)!.GetValue(engine)!;
object? Field(string name) => typeof(FFBEngine).GetField(name, flags)!.GetValue(engine);
void Call(string method, object e) => typeof(FFBEngine).GetMethod(method, flags)!.Invoke(engine, new object?[] { null, e });
void Fire(WeaponFireEvent e) => Call("HandleWeaponFire", e);
bool WaitFor(Func<bool> condition, int timeoutMs)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs) { if (condition()) return true; Thread.Sleep(20); }
    return condition();
}

// Known intensities for the magnitude test (in memory only - never saved)
engine.Configuration.MasterIntensity = 1f;
engine.Configuration.Simple.MissileIntensity = 0.6f;

{
    var calc = typeof(FFBEngine).GetMethod("CalculateRecoilMagnitude", flags)!;
    var missile = new WeaponFireEvent { WeaponClass = WeaponClass.Missile, Damage = 100 };
    engine.Configuration.Advanced.Missiles.RumbleMultiplier = 1f;
    int full = (int)calc.Invoke(engine, new object[] { missile })!;
    engine.Configuration.Advanced.Missiles.RumbleMultiplier = 0.5f;
    int half = (int)calc.Invoke(engine, new object[] { missile })!;
    engine.Configuration.Advanced.Missiles.RumbleMultiplier = 1f;
    Check("Missile Rumble Intensity slider scales launch rumble", full > 0 && Math.Abs(half - full / 2) <= 1, $"100%={full}, 50%={half}");
}

engine.Start(); Thread.Sleep(50); engine.Stop();
engine.Start();
{
    int before = device.Creates;
    Fire(new WeaponFireEvent { WeaponClass = WeaponClass.Ballistic, Damage = 700, ProjectileMass = 1 });
    Check("Stop then Start: effects still play", WaitFor(() => device.Creates == before + 1, 1000));
}

{
    // The engine must always emit the reference (canonical) directions - device differences
    // belong to AxisMapping only. Reference = old "Invert Axis ON", verified on the author's stick.
    int Direction(Action act)
    {
        device.ClearImpactDirections();
        act();
        WaitFor(() => device.ImpactDirections.Length > 0, 1000);
        var d = device.ImpactDirections;
        return d.Length > 0 ? d[0] : -1;
    }
    DamageEvent Hit(float from) => new() { DamageType = DamageType.Projectile, DamageAmount = 5, DirectionAngle = from };
    var results = new (string Name, int Got, int Want)[]
    {
        ("recoil -> toward you", Direction(() => Fire(new WeaponFireEvent { WeaponClass = WeaponClass.Ballistic, Damage = 700, ProjectileMass = 1 })), 9000),
        ("landing -> away", Direction(() => Call("HandleImpact", new ImpactEvent { Type = ImpactType.Landing })), 27000),
        ("hit from front -> toward you", Direction(() => Call("HandleDamage", Hit(180))), 9000),
        ("hit from rear -> away", Direction(() => Call("HandleDamage", Hit(0))), 27000),
        ("hit from right -> left", Direction(() => Call("HandleDamage", Hit(270))), 0),
        ("hit from left -> right", Direction(() => Call("HandleDamage", Hit(90))), 18000),
    };
    var wrong = results.Where(r => r.Got != r.Want).Select(r => $"{r.Name}: got {r.Got}").ToArray();
    Check("Directions: engine emits the reference directions", wrong.Length == 0, string.Join("; ", wrong));
}

{
    var on = new WeaponFireEvent { WeaponClass = WeaponClass.Energy, IsActive = true, BeamDuration = 0, Damage = 8 };
    var off = new WeaponFireEvent { WeaponClass = WeaponClass.Energy, IsActive = false, BeamDuration = 0, Damage = 8 };
    int before = device.Creates;
    Fire(on); Fire(on); Fire(off);
    Thread.Sleep(150);
    Check("Beam laser: fast START/START/STOP makes at most one effect",
        device.Creates - before <= 1 && (int)Field("_activeContinuousBeamEffectId")! == -1, $"created {device.Creates - before}");

    before = device.Creates;
    Fire(on); Thread.Sleep(80); Fire(on); Fire(off);
    Thread.Sleep(150);
    Check("Beam laser: normal START/STOP makes exactly one effect", device.Creates - before == 1);
}

{
    var on = new JumpJetEvent { State = JumpJetState.Active };
    var off = new JumpJetEvent { State = JumpJetState.Inactive };
    int before = device.Creates;
    Call("HandleJumpJet", on); Call("HandleJumpJet", on); Call("HandleJumpJet", off);
    Thread.Sleep(150);
    Check("Jump jets: fast ON/ON/OFF makes at most one effect",
        device.Creates - before <= 1 && (int)Field("_activeJumpJetEffectId")! == -1, $"created {device.Creates - before}");
}

{
    var hit = new DamageEvent { DamageType = DamageType.Trace, BeamDuration = 0, DamageAmount = 1, DirectionAngle = 180 };
    int before = device.Creates;
    Call("HandleDamage", hit); Call("HandleDamage", hit);
    Thread.Sleep(100);
    Check("Machine-gun damage: two quick hits make one effect", device.Creates - before == 1, $"created {device.Creates - before}");
}

{
    // Mixed combat burst: every device call must be strictly one-at-a-time, and everything gets cleaned up
    int overlapsBefore = device.Overlaps;
    for (int i = 0; i < 40; i++)
    {
        Fire(new WeaponFireEvent { WeaponClass = WeaponClass.Ballistic, Damage = 1250, ProjectileMass = 1 });
        Fire(new WeaponFireEvent { WeaponClass = WeaponClass.Ballistic, Damage = 400, ProjectileMass = 1, NumberOfShots = 4, DelayBetweenShots = 0.05f });
        Fire(new WeaponFireEvent { WeaponClass = WeaponClass.Missile, Damage = 100, ProjectileMass = 10, FiringDelay = i % 2 == 0 ? 0.1f : 0f });
        Call("HandleDamage", new DamageEvent { DamageType = DamageType.Projectile, DamageAmount = 5, DirectionAngle = i * 9 });
        Call("HandleDamage", new DamageEvent { DamageType = DamageType.Explosion, DamageAmount = 10, DirectionAngle = 90 });
        Thread.Sleep(5);
    }
    bool cleaned = WaitFor(() => device.LiveCount == 0, 4000);
    Check("Queue: no overlapping device calls under a combat burst", device.Overlaps == overlapsBefore, $"overlaps {device.Overlaps - overlapsBefore}");
    Check("Queue: every effect is destroyed afterwards", cleaned, $"still live {device.LiveCount}");
}

{
    output.WriteLine("      (machine-gun safety stop test takes ~11 s)");
    Fire(new WeaponFireEvent { WeaponClass = WeaponClass.Energy, IsMachineGun = true, IsActive = true, Damage = 1 });
    Thread.Sleep(10600);
    bool stillActive = (bool)Field("_machineGunsActive")!;
    int pulses = device.Creates; Thread.Sleep(300);
    Check("Machine guns: safety stop after 10 s without a firing event", !stillActive && device.Creates == pulses);
}

{
    Fire(new WeaponFireEvent { WeaponClass = WeaponClass.Ballistic, Damage = 5000, ProjectileMass = 1 });
    Call("HandleJumpJet", new JumpJetEvent { State = JumpJetState.Active });
    Thread.Sleep(60);
    engine.Stop();
    Check("Stop: releases every effect and resets state",
        device.LiveCount == 0 && (int)Field("_activeJumpJetEffectId")! == -1 && Field("_machineGunTimer") == null);
}

engine.Dispose();
output.WriteLine($"\n{passed} passed, {failed} failed, {skipped} skipped");
output.WriteLine($"Results saved to {DisplayPath.Of(resultsPath)}");
resultsFile.Dispose();
return failed;

sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _a, _b;
    public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
    public override System.Text.Encoding Encoding => _a.Encoding;
    public override void Write(char value) { _a.Write(value); _b.Write(value); }
    public override void Write(string? value) { _a.Write(value); _b.Write(value); }
    public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
}
