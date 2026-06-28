using System.Text.Json;
using System.Text.Json.Serialization;
using MechFFBReader.Events;

namespace MechFFBEngine.Configuration;

/// <summary>
/// Per-weapon recoil profile table.
///
/// The game's per-shot <c>Impulse</c> (F5) is an unreliable proxy for "how big a
/// weapon should feel" — outliers either under-scale or, on the burst path, blow
/// up the remap exponential. This table maps each weapon to a canonical impulse
/// that lands cleanly on its class tier, fed into the existing recoil formula
/// unchanged.
///
/// Key = (shots=F0, pellets=F2, impulse=F5). These are quirk-stable. Projectile
/// SPEED (F3) is deliberately NOT in the key: live telemetry scales it by the
/// pilot's velocity skills/quirks (e.g. ×1.125), so an exact speed match fails.
/// Speed is kept only as a nearest-match tie-break for the handful of keys shared
/// by weapons that need different values (MediumRifle vs Gauss; the Clan LBX
/// solid-slugs) — their base speeds are far enough apart that nearest-match is
/// robust even with a velocity quirk applied.
///
/// Resolving a weapon on the hot read thread is an allocation-free dictionary
/// probe (plus, only for the rare shared keys, a loop over ≤3 candidates).
/// Unknown/modded weapons fall back to their raw impulse — and the recoil formula
/// self-clamps, so a fallthrough can never overflow.
/// </summary>
public sealed class WeaponProfiles
{
    /// <summary>Allocation-free lookup key over the quirk-stable telemetry fields.</summary>
    public readonly record struct WeaponSignature(int Shots, int Pellets, int Impulse);

    /// <summary>One weapon mapped to a key; BaseSpeed disambiguates shared keys.</summary>
    private readonly record struct Candidate(int BaseSpeed, int Canonical);

    private readonly Dictionary<WeaponSignature, Candidate[]> _map = new();

    public int Count => _map.Count;

    /// <summary>
    /// Resolve the impulse to feed into the recoil formula for this event.
    /// Returns the weapon's canonical impulse if a profile exists, otherwise the
    /// raw game impulse (<c>e.Damage</c>). No allocation.
    /// </summary>
    public float ResolveImpulse(WeaponFireEvent e)
    {
        var sig = new WeaponSignature(
            e.NumberOfShots,
            (int)MathF.Round(e.ProjectileMass),   // F2 = pellets per shell (held in ProjectileMass for ballistics)
            (int)MathF.Round(e.Damage));          // F5 = raw impulse

        if (!_map.TryGetValue(sig, out var candidates))
            return e.Damage;                      // unknown weapon → formula fallback

        if (candidates.Length == 1)
            return candidates[0].Canonical;

        // Shared key (e.g. MediumRifle vs Gauss; the Clan LBX solid-slugs). Velocity
        // skills/quirks only ADD speed, so live speed ≥ a weapon's base speed. The
        // right match is therefore the candidate with the LARGEST base speed that
        // does not exceed the live speed. (Nearest-match would tie on a midpoint;
        // this resolves it correctly.) Loop is over ≤3 entries — negligible.
        int liveSpeed = (int)MathF.Round(e.MuzzleVelocity);
        Candidate best = default;
        bool have = false;
        for (int i = 0; i < candidates.Length; i++)
        {
            var c = candidates[i];
            if (c.BaseSpeed <= liveSpeed && (!have || c.BaseSpeed > best.BaseSpeed))
            {
                best = c; have = true;
            }
        }
        if (have)
            return best.Canonical;

        // No base ≤ live (only possible with a speed-reducing modifier) → nearest.
        best = candidates[0];
        int bestDiff = Math.Abs(best.BaseSpeed - liveSpeed);
        for (int i = 1; i < candidates.Length; i++)
        {
            int diff = Math.Abs(candidates[i].BaseSpeed - liveSpeed);
            if (diff < bestDiff) { bestDiff = diff; best = candidates[i]; }
        }
        return best.Canonical;
    }

    /// <summary>
    /// Load the profile table. Layers a base file and an optional mod-override
    /// file, in order: weapon_profiles.json (vanilla) then weapon_profiles.mods.json
    /// (modded weapons). Entries from all files merge into the same signature map —
    /// a modded weapon with a new signature simply adds a key; one that shares a
    /// vanilla signature joins the candidates and is resolved by the speed tie-break.
    ///
    /// Each file is looked up first under %AppData%\MechFFB (user override), then
    /// next to the app (shipped copy). Missing/invalid files are non-fatal: any
    /// weapon with no profile falls back to the formula (which still self-clamps).
    /// </summary>
    public static WeaponProfiles Load()
    {
        var profiles = new WeaponProfiles();
        var grouped = new Dictionary<WeaponSignature, List<Candidate>>();

        foreach (string fileName in new[] { "weapon_profiles.json", "weapon_profiles.mods.json" })
        {
            foreach (var w in ReadEntries(fileName))
            {
                var sig = new WeaponSignature(w.Shots, w.Pellets, w.Impulse);
                if (!grouped.TryGetValue(sig, out var list))
                    grouped[sig] = list = new List<Candidate>();
                list.Add(new Candidate(w.BaseSpeed, w.CanonicalImpulse));
            }
        }

        foreach (var kv in grouped)
            profiles._map[kv.Key] = kv.Value.ToArray();

        Console.WriteLine($"WeaponProfiles: loaded {profiles._map.Count} signature keys");
        return profiles;
    }

    /// <summary>Read one profile file (AppData override preferred over shipped copy).</summary>
    private static List<ProfileEntry> ReadEntries(string fileName)
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MechFFB", fileName);
        string shipped = Path.Combine(AppContext.BaseDirectory, fileName);
        string path = File.Exists(appData) ? appData : shipped;

        try
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"WeaponProfiles: {fileName} not present — skipping");
                return new List<ProfileEntry>();
            }
            var doc = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(path));
            var list = doc?.Weapons ?? new List<ProfileEntry>();
            Console.WriteLine($"WeaponProfiles: read {list.Count} entries from {path}");
            return list;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WeaponProfiles: failed to read {fileName} ({ex.Message}) — skipping");
            return new List<ProfileEntry>();
        }
    }

    private sealed class ProfileDocument
    {
        [JsonPropertyName("weapons")]
        public List<ProfileEntry>? Weapons { get; set; }
    }

    private sealed class ProfileEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("shots")] public int Shots { get; set; }
        [JsonPropertyName("pellets")] public int Pellets { get; set; }
        [JsonPropertyName("impulse")] public int Impulse { get; set; }
        [JsonPropertyName("baseSpeed")] public int BaseSpeed { get; set; }
        [JsonPropertyName("canonicalImpulse")] public int CanonicalImpulse { get; set; }
    }
}
