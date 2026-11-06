using System.Reflection;
using System.Text.Json;

namespace RaidAnsager.Engine;

/// <summary>German NPC and NPC-skill names (from the aion2-dps-meter game data, see tools/make_data.py).</summary>
public sealed class GameData
{
    private readonly Dictionary<uint, (string Name, bool Boss, uint Dungeon, bool Dummy)> _npcs = new();
    private readonly Dictionary<uint, string> _skills = new();

    public static GameData Load()
    {
        var data = new GameData();
        using (var s = Open("data/npcs-de.json"))
        using (var doc = JsonDocument.Parse(s))
            foreach (var p in doc.RootElement.EnumerateObject())
                if (uint.TryParse(p.Name, out var id))
                    data._npcs[id] = (p.Value.GetProperty("n").GetString() ?? "", p.Value.TryGetProperty("b", out _),
                        p.Value.TryGetProperty("d", out var d) && d.TryGetUInt32(out var dungeon) ? dungeon : 0,
                        p.Value.TryGetProperty("x", out _));
        using (var s = Open("data/skills-de.json"))
        using (var doc = JsonDocument.Parse(s))
            foreach (var p in doc.RootElement.EnumerateObject())
                if (uint.TryParse(p.Name, out var id) && p.Value.GetString() is { Length: > 0 } n)
                    data._skills[id] = n;
        return data;
    }

    private static Stream Open(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " fehlt");

    public string? NpcName(uint code) => _npcs.TryGetValue(code, out var n) ? n.Name : null;

    public bool IsBoss(uint code) => _npcs.TryGetValue(code, out var n) && n.Boss && !n.Dummy;

    /// <summary>Boss of a dungeon/raid (the game data names its instance); field bosses have none.</summary>
    public bool IsInstanceBoss(uint code) => _npcs.TryGetValue(code, out var n) && n.Boss && !n.Dummy && n.Dungeon != 0;

    /// <summary>Name of a skill code; variants (last digit ≠ 0) fall back to their base code.</summary>
    public string? SkillName(uint code)
    {
        if (_skills.TryGetValue(code, out var n)) return n;
        if (code % 10 != 0 && _skills.TryGetValue(code - code % 10, out n)) return n;
        return null;
    }

    /// <summary>Plain melee swings that are not worth announcing.</summary>
    public static bool IsAutoAttack(string? name) => name is "Angriff" or "Grundangriff";

    /// <summary>Unnamed skills every boss casts now and then (seen on all bosses of 2026-11-05), not a mechanic.</summary>
    public static bool IsGeneric(uint skill) => skill is 1000009 or 1001450;
}
