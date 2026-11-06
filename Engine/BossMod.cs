using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaidAnsager.Engine;

/// <summary>
/// A boss mod: the bosses of one raid/dungeon and the triggers that turn their skills into callouts.
/// Stored as JSON (see mods/ludra.json); a newer file in a mods folder replaces the built-in copy.
/// </summary>
public sealed class BossMod
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Sortable version, e.g. "2026-11-06" or "2026-11-08b". The highest one per id wins.</summary>
    public string Version { get; set; } = "";
    public string? Notes { get; set; }
    /// <summary>Zones (01 40) where the mod is active; empty = everywhere.</summary>
    public List<uint> Zones { get; set; } = new();
    public List<BossDef> Bosses { get; set; } = new();

    [JsonIgnore] public string Source { get; set; } = "";
}

public sealed class BossDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>NPC codes of the boss (41 36 spawn).</summary>
    public List<uint> Npc { get; set; } = new();
    /// <summary>Seconds without any combat on the boss before the fight counts as reset/wipe.</summary>
    public double ResetAfter { get; set; } = 45;
    /// <summary>Spoken when the boss dies. Default "{boss} besiegt"; "" = silent.</summary>
    public string? OnKill { get; set; }
    public List<Trigger> Triggers { get; set; } = new();
}

public sealed class Trigger
{
    public string Name { get; set; } = "";
    /// <summary>pull | cast | hit | hp | buff | spawn | death</summary>
    public string On { get; set; } = "cast";
    /// <summary>Skill codes for cast/hit (02 38 / 04 38).</summary>
    public List<uint> Skill { get; set; } = new();
    /// <summary>NPC codes for spawn/death; for cast/hit: only when one of these casts (default: any NPC).</summary>
    public List<uint> Npc { get; set; } = new();
    /// <summary>Buff ids for buff triggers (2A 38).</summary>
    public List<uint> Buff { get; set; } = new();
    /// <summary>hp trigger: fires once when the boss drops below this percentage.</summary>
    public double? Below { get; set; }
    /// <summary>any | me | notme | player | boss</summary>
    public string Target { get; set; } = "any";
    public int MinStack { get; set; }
    /// <summary>Only active in this phase (phases start at 1 on pull).</summary>
    public int? Phase { get; set; }
    public int? SetPhase { get; set; }

    public string? Say { get; set; }
    public string? Show { get; set; }
    public string? Color { get; set; }
    /// <summary>alarm | info | none</summary>
    public string? Sound { get; set; }
    public double ShowSeconds { get; set; } = 4;
    /// <summary>Seconds to wait before the callout (e.g. "move now" after a wind-up).</summary>
    public double Delay { get; set; }
    /// <summary>Seconds during which the trigger does not fire again (multi-target casts send several packets).</summary>
    public double Cooldown { get; set; } = 3;
    public bool Once { get; set; }
    /// <summary>Only for these roles (tank, heiler, dd); empty = everyone.</summary>
    public List<string> Roles { get; set; } = new();
    /// <summary>Starts (or restarts) a countdown bar.</summary>
    public BarDef? Bar { get; set; }
    /// <summary>Removes the bar with this text.</summary>
    public string? CancelBar { get; set; }
    /// <summary>false = skill code taken from game data/guides, not yet seen in our own recordings.</summary>
    public bool Verified { get; set; } = true;
    public string? Note { get; set; }
}

public sealed class BarDef
{
    public string Text { get; set; } = "";
    public double In { get; set; }
    /// <summary>Seconds before the end at which the bar turns red and is announced.</summary>
    public double Warn { get; set; } = 5;
    /// <summary>Spoken at the warning. Default "{text} in {warn}"; "" = silent.</summary>
    public string? WarnSay { get; set; }
    /// <summary>Spoken when the bar runs out (optional).</summary>
    public string? EndSay { get; set; }
}

public static class ModLoader
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>For writing mods: leaves out empty lists and values equal to the defaults, so files stay short.</summary>
    public static readonly JsonSerializerOptions Compact = new(Json)
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    if (info.Kind != System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object || info.CreateObject is null) return;
                    var defaults = info.CreateObject();
                    foreach (var prop in info.Properties)
                    {
                        if (prop.Get is null) continue;
                        var get = prop.Get;
                        var def = get(defaults);
                        prop.ShouldSerialize = (_, value) => value switch
                        {
                            null => false,
                            System.Collections.ICollection { Count: 0 } => false,
                            _ => !Equals(value, def),
                        };
                    }
                },
            },
        },
    };

    public static BossMod Parse(string json, string source)
    {
        var mod = JsonSerializer.Deserialize<BossMod>(json, Json) ?? throw new FormatException("leere Datei");
        mod.Source = source;
        return mod;
    }

    /// <summary>Built-in mods plus *.json from the given folders; per id the highest version wins (files win ties).</summary>
    public static List<BossMod> LoadAll(IEnumerable<string> folders, List<string> problems)
    {
        var found = new List<BossMod>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("mods/", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            try { found.Add(Parse(r.ReadToEnd(), "eingebaut")); }
            catch (Exception ex) when (ex is JsonException or FormatException) { problems.Add($"{res}: {ex.Message}"); }
        }
        foreach (var dir in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try { found.Add(Parse(File.ReadAllText(file), file)); }
                catch (Exception ex) when (ex is JsonException or FormatException or IOException)
                {
                    problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }

        var result = new List<BossMod>();
        foreach (var group in found.Where(m => m.Id.Length > 0).GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            var best = group.OrderByDescending(m => m.Version, StringComparer.Ordinal)
                .ThenBy(m => m.Source == "eingebaut" ? 1 : 0).First();
            result.Add(best);
        }
        foreach (var mod in result) problems.AddRange(Check(mod));
        return result;
    }

    private static readonly string[] Kinds = ["pull", "cast", "hit", "hp", "buff", "spawn", "death"];
    private static readonly string[] Targets = ["any", "me", "notme", "player", "boss"];

    public static IEnumerable<string> Check(BossMod mod)
    {
        foreach (var boss in mod.Bosses)
        {
            if (boss.Npc.Count == 0) yield return $"{mod.Id}/{boss.Id}: keine NPC-Codes";
            foreach (var t in boss.Triggers)
            {
                var where = $"{mod.Id}/{boss.Id}/{(t.Name.Length > 0 ? t.Name : t.On)}";
                if (!Kinds.Contains(t.On)) yield return $"{where}: unbekanntes \"on\": {t.On}";
                if (!Targets.Contains(t.Target)) yield return $"{where}: unbekanntes \"target\": {t.Target}";
                if (t.On is "cast" or "hit" && t.Skill.Count == 0) yield return $"{where}: \"skill\" fehlt";
                if (t.On == "buff" && t.Buff.Count == 0) yield return $"{where}: \"buff\" fehlt";
                if (t.On is "spawn" or "death" && t.Npc.Count == 0) yield return $"{where}: \"npc\" fehlt";
                if (t.On == "hp" && t.Below is null) yield return $"{where}: \"below\" fehlt";
                if (t.Bar is { In: <= 0 }) yield return $"{where}: Balken ohne Dauer";
            }
        }
    }
}
