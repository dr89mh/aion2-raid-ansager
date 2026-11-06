using System.Globalization;
using System.Text;
using System.Text.Json;
using RaidAnsager.Engine;

namespace RaidAnsager.Analysis;

/// <summary>Per-skill statistics of one or more pulls of the same boss.</summary>
public sealed class SkillStat
{
    public uint Code;
    public string? Name;
    public string? Caster;
    public int Casts;
    public int Pulls;
    public readonly List<double> First = new();     // seconds after pull, one per pull
    public readonly List<double> Intervals = new(); // seconds between casts within a pull
    public readonly List<double> Leads = new();     // seconds from cast to first hit
    public readonly List<double> Hp = new();        // boss HP % at the casts
    public bool Known;

    public double Median(List<double> v) => v.Count == 0 ? double.NaN : v.Order().ElementAt(v.Count / 2);
}

public static class FightReport
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Casts of the same skill closer than this belong to one use (one packet per target).</summary>
    private const double SameCastSec = 1.0;

    public static string Clock(double sec) => sec < 0 ? "-" + Clock(-sec) : $"{(int)(sec / 60)}:{sec % 60:00.0}";

    public static List<SkillStat> Skills(IEnumerable<Fight> fights, GameData data)
    {
        var stats = new Dictionary<(uint, string?), SkillStat>();
        foreach (var f in fights)
        {
            var lastCast = new Dictionary<(uint, string?), double>();
            var seen = new HashSet<(uint, string?)>();
            foreach (var r in f.Records)
            {
                if (r.Type is not ("CAST" or "HIT")) continue;
                var sec = (r.TimeMs - f.PullMs) / 1000.0;
                var caster = r.Caster;
                var key = (r.Code, caster);
                if (!stats.TryGetValue(key, out var s))
                    stats[key] = s = new SkillStat { Code = r.Code, Name = r.Name ?? data.SkillName(r.Code), Caster = caster };
                if (r.Type == "HIT")
                {
                    if (!double.IsNaN(r.Value)) s.Leads.Add(r.Value);
                    continue;
                }
                if (r.Known) s.Known = true;
                if (lastCast.TryGetValue(key, out var prev) && sec - prev < SameCastSec) continue;
                if (lastCast.TryGetValue(key, out prev)) s.Intervals.Add(sec - prev);
                lastCast[key] = sec;
                s.Casts++;
                if (!double.IsNaN(r.HpPct)) s.Hp.Add(r.HpPct);
                if (seen.Add(key))
                {
                    s.Pulls++;
                    s.First.Add(sec);
                }
            }
        }
        return stats.Values.OrderBy(s => s.Caster is null ? 0 : 1).ThenBy(s => s.Median(s.First)).ToList();
    }

    /// <summary>The readable log of one pull: header, timeline, skill table.</summary>
    public static string Text(Fight f, GameData data)
    {
        var sb = new StringBuilder();
        var start = DateTimeOffset.FromUnixTimeMilliseconds(f.PullMs).ToLocalTime();
        sb.AppendLine(L.T("Raid Announcer – fight log", "Raid-Ansager – Kampfprotokoll"));
        sb.AppendLine($"Boss:     {Boss(f, data)} (NPC {f.Npc}, Entity {f.Entity})");
        sb.AppendLine($"Zone:     {f.Zone}    Mod: {f.Mod.Id} {f.Mod.Version}");
        sb.AppendLine(L.T(
            $"Start:    {start:yyyy-MM-dd HH:mm:ss}    Duration: {Clock((f.EndMs - f.PullMs) / 1000.0)}    Result: {f.Outcome}",
            $"Beginn:   {start:dd.MM.yyyy HH:mm:ss}    Dauer: {Clock((f.EndMs - f.PullMs) / 1000.0)}    Ergebnis: {f.Outcome}"));
        sb.AppendLine();
        sb.AppendLine(L.T("Skills (casts = uses; several packets within 1 s count as one):",
            "Fähigkeiten (Casts = Einsätze, mehrere Pakete innerhalb 1 s zählen als einer):"));
        sb.AppendLine(SkillTable(Skills([f], data)));
        sb.AppendLine(L.T("Timeline (time since pull, boss HP):", "Zeitleiste (Zeit ab Pull, Boss-HP):"));
        foreach (var r in f.Records)
        {
            var sec = (r.TimeMs - f.PullMs) / 1000.0;
            var hp = double.IsNaN(r.HpPct) ? "   ?  " : $"{r.HpPct,5:0.0}%";
            var what = r.Type switch
            {
                "CAST" => $"CAST    {(r.Caster is { } c ? $"[{c}] " : "")}{r.Code} {r.Name ?? "?"}{(r.Known ? " ✓" : "")}" +
                          (r.Detail is { } d ? $"  → {d}" : ""),
                "HIT" => $"  {L.T("hit", "Treffer")} {(r.Caster is { } c2 ? $"[{c2}] " : "")}{r.Code} {r.Name ?? "?"}" +
                         (r.Detail is { } d2 ? $"  → {d2}" : "") + $"  ({r.Value:0.0} s {L.T("after the cast", "nach Cast")})",
                "BUFF" => $"BUFF    {r.Code} {r.Name}" + (r.Detail is { } d3 ? $"  → {d3}" : ""),
                "SPAWN" => $"SPAWN   NPC {r.Code} {r.Name ?? "?"} (Entity {r.Actor})",
                "NPC-TOD" => $"{L.T("NPC-DIED", "NPC-TOD")} {r.Code} {r.Name ?? "?"}",
                "TOD" => $"{L.T("DIED   ", "TOD    ")} {r.Name ?? $"{L.T("player", "Spieler")} {r.Target}"}",
                "TRIGGER" => $">>> {L.T("CALLOUT", "ANSAGE")} {r.Name}" + (r.Detail is { } d4 ? $": {d4}" : ""),
                "ENDE" => $"{L.T("END", "ENDE")}    {r.Name}",
                _ => $"{r.Type,-7} {(r.Code != 0 ? r.Code + " " : "")}{r.Name}" + (r.Detail is { } d5 ? $"  {d5}" : ""),
            };
            sb.AppendLine($"{Clock(sec),8}  {hp}  {what}");
        }
        return sb.ToString();
    }

    public static string SkillTable(List<SkillStat> skills)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.T("  Code      Name                              Casts  first    gap min/median/max       lead     HP at casts",
                          "  Code      Name                              Casts  erster   Abstand min/Median/max   Vorlauf  HP bei Casts"));
        foreach (var s in skills)
        {
            var name = (s.Caster is null ? "" : $"[{s.Caster}] ") + (s.Name ?? "?") + (s.Known ? " ✓" : "");
            if (name.Length > 33) name = name[..32] + "…";
            var iv = s.Intervals.Count == 0 ? "-" :
                $"{s.Intervals.Min(),5:0.0} / {s.Median(s.Intervals),5:0.0} / {s.Intervals.Max(),5:0.0}";
            var lead = s.Leads.Count == 0 ? "-" : $"{s.Median(s.Leads):0.0} s";
            var hp = s.Hp.Count == 0 ? "" : string.Join(" ", s.Hp.Take(8).Select(h => $"{h:0}"));
            if (s.Hp.Count > 8) hp += " …";
            sb.AppendLine($"  {s.Code,-9} {name,-33} {s.Casts,5}  {Clock(s.Median(s.First)),7}   {iv,-23}  {lead,7}  {hp}");
        }
        return sb.ToString();
    }

    /// <summary>A draft mod with one trigger per observed boss skill; to be renamed and curated by hand.</summary>
    public static BossMod Draft(IReadOnlyList<Fight> fights, GameData data)
    {
        var first = fights[0];
        var mod = new BossMod
        {
            Id = first.Mod.Id == "lernmodus" ? $"zone-{first.Zone}" : first.Mod.Id,
            Name = $"Entwurf aus {fights.Count} Pull(s), Zone {first.Zone}",
            Version = DateTime.Now.ToString("yyyy-MM-dd-'entwurf'", CultureInfo.InvariantCulture),
            Zones = { first.Zone },
            Notes = "Automatisch erzeugt. Ansagetexte anpassen, unnötige Trigger löschen, dann version erhöhen.",
        };
        foreach (var group in fights.GroupBy(f => f.Def.Id))
        {
            var def = group.First().Def;
            var boss = new BossDef { Id = def.Id, Name = Boss(group.First(), data), Npc = def.Npc.ToList() };
            foreach (var s in Skills(group, data))
            {
                if (data.IsAutoAttack(s.Code) || GameData.IsGeneric(s.Code)) continue;
                if (s.Caster is not null && s.Name is null) continue; // unnamed helper casts of adds/effects
                var series = s.Intervals.Count >= 8 && s.Median(s.Intervals) < 5;
                var label = s.Name ?? $"{L.T("Skill", "Fähigkeit")} {s.Code}";
                string? en = data.SkillName(s.Code, "en"), de = data.SkillName(s.Code, "de");
                var t = new Trigger
                {
                    Name = (s.Caster is null ? "" : s.Caster + ": ") + label,
                    On = "cast",
                    Skill = { s.Code },
                    Say = en is null && de is null ? null : LocalText.Of(en, de),
                    Show = LocalText.Of((en ?? $"Skill {s.Code}").ToUpperInvariant(), (de ?? $"Fähigkeit {s.Code}").ToUpper(De)),
                    Cooldown = series ? 20 : 3,
                    Note = $"{s.Casts}× in {s.Pulls} Pull(s), erster nach {Clock(s.Median(s.First))}" +
                           (s.Intervals.Count > 0 ? $", Abstand {s.Intervals.Min():0}–{s.Intervals.Max():0} s (Median {s.Median(s.Intervals):0})" : "") +
                           (s.Leads.Count > 0 ? $", Treffer {s.Median(s.Leads):0.0} s nach Cast" : ""),
                };
                if (series) t.Note += "; kommt in Serien → nur einmal je 20 s";
                else if (Regular(s) is { } every)
                {
                    t.Bar = new BarDef { Text = LocalText.Of(en ?? label, de ?? label), In = Math.Round(every, 1), Warn = 5 };
                    t.Note += $"; regelmäßig alle {every:0.0} s → Balken";
                }
                boss.Triggers.Add(t);
            }
            mod.Bosses.Add(boss);
        }
        return mod;
    }

    /// <summary>The usual gap when at least 70 % of the gaps lie within ±15 % of the median (skips and phase pauses
    /// do not count against it), otherwise null.</summary>
    public static double? Regular(SkillStat s)
    {
        if (s.Intervals.Count < 3) return null;
        var med = s.Median(s.Intervals);
        if (med < 8) return null;
        var near = s.Intervals.Where(v => Math.Abs(v - med) <= 0.15 * med).ToList();
        return near.Count >= 0.7 * s.Intervals.Count ? near.Average() : null;
    }

    public static string DraftJson(BossMod mod) => JsonSerializer.Serialize(mod, ModLoader.Compact);

    /// <summary>The boss name in the current language.</summary>
    public static string Boss(Fight f, GameData data) => data.NpcName(f.Npc) ?? f.Def.Name;

    private static readonly JsonSerializerOptions Compact = new(ModLoader.Json) { WriteIndented = false };

    /// <summary>Machine-readable copy of the records (one JSON object per line).</summary>
    public static IEnumerable<string> Jsonl(Fight f)
    {
        foreach (var r in f.Records)
            yield return JsonSerializer.Serialize(new
            {
                t = (r.TimeMs - f.PullMs) / 1000.0, type = r.Type, code = r.Code, name = r.Name, detail = r.Detail,
                hp = double.IsNaN(r.HpPct) ? (double?)null : Math.Round(r.HpPct, 2), actor = r.Actor, target = r.Target,
                caster = r.Caster, value = double.IsNaN(r.Value) ? (double?)null : Math.Round(r.Value, 3), known = r.Known,
            }, Compact);
    }
}

/// <summary>Writes the log files of every finished pull to the fight folder.</summary>
public sealed class FightLogWriter
{
    private readonly string _dir;
    private readonly GameData _data;

    public FightLogWriter(string dir, GameData data)
    {
        _dir = dir;
        _data = data;
    }

    public string? LastFile { get; private set; }

    /// <summary>Pulls shorter than this without any trigger are noise (a stray hit on a boss) and are not written.</summary>
    public double MinSeconds { get; set; } = 10;

    public void Write(Fight f)
    {
        if ((f.EndMs - f.PullMs) / 1000.0 < MinSeconds && f.Records.All(r => r.Type != "TRIGGER")) return;
        Directory.CreateDirectory(_dir);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(f.PullMs).ToLocalTime();
        var safe = string.Concat(FightReport.Boss(f, _data).Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
        var stem = Path.Combine(_dir, $"{start:yyyyMMdd_HHmmss}_{safe}");
        File.WriteAllText(stem + ".txt", FightReport.Text(f, _data), new UTF8Encoding(true));
        File.WriteAllLines(stem + ".jsonl", FightReport.Jsonl(f));
        File.WriteAllText(stem + ".entwurf.json", FightReport.DraftJson(FightReport.Draft([f], _data)), new UTF8Encoding(false));
        LastFile = stem + ".txt";
    }
}
