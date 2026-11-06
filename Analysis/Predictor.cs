using System.Globalization;
using System.Text;
using RaidAnsager.Engine;

namespace RaidAnsager.Analysis;

/// <summary>How well one boss skill can be predicted, and from what.</summary>
public sealed class Prediction
{
    public uint Npc;
    public string Boss = "";
    public uint Skill;
    public string? Caster;
    public int Casts;
    public int Pulls;
    /// <summary>Relevance: marked by players (hotkey), deaths it caused, hits on the recording player.</summary>
    public int Marks, Kills, HitsOnMe;

    /// <summary>Own cooldown: seconds between two casts.</summary>
    public Estimate? Interval;
    /// <summary>First cast after the pull.</summary>
    public Estimate? FromPull;
    /// <summary>Fixed gap after another boss skill.</summary>
    public Estimate? After;
    public uint AfterSkill;

    /// <summary>The predictor used for the timer: the best of the three with enough lead time, or null.</summary>
    /// <remarks>Needs at least two pulls: a pattern seen in a single pull may be chance.</remarks>
    public Estimate? Best => Pulls < 2 ? null : new[] { After, Interval is { Seconds: >= 6 } ? Interval : null }.Where(e => e is { Usable: true })
        .OrderByDescending(e => Math.Round(e!.Score, 1)).ThenByDescending(e => e!.Seconds).FirstOrDefault();

    public double Relevance => Marks * 10 + Kills * 5 + Math.Min(HitsOnMe, 20) * 0.2 + Casts * 0.01;

    /// <summary>Marked, deadly or hitting the recording player regularly: worth a spoken warning.</summary>
    public bool Important => Marks > 0 || Kills > 0 || HitsOnMe >= 3;
}

/// <param name="Seconds">typical gap</param>
/// <param name="Spread">median deviation of the hits from it</param>
/// <param name="Hits">samples that matched within the tolerance</param>
/// <param name="Samples">samples overall</param>
public sealed record Estimate(double Seconds, double Spread, int Hits, int Samples, double Score)
{
    /// <summary>Good enough for a countdown: at least 4 samples, 75 % reliable, 3 s or more of warning.</summary>
    public bool Usable => Samples >= 4 && Score >= 0.75 && Seconds >= 3;

    public string Describe() => $"{Seconds:0.0} s (±{Spread:0.0}, {Hits}/{Samples} = {Score:P0})";
}

/// <summary>
/// Learns countdowns from many pulls: per boss skill its cooldown, its first cast after the pull, and the boss skill
/// it reliably follows. Casts of the same skill within 1 s count as one.
/// </summary>
public static class Predictor
{
    private const double SameCast = 1.0;
    private const double AfterWindow = 45;

    private static double Tolerance(double seconds) => Math.Max(1.0, 0.12 * seconds);

    private static double Median(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        return v.Count == 0 ? double.NaN : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    public static List<Prediction> Run(IEnumerable<Fight> fights, GameData data)
    {
        var result = new List<Prediction>();
        foreach (var boss in fights.Where(f => (f.EndMs - f.PullMs) >= 15_000).GroupBy(f => f.Npc))
        {
            var pulls = boss.ToList();
            // per pull: casts per (caster, skill), deduplicated; boss casts only for predecessors
            var casts = pulls.Select(Casts).ToList();
            var keys = casts.SelectMany(c => c.Keys).Distinct().ToList();
            foreach (var key in keys)
            {
                var (caster, skill) = key;
                if (data.IsAutoAttack(skill) || GameData.IsGeneric(skill)) continue;
                if (caster is not null && data.SkillName(skill) is null) continue; // unnamed helper casts of adds
                var p = new Prediction
                {
                    Npc = boss.Key, Boss = FightReport.Boss(pulls[0], data), Skill = skill, Caster = caster,
                    Casts = casts.Sum(c => c.TryGetValue(key, out var l) ? l.Count : 0),
                    Pulls = casts.Count(c => c.ContainsKey(key)),
                };
                if (p.Casts < 3) continue;
                p.Interval = Interval(casts, key);
                p.FromPull = FromPull(casts, key);
                (p.After, p.AfterSkill) = BestAfter(casts, key, pulls, data);
                Relevance(p, pulls);
                result.Add(p);
            }
        }
        return result;
    }

    private static Dictionary<(string?, uint), List<double>> Casts(Fight f)
    {
        var map = new Dictionary<(string?, uint), List<double>>();
        foreach (var r in f.Records)
        {
            if (r.Type != "CAST") continue;
            var key = (r.Caster, r.Code);
            var t = (r.TimeMs - f.PullMs) / 1000.0;
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<double>();
            if (list.Count == 0 || t - list[^1] >= SameCast) list.Add(t);
        }
        return map;
    }

    private static Estimate? Interval(List<Dictionary<(string?, uint), List<double>>> casts, (string?, uint) key)
    {
        var gaps = new List<double>();
        foreach (var c in casts)
            if (c.TryGetValue(key, out var l))
                for (var i = 1; i < l.Count; i++) gaps.Add(l[i] - l[i - 1]);
        return Fit(gaps, gaps.Count);
    }

    private static Estimate? FromPull(List<Dictionary<(string?, uint), List<double>>> casts, (string?, uint) key)
    {
        var first = casts.Where(c => c.ContainsKey(key)).Select(c => c[key][0]).ToList();
        return first.Count >= 3 ? Fit(first, first.Count) : null;
    }

    /// <summary>Median of the samples, the share within the tolerance around it, and their spread.</summary>
    private static Estimate? Fit(List<double> samples, int outOf)
    {
        if (samples.Count < 2) return null;
        var typical = Median(samples);
        var tol = Tolerance(typical);
        var near = samples.Where(v => Math.Abs(v - typical) <= tol).ToList();
        if (near.Count == 0) return null;
        typical = near.Average();
        return new Estimate(typical, Median(near.Select(v => Math.Abs(v - typical))), near.Count, outOf,
            near.Count / (double)Math.Max(1, outOf));
    }

    /// <summary>
    /// The boss skill Y after which X comes at a fixed gap: coverage (how many X casts it explains) and precision (how
    /// many Y casts are really followed by X) must both be high, the score is the smaller of the two.
    /// </summary>
    private static (Estimate?, uint) BestAfter(List<Dictionary<(string?, uint), List<double>>> casts, (string?, uint) key,
        List<Fight> pulls, GameData data)
    {
        Estimate? best = null;
        uint bestSkill = 0;
        var xCount = casts.Sum(c => c.TryGetValue(key, out var l) ? l.Count : 0);
        // other boss skills only; parts of the same skill (code differs only in the last digit, or the same name) are
        // no warning
        var name = data.SkillName(key.Item2, "en");
        var candidates = casts.SelectMany(c => c.Keys).Distinct()
            .Where(k => k.Item1 is null && k != key && k.Item2 / 10 != key.Item2 / 10 &&
                        (name is null || data.SkillName(k.Item2, "en") != name)).ToList();
        foreach (var y in candidates)
        {
            var gaps = new List<double>();
            for (var p = 0; p < casts.Count; p++)
            {
                if (!casts[p].TryGetValue(key, out var xs) || !casts[p].TryGetValue(y, out var ys)) continue;
                foreach (var t in xs)
                {
                    var before = ys.LastOrDefault(u => u < t - 0.3, double.NaN);
                    if (!double.IsNaN(before) && t - before <= AfterWindow) gaps.Add(t - before);
                }
            }
            if (gaps.Count < 4) continue;
            var fit = Fit(gaps, xCount);
            if (fit is null || fit.Seconds < 2) continue;
            // precision: Y casts that are followed by X at that gap (only where the pull lasted long enough)
            var tol = Tolerance(fit.Seconds);
            int yAll = 0, yHit = 0;
            for (var p = 0; p < casts.Count; p++)
            {
                if (!casts[p].TryGetValue(y, out var ys)) continue;
                var len = (pulls[p].EndMs - pulls[p].PullMs) / 1000.0;
                var xs = casts[p].TryGetValue(key, out var l) ? l : [];
                foreach (var u in ys)
                {
                    if (u + fit.Seconds + tol > len) continue;
                    yAll++;
                    if (xs.Any(t => Math.Abs(t - u - fit.Seconds) <= tol)) yHit++;
                }
            }
            if (yAll < 4) continue;
            var precision = yHit / (double)yAll;
            var score = Math.Min(fit.Score, precision);
            var est = fit with { Score = score };
            if (best is null || score > best.Score + 0.02 || Math.Abs(score - best.Score) <= 0.02 && est.Seconds > best.Seconds)
            {
                best = est;
                bestSkill = y.Item2;
            }
        }
        return (best, bestSkill);
    }

    private static void Relevance(Prediction p, List<Fight> pulls)
    {
        foreach (var f in pulls)
        {
            double lastHit = double.NegativeInfinity;
            for (var i = 0; i < f.Records.Count; i++)
            {
                var r = f.Records[i];
                if (r.Code == p.Skill && r.Type == "MARK") p.Marks++;
                if (r.Code == p.Skill && r.Type == "HITME" && r.TimeMs / 1000.0 - lastHit >= SameCast)
                {
                    p.HitsOnMe++;
                    lastHit = r.TimeMs / 1000.0;
                }
                if (r.Type != "TOD") continue;
                // what killed: the last NPC hit on us, or the last cast/hit aimed at that player, within 3 s
                for (var j = i - 1; j >= 0 && r.TimeMs - f.Records[j].TimeMs <= 3_000; j--)
                {
                    var q = f.Records[j];
                    var onThem = r.TargetKind == "me" ? q.Type == "HITME" : q.Target == r.Target && q.Type is "CAST" or "HIT";
                    if (!onThem) continue;
                    if (q.Code == p.Skill) p.Kills++;
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ output

    public static string Report(List<Prediction> all, GameData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.T("Raid Announcer – countdown predictions", "Raid-Ansager – Vorhersagen für Countdowns"));
        sb.AppendLine(L.T(
            "Per boss skill: cooldown (own gap), first cast after the pull, and the boss skill it follows. A timer needs ≥ 4 samples, ≥ 75 % hits and ≥ 3 s warning.",
            "Je Boss-Fähigkeit: Abklingzeit (eigener Abstand), erster Einsatz nach dem Pull und die Boss-Fähigkeit, nach der sie kommt. Ein Timer braucht ≥ 4 Beispiele, ≥ 75 % Treffer und ≥ 3 s Vorwarnung."));
        sb.AppendLine(L.T("Relevance: M = marked by players, K = killed someone, H = hits on the recording player.",
            "Wichtigkeit: M = von Spielern markiert, K = hat jemanden getötet, H = Treffer auf den Aufnehmenden."));
        foreach (var boss in all.GroupBy(p => p.Npc).OrderByDescending(g => g.Max(p => p.Pulls)))
        {
            var first = boss.First();
            sb.AppendLine();
            sb.AppendLine($"=== {first.Boss} (NPC {boss.Key}), {boss.Max(p => p.Pulls)} Pull(s)");
            foreach (var p in boss.OrderByDescending(p => p.Best is not null).ThenByDescending(p => p.Relevance))
            {
                var name = (p.Caster is null ? "" : $"[{p.Caster}] ") + (data.SkillName(p.Skill) ?? $"#{p.Skill}");
                var tag = $"M{p.Marks} K{p.Kills} H{p.HitsOnMe}";
                var best = p.Best;
                var what = best is null ? L.T("not predictable yet", "noch nicht vorhersagbar")
                    : best == p.After ? L.T($"TIMER {best.Seconds:0.0} s after \"{data.SkillName(p.AfterSkill) ?? "#" + p.AfterSkill}\"",
                        $"TIMER {best.Seconds:0.0} s nach „{data.SkillName(p.AfterSkill) ?? "#" + p.AfterSkill}“")
                    : L.T($"TIMER every {best.Seconds:0.0} s", $"TIMER alle {best.Seconds:0.0} s");
                sb.AppendLine($"  {p.Skill,-9} {name,-34} {p.Casts,4}× {tag,-12} {what}");
                sb.AppendLine($"            {L.T("cooldown", "Abstand")}: {p.Interval?.Describe() ?? "-"} · " +
                              $"{L.T("after pull", "nach Pull")}: {p.FromPull?.Describe() ?? "-"} · " +
                              $"{L.T("after", "nach")} {(p.AfterSkill == 0 ? "-" : data.SkillName(p.AfterSkill) ?? "#" + p.AfterSkill)}: {p.After?.Describe() ?? "-"}");
            }
        }
        return sb.ToString();
    }

    /// <summary>A mod with countdown bars for the predictable skills (at most <paramref name="perBoss"/> per boss,
    /// marked and deadly ones first).</summary>
    public static BossMod Mod(List<Prediction> all, GameData data, int perBoss = 4)
    {
        var mod = new BossMod
        {
            Id = "dungeon-timers",
            Name = "Dungeon countdowns (learned from member logs)",
            Version = DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture),
            Notes = "Generated by 'RaidAnsager analyse'. Every timer was right in at least 75 % of the recorded casts. " +
                    "Spoken warnings only for marked, deadly or often hitting skills; the others are silent bars.",
        };
        foreach (var boss in all.GroupBy(p => p.Npc))
        {
            // several codes with one name (parts of a skill): one timer, the most relevant
            var chosen = boss.Where(p => p.Best is not null && data.SkillName(p.Skill) is not null && (p.Caster is null || p.Important))
                .GroupBy(p => data.SkillName(p.Skill, "en"))
                .Select(g => g.OrderByDescending(p => p.Relevance).ThenByDescending(p => p.Best!.Score).First())
                .OrderByDescending(p => p.Relevance).ThenByDescending(p => p.Best!.Score).Take(perBoss).ToList();
            if (chosen.Count == 0) continue;
            var def = new BossDef { Id = boss.Key.ToString(CultureInfo.InvariantCulture), Name = data.NpcName(boss.Key, "en") ?? boss.First().Boss, Npc = { boss.Key } };
            foreach (var p in chosen)
            {
                var best = p.Best!;
                var text = LocalText.Of(data.SkillName(p.Skill, "en"), data.SkillName(p.Skill, "de"));
                var bar = new BarDef
                {
                    Text = text, In = Math.Round(best.Seconds - best.Spread, 1), Warn = best.Seconds >= 10 ? 5 : 3,
                    WarnSay = p.Important ? null : (LocalText)"", // spoken "… in 3" only for the important ones, the rest is a silent bar
                };
                var note = $"{p.Casts}× in {p.Pulls} pull(s), M{p.Marks} K{p.Kills} H{p.HitsOnMe}; " +
                           (best == p.After ? $"after {p.AfterSkill}: " : "cooldown: ") + best.Describe();
                if (best == p.After)
                    def.Triggers.Add(new Trigger
                    {
                        Name = $"{data.SkillName(p.Skill, "en")} ← {data.SkillName(p.AfterSkill, "en") ?? p.AfterSkill.ToString(CultureInfo.InvariantCulture)}",
                        On = "cast", Skill = { p.AfterSkill }, Bar = bar, Cooldown = 1, Note = note,
                    });
                else
                    def.Triggers.Add(new Trigger
                    {
                        Name = $"{data.SkillName(p.Skill, "en")} (cooldown)", On = "cast", Skill = { p.Skill }, Bar = bar,
                        Cooldown = 1, Note = note,
                    });
                if (p.FromPull is { Usable: true } pull)
                    def.Triggers.Add(new Trigger
                    {
                        Name = $"{data.SkillName(p.Skill, "en")} (first)", On = "pull",
                        Bar = new BarDef
                        {
                            Text = text, In = Math.Round(pull.Seconds - pull.Spread, 1), Warn = pull.Seconds >= 10 ? 5 : 3,
                            WarnSay = p.Important ? null : (LocalText)"",
                        },
                        Note = $"first cast after the pull: {pull.Describe()}",
                    });
                // the cast itself is still read out (like in learning mode)
                def.Triggers.Add(new Trigger
                {
                    Name = data.SkillName(p.Skill, "en") ?? p.Skill.ToString(CultureInfo.InvariantCulture), On = "cast",
                    Skill = { p.Skill }, Say = text, Sound = "none", ShowSeconds = 2.5,
                });
            }
            mod.Bosses.Add(def);
        }
        return mod;
    }
}
