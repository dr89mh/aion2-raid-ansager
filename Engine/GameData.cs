using System.Reflection;
using System.Text.Json;

namespace RaidAnsager.Engine;

/// <summary>NPC and NPC-skill names in English and German (from the aion2-dps-meter game data, see tools/make_data.py).
/// Names follow <see cref="L.Lang"/>.</summary>
public sealed class GameData
{
    private readonly Dictionary<string, Dictionary<uint, string>> _npcNames = new();
    private readonly Dictionary<string, Dictionary<uint, string>> _skillNames = new();
    private readonly Dictionary<uint, (bool Boss, uint Dungeon, bool Dummy)> _npcFlags = new();

    public static GameData Load()
    {
        var data = new GameData();
        foreach (var lang in L.Languages)
        {
            var npcs = data._npcNames[lang] = new Dictionary<uint, string>();
            using (var s = Open($"data/npcs-{lang}.json"))
            using (var doc = JsonDocument.Parse(s))
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (!uint.TryParse(p.Name, out var id)) continue;
                    npcs[id] = p.Value.GetProperty("n").GetString() ?? "";
                    if (lang == "en")
                        data._npcFlags[id] = (p.Value.TryGetProperty("b", out _),
                            p.Value.TryGetProperty("d", out var d) && d.TryGetUInt32(out var dungeon) ? dungeon : 0,
                            p.Value.TryGetProperty("x", out _));
                }
            var skills = data._skillNames[lang] = new Dictionary<uint, string>();
            using (var s = Open($"data/skills-{lang}.json"))
            using (var doc = JsonDocument.Parse(s))
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (uint.TryParse(p.Name, out var id) && p.Value.GetString() is { Length: > 0 } n)
                        skills[id] = n;
        }
        return data;
    }

    private static Stream Open(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " fehlt");

    public string? NpcName(uint code) => NpcName(code, L.Lang);

    public string? NpcName(uint code, string lang) => _npcNames[lang].TryGetValue(code, out var n) ? n : null;

    public bool IsBoss(uint code) => _npcFlags.TryGetValue(code, out var n) && n.Boss && !n.Dummy;

    /// <summary>Boss of a dungeon/raid (the game data names its instance); field bosses have none.</summary>
    public bool IsInstanceBoss(uint code) => _npcFlags.TryGetValue(code, out var n) && n.Boss && !n.Dummy && n.Dungeon != 0;

    /// <summary>Name of a skill code; variants (last digit ≠ 0) fall back to their base code.</summary>
    public string? SkillName(uint code) => SkillName(code, L.Lang);

    public string? SkillName(uint code, string lang)
    {
        var table = _skillNames[lang];
        if (table.TryGetValue(code, out var n)) return n;
        if (code % 10 != 0 && table.TryGetValue(code - code % 10, out n)) return n;
        return null;
    }

    /// <summary>Plain melee swings that are not worth announcing (checked in both languages).</summary>
    public bool IsAutoAttack(uint code) =>
        SkillName(code, "en") is "Attack" or "Basic Attack" || SkillName(code, "de") is "Angriff" or "Grundangriff";

    /// <summary>Unnamed skills every boss casts now and then (seen on all bosses of 2026-11-05), not a mechanic.</summary>
    public static bool IsGeneric(uint skill) => skill is 1000009 or 1001450;
}
