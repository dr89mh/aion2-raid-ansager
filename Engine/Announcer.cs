using RaidAnsager.Protocol;

namespace RaidAnsager.Engine;

/// <summary>Where callouts go (speech + sound). Overlay content is read through <see cref="Announcer.Snapshot"/>.</summary>
public interface IAnnouncerOutput
{
    void Say(string text, bool urgent);
    void Sound(string kind);
}

public sealed class NullOutput : IAnnouncerOutput
{
    public void Say(string text, bool urgent) { }
    public void Sound(string kind) { }
}

public sealed class AnnouncerOptions
{
    /// <summary>alle | tank | heiler | dd</summary>
    public string Role { get; set; } = "alle";
    /// <summary>Read out boss skills that no trigger covers (learning mode for new bosses).</summary>
    public bool SayUnknown { get; set; } = true;
    /// <summary>Bosses without a mod start a fight too (learning mode: their skills are read out by name).</summary>
    public bool AllBosses { get; set; }
    /// <summary>With <see cref="AllBosses"/>: only dungeon/raid bosses, not field bosses of the open world.</summary>
    public bool InstanceBossesOnly { get; set; }
    /// <summary>Ids of mods that are loaded but switched off.</summary>
    public HashSet<string> DisabledMods { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <param name="Caster">Name of the NPC that cast, when it was not the boss itself.</param>
/// <param name="Value">HIT: seconds since the matching cast; MARK: seconds since the marked cast.</param>
/// <param name="Known">CAST: a trigger of the mod covers this skill.</param>
/// <param name="TargetKind">"me", "player", "npc" or "" – the analysis format keeps only this, never a player name.</param>
/// <param name="TargetName">Name of the target, for the readable log only.</param>
/// <param name="TargetNpc">NPC code of the target when it is an NPC.</param>
public sealed record FightRecord(long TimeMs, string Type, uint Code, string? Name, string? Detail, double HpPct,
    uint Actor, uint Target, string? Caster, double Value, bool Known, string TargetKind = "", string? TargetName = null,
    uint TargetNpc = 0);

/// <summary>One pull of one boss, from the first combat event to kill, reset or zone change.</summary>
public sealed class Fight
{
    internal Fight(BossMod mod, BossDef def, uint entity, uint npc, uint zone, long pullMs)
    {
        Mod = mod;
        Def = def;
        Entity = entity;
        Npc = npc;
        Zone = zone;
        PullMs = pullMs;
        LastActivityMs = pullMs;
    }

    public BossMod Mod { get; }
    public BossDef Def { get; }
    public uint Entity { get; }
    public uint Npc { get; }
    public uint Zone { get; }
    public long PullMs { get; }
    public long EndMs { get; internal set; }
    public string Outcome { get; internal set; } = "";
    /// <summary>The boss died (as opposed to a reset/wipe, zone change, …).</summary>
    public bool Won { get; internal set; }
    /// <summary>The fight ended without a kill while still in the zone (wipe or reset).</summary>
    public bool Reset { get; internal set; }
    public int Phase { get; internal set; } = 1;
    public double HpPct { get; internal set; } = double.NaN;
    public long LastActivityMs { get; internal set; }
    public List<FightRecord> Records { get; } = new();

    internal long? BattleOffMs;
    internal int HpStep = 100;
    internal readonly HashSet<Trigger> Fired = new();
    internal readonly Dictionary<Trigger, long> LastFire = new();
    internal readonly Dictionary<uint, long> LastUnknownSay = new();
    internal readonly Dictionary<uint, long> OpenCasts = new(); // skill -> cast time, for the first hit after it
    internal readonly List<Bar> Bars = new();
    internal (uint Skill, long At)? LastHitOnMe;
}

internal sealed class Bar
{
    public required string Text;
    public required long EndMs;
    public required long TotalMs;
    public required BarDef Def;
    public bool Warned;
}

internal readonly record struct Delayed(long DueMs, Trigger Trigger, Fight Fight, Ctx Ctx);

internal readonly record struct Ctx(uint Skill, uint Target, uint Stack, uint Npc);

public sealed record BarView(string Text, double Remaining, double Total, bool Warning);
public sealed record AlertView(string Text, string Color, double Age, double Seconds);
public sealed record BossView(string Name, double HpPct, int Phase, double Seconds);
public sealed record OverlayState(IReadOnlyList<BossView> Bosses, IReadOnlyList<AlertView> Alerts,
    IReadOnlyList<BarView> Bars, IReadOnlyList<string> Unknown, uint Zone, bool InModZone);

/// <summary>
/// The announcer: tracks entities and boss fights from <see cref="GameEvent"/>s and fires the triggers of the loaded
/// boss mods. Thread-safe; events come from the capture thread, <see cref="Tick"/> and <see cref="Snapshot"/> from the UI.
/// </summary>
public sealed class Announcer
{
    private sealed class Ent
    {
        public uint Npc;
        public byte Kind;
        public string? Name;
        public bool Player;
        public long HpMax;
        public long HpCur;
        /// <summary>Time of death; a dead boss is not pulled again by late ticks until it spawns anew.</summary>
        public long DeadAt;
    }

    private readonly object _gate = new();
    private readonly GameData _data;
    private readonly IAnnouncerOutput _out;
    private readonly Dictionary<uint, Ent> _ents = new();
    private readonly List<Fight> _fights = new();
    private readonly List<(string Text, string Color, long At, double Seconds)> _alerts = new();
    private readonly List<(string Text, long At)> _unknown = new();
    private readonly List<Delayed> _delayed = new();
    private IReadOnlyList<BossMod> _mods;
    private uint _self;
    private uint _zone;
    private long _now;
    private long _lastUnknownSayMs;

    public Announcer(GameData data, IReadOnlyList<BossMod> mods, IAnnouncerOutput output, AnnouncerOptions options)
    {
        _data = data;
        _mods = mods;
        _out = output;
        Options = options;
    }

    public AnnouncerOptions Options { get; }

    /// <summary>Raised (under the engine lock) when a fight ends, for the fight log.</summary>
    public event Action<Fight>? FightEnded;

    public uint Zone { get { lock (_gate) return _zone; } }

    /// <summary>A boss fight is running (the updater waits for its end).</summary>
    public bool InFight { get { lock (_gate) return _fights.Count > 0; } }

    public bool InModZone { get { lock (_gate) return ModZone(_zone); } }

    public void SetMods(IReadOnlyList<BossMod> mods)
    {
        lock (_gate)
        {
            foreach (var f in _fights.ToList()) End(f, _now, L.T("mods reloaded", "Mods neu geladen"));
            _mods = mods;
        }
    }

    private bool ModZone(uint zone) => _mods.Any(m => m.Zones.Count > 0 && m.Zones.Contains(zone));

    // ------------------------------------------------------------------ events

    public void OnEvent(GameEvent e)
    {
        lock (_gate)
        {
            if (e.TimeMs > _now) _now = e.TimeMs;
            switch (e)
            {
                case ZoneEvent z: OnZone(z); break;
                case SelfEvent s: _self = s.Entity; break;
                case PlayerEvent p:
                {
                    var ent = Get(p.Entity);
                    ent.Player = true;
                    ent.Name = p.Name;
                    ent.Npc = 0;
                    break;
                }
                case SpawnEvent s: OnSpawn(s); break;
                case CastEvent c: OnCast(c); break;
                case HitEvent h: OnHit(h); break;
                case HpEvent h: OnHp(h); break;
                case KillEvent k: OnDeath(k.TimeMs, k.Entity, "Tod"); break;
                case GoneEvent g:
                    if (g.Flag == 3) OnDeath(g.TimeMs, g.Entity, "Tod");
                    else if (g.Flag == 7 && FightOf(g.Entity) is { } gone) End(gone, g.TimeMs, L.T("boss gone", "Boss verschwunden"));
                    break;
                case BuffEvent b: OnBuff(b); break;
                case BattleEvent b: OnBattle(b); break;
            }
        }
    }

    private Ent Get(uint id)
    {
        if (!_ents.TryGetValue(id, out var ent)) _ents[id] = ent = new Ent();
        return ent;
    }

    private void OnZone(ZoneEvent z)
    {
        if (z.Zone == _zone) return;
        foreach (var f in _fights.ToList()) End(f, z.TimeMs, L.T("zone change", "Zonenwechsel"));
        _zone = z.Zone;
        var self = _self;
        var names = _ents.Where(kv => kv.Value.Player).ToList();
        _ents.Clear();
        foreach (var (id, ent) in names) _ents[id] = ent; // player names stay valid across zones
        _self = self;
    }

    private void OnSpawn(SpawnEvent s)
    {
        if (s.Entity == _self) return;
        var ent = Get(s.Entity);
        ent.DeadAt = 0;
        if (s.NpcCode != 0)
        {
            ent.Npc = s.NpcCode;
            ent.Kind = s.Kind;
            ent.Player = false;
            ent.Name = _data.NpcName(s.NpcCode);
            if (s.HpMax > 0)
            {
                ent.HpMax = s.HpMax;
                ent.HpCur = s.HpCur;
            }
        }
        if (s.NpcCode == 0 || _fights.Count == 0) return;
        foreach (var f in _fights)
        {
            if (f.Entity == s.Entity) continue;
            if (s.Kind is 0x0C or 0x0D)
                Record(f, s.TimeMs, "SPAWN", s.NpcCode, Label(ent), null, actor: s.Entity);
            foreach (var t in Active(f, "spawn"))
                if (t.Npc.Contains(s.NpcCode)) Fire(f, t, s.TimeMs, new Ctx(0, s.Entity, 0, s.NpcCode));
        }
    }

    private void OnCast(CastEvent c)
    {
        var actor = Get(c.Actor);
        if (actor.Player || c.Actor == _self) return;
        var fight = FightOf(c.Actor) ?? Pull(c.Actor, c.TimeMs) ?? Adopt(c.Actor, c.Skill, c.TimeMs);
        if (fight is null)
        {
            // an add or effect entity of a running fight; player skills and summons are not of interest
            if (_fights.Count == 0 || !IsNpcSkill(c.Skill) || IsSummon(actor.Kind)) return;
            fight = _fights.OrderByDescending(f => f.LastActivityMs).First();
        }
        var isBoss = fight.Entity == c.Actor;
        if (isBoss) Activity(fight, c.TimeMs);

        var matched = false;
        foreach (var t in Active(fight, "cast"))
            if (t.Skill.Contains(c.Skill) && (t.Npc.Count == 0 || t.Npc.Contains(actor.Npc)) && TargetOk(t, fight, c.Target))
            {
                matched = true;
                Fire(fight, t, c.TimeMs, new Ctx(c.Skill, c.Target, 0, actor.Npc));
            }

        var name = _data.SkillName(c.Skill);
        var known = matched || Covered(fight, c.Skill);
        Record(fight, c.TimeMs, "CAST", c.Skill, name, null, actor: c.Actor, target: c.Target,
            caster: isBoss ? null : Label(actor) ?? $"Entity {c.Actor}", known: known);
        fight.OpenCasts[c.Skill] = c.TimeMs;
        if (!known && (isBoss || actor.Npc != 0) && IsNpcSkill(c.Skill)) Unknown(fight, c, name, isBoss ? null : Label(actor));
    }

    private void OnHit(HitEvent h)
    {
        var actor = Get(h.Actor);
        // Players we have no name for yet use player skill codes; summons count for their owner.
        var npcActor = !actor.Player && h.Actor != _self &&
                       (IsNpcSkill(h.Skill) || actor.Npc != 0 && !IsSummon(actor.Kind) && h.Skill < 10_000_000);
        if (!npcActor)
        {
            if (h.Target == _self || Get(h.Target).Player) return;
            var hit = FightOf(h.Target) ?? Pull(h.Target, h.TimeMs);
            if (hit is not null) Activity(hit, h.TimeMs);
            return;
        }

        var fight = FightOf(h.Actor) ?? Pull(h.Actor, h.TimeMs);
        if (fight is null)
        {
            if (_fights.Count == 0 || !IsNpcSkill(h.Skill)) return;
            fight = _fights.OrderByDescending(f => f.LastActivityMs).First();
        }
        if (fight.Entity == h.Actor) Activity(fight, h.TimeMs);
        if (fight.OpenCasts.Remove(h.Skill, out var castAt))
            Record(fight, h.TimeMs, "HIT", h.Skill, _data.SkillName(h.Skill), null,
                actor: h.Actor, target: h.Target, caster: fight.Entity == h.Actor ? null : Label(actor),
                value: (h.TimeMs - castAt) / 1000.0);
        if (h.Target == _self && h.Target != 0)
        {
            // every NPC hit on us: what hurts most and what killed us
            Record(fight, h.TimeMs, "HITME", h.Skill, _data.SkillName(h.Skill), null, actor: h.Actor, target: h.Target,
                caster: fight.Entity == h.Actor ? null : Label(actor));
            fight.LastHitOnMe = (h.Skill, h.TimeMs);
        }
        foreach (var t in Active(fight, "hit"))
            if (t.Skill.Contains(h.Skill) && (t.Npc.Count == 0 || t.Npc.Contains(actor.Npc)) && TargetOk(t, fight, h.Target))
                Fire(fight, t, h.TimeMs, new Ctx(h.Skill, h.Target, 0, actor.Npc));
    }

    private static bool IsSummon(byte kind) => kind is 0x1D or 0x1F or 0x5D or 0x5F;

    private void OnHp(HpEvent h)
    {
        var ent = Get(h.Entity);
        // The maximum comes from the spawn; without it (joined mid-fight) the percentage is unknown.
        if (ent.HpMax > 0 && h.Hp > ent.HpMax) ent.HpMax = h.Hp;
        var fight = FightOf(h.Entity);
        if (fight is null) return;
        Activity(fight, h.TimeMs);
        if (ent.HpMax <= 0) return;
        var pct = 100.0 * h.Hp / ent.HpMax;
        var before = fight.HpPct;
        fight.HpPct = pct;
        if (!double.IsNaN(before) && before < 95 && pct >= 99.9)
        {
            End(fight, h.TimeMs, L.T("reset (boss back to full HP)", "Reset (Boss wieder voll)"), reset: true);
            return;
        }
        while (fight.HpStep - 5 >= pct && fight.HpStep > 0)
        {
            fight.HpStep -= 5;
            Record(fight, h.TimeMs, "HP", 0, $"{fight.HpStep} %", null);
        }
        foreach (var t in Active(fight, "hp"))
        {
            if (t.Below is not { } below || pct >= below || fight.Fired.Contains(t)) continue;
            if (double.IsNaN(before) && below > pct + 2)
            {
                fight.Fired.Add(t); // already past this threshold when we got the first HP value: stay quiet
                continue;
            }
            Fire(fight, t, h.TimeMs, default); // marks it fired: hp triggers run once per pull
        }
    }

    private void OnDeath(long time, uint entity, string what)
    {
        if (_ents.TryGetValue(entity, out var dead) && dead.DeadAt == 0) dead.DeadAt = time;
        if (FightOf(entity) is { } fight)
        {
            var defeated = L.T("{boss} defeated", "{boss} besiegt");
            var say = fight.Def.OnKill?.Get() ?? defeated;
            if (say.Length > 0) _out.Say(Format(say, fight, default), urgent: false);
            Alert(Format(defeated, fight, default), "#7CFC7C", time, 5);
            fight.Won = true;
            End(fight, time, L.T("kill", "Sieg"));
            return;
        }
        if (_fights.Count == 0 || !_ents.TryGetValue(entity, out var ent)) return;
        foreach (var f in _fights)
        {
            if (ent.Player || entity == _self)
                Record(f, time, "TOD", 0, entity == _self ? L.T("you", "du") : ent.Name, null, target: entity);
            else if (ent.Npc != 0)
            {
                Record(f, time, "NPC-TOD", ent.Npc, Label(ent), null, target: entity);
                foreach (var t in Active(f, "death"))
                    if (t.Npc.Contains(ent.Npc)) Fire(f, t, time, new Ctx(0, entity, 0, ent.Npc));
            }
        }
    }

    private void OnBuff(BuffEvent b)
    {
        if (_fights.Count == 0) return;
        // Only what NPCs do: player debuffs on the boss and summon auras are noise here.
        var caster = _ents.GetValueOrDefault(b.Caster);
        var npcCaster = caster is { Player: false, Npc: not 0 } && !IsSummon(caster.Kind) || _fights.Any(f => f.Entity == b.Caster);
        var fromNpc = npcCaster && b.Caster != _self && (b.SourceSkill == 0 || IsNpcSkill(b.SourceSkill));
        foreach (var f in _fights)
        {
            var onBoss = b.Target == f.Entity;
            if (fromNpc)
                Record(f, b.TimeMs, "BUFF", b.Buff, onBoss ? L.T("on boss", "auf Boss") : null,
                    $"{L.T("stack", "Stapel")} {b.Stack}" +
                    (b.DurationMs > 0 ? $", {b.DurationMs / 1000.0:0.#} s" : "") +
                    (b.SourceSkill != 0 ? $", {L.T("from", "aus")} {b.SourceSkill} {_data.SkillName(b.SourceSkill)}" : ""),
                    actor: b.Caster, target: b.Target, value: b.SourceSkill);
            foreach (var t in Active(f, "buff"))
                if (t.Buff.Contains(b.Buff) && b.Stack >= t.MinStack && TargetOk(t, f, b.Target))
                    Fire(f, t, b.TimeMs, new Ctx(b.SourceSkill, b.Target, b.Stack, 0));
        }
    }

    private void OnBattle(BattleEvent b)
    {
        if (b.On)
        {
            var f = FightOf(b.Entity) ?? Pull(b.Entity, b.TimeMs);
            if (f is not null) f.BattleOffMs = null;
        }
        else if (FightOf(b.Entity) is { } fight) fight.BattleOffMs = b.TimeMs;
    }

    // ------------------------------------------------------------------ fights

    private Fight? FightOf(uint entity)
    {
        foreach (var f in _fights) if (f.Entity == entity) return f;
        return null;
    }

    private (BossMod Mod, BossDef Def)? BossFor(uint npc)
    {
        if (npc == 0) return null;
        foreach (var m in _mods)
        {
            if (m.Zones.Count > 0 && !m.Zones.Contains(_zone) || Options.DisabledMods.Contains(m.Id)) continue;
            foreach (var d in m.Bosses) if (d.Npc.Contains(npc)) return (m, d);
        }
        if (Options.AllBosses && (Options.InstanceBossesOnly
                ? _data.IsInstanceBoss(npc) || _zone >= 100_000 && _data.IsBoss(npc) // open world maps have 4-digit ids
                : _data.IsBoss(npc)))
            return (AdHoc, new BossDef { Id = npc.ToString(), Name = _data.NpcName(npc) ?? $"Boss {npc}", Npc = { npc } });
        return null;
    }

    private static readonly BossMod AdHoc = new() { Id = "lernmodus", Name = "Lernmodus (ohne Mod)", Source = "intern" };

    private Fight? Pull(uint entity, long time)
    {
        if (!_ents.TryGetValue(entity, out var ent) || BossFor(ent.Npc) is not { } boss) return null;
        if (ent.DeadAt != 0 && time - ent.DeadAt < 60_000) return null; // ticks and despawn packets after the kill
        return StartFight(boss.Mod, boss.Def, entity, ent.Npc, time, "PULL");
    }

    /// <summary>Joined mid-fight (no spawn seen): an unknown entity casting a skill of a mod's boss is that boss.</summary>
    private Fight? Adopt(uint entity, uint skill, long time)
    {
        if (!_ents.TryGetValue(entity, out var ent) || ent.Npc != 0) return null;
        foreach (var m in _mods)
        {
            if (m.Zones.Count > 0 && !m.Zones.Contains(_zone)) continue;
            foreach (var d in m.Bosses)
                if (d.Triggers.Any(t => t.On is "cast" or "hit" && t.Npc.Count == 0 && t.Skill.Contains(skill) && t.Verified)
                    && !_fights.Any(f => f.Def == d))
                {
                    ent.Npc = d.Npc[0];
                    ent.Name = _data.NpcName(d.Npc[0]) ?? d.Name;
                    return StartFight(m, d, entity, ent.Npc, time, L.T("PULL (recognised by its skill)", "PULL (erkannt an Fähigkeit)"));
                }
        }
        return null;
    }

    private Fight StartFight(BossMod mod, BossDef def, uint entity, uint npc, long time, string how)
    {
        var f = new Fight(mod, def, entity, npc, _zone, time);
        _fights.Add(f);
        if (_ents.TryGetValue(entity, out var ent) && ent.HpMax > 0)
        {
            f.HpPct = ent.HpCur > 0 ? Math.Min(100, 100.0 * ent.HpCur / ent.HpMax) : 100;
            // joined while the boss was already hurt: thresholds it has passed stay quiet
            foreach (var t in f.Def.Triggers)
                if (t.On == "hp" && t.Below is { } below && below > f.HpPct + 2) f.Fired.Add(t);
        }
        Record(f, time, "PULL", npc, BossName(f), how == "PULL" ? null : how);
        Alert(L.T($"Pull: {BossName(f)}", $"{BossName(f)}: Kampf beginnt"), "#FFFFFF", time, 3);
        foreach (var t in Active(f, "pull")) Fire(f, t, time, default);
        return f;
    }

    private void Activity(Fight f, long time)
    {
        if (time > f.LastActivityMs) f.LastActivityMs = time;
        f.BattleOffMs = null;
    }

    private void End(Fight f, long time, string outcome, bool reset = false)
    {
        if (!_fights.Remove(f)) return;
        f.EndMs = time;
        f.Outcome = outcome;
        f.Reset = reset;
        f.Bars.Clear();
        _delayed.RemoveAll(d => d.Fight == f);
        Record(f, time, "ENDE", 0, outcome, null);
        if (reset) Alert($"{BossName(f)}: {outcome}", "#C0C0C0", time, 4);
        try { FightEnded?.Invoke(f); }
        catch (IOException) { }
    }

    // ------------------------------------------------------------------ triggers

    private IEnumerable<Trigger> Active(Fight f, string on)
    {
        foreach (var t in f.Def.Triggers)
        {
            if (t.On != on) continue;
            if (t.Phase is { } p && p != f.Phase) continue;
            if (t.Roles.Count > 0 && Options.Role != "alle" &&
                !t.Roles.Contains(Options.Role, StringComparer.OrdinalIgnoreCase)) continue;
            yield return t;
        }
    }

    /// <summary>A trigger handles this skill, or another code with the same name (parts of one skill).</summary>
    private bool Covered(Fight f, uint skill)
    {
        var name = _data.SkillName(skill, "en");
        return f.Def.Triggers.Any(t => t.On is "cast" or "hit" &&
                                       (t.Skill.Contains(skill) || name is not null && t.Skill.Any(s => _data.SkillName(s, "en") == name)));
    }

    private bool TargetOk(Trigger t, Fight f, uint target) => t.Target switch
    {
        "me" => target != 0 && target == _self,
        "notme" => target != _self,
        "player" => target == _self || _ents.GetValueOrDefault(target)?.Player == true,
        "boss" => target == f.Entity,
        _ => true,
    };

    private void Fire(Fight f, Trigger t, long time, Ctx ctx)
    {
        if (t.Once && f.Fired.Contains(t)) return;
        if (f.LastFire.TryGetValue(t, out var last) && time - last < t.Cooldown * 1000) return;
        f.LastFire[t] = time;
        f.Fired.Add(t);
        if (t.Delay > 0)
        {
            _delayed.Add(new Delayed(time + (long)(t.Delay * 1000), t, f, ctx));
            return;
        }
        Run(f, t, time, ctx);
    }

    private void Run(Fight f, Trigger t, long time, Ctx ctx)
    {
        Record(f, time, "TRIGGER", ctx.Skill, t.Name.Length > 0 ? t.Name : t.On, t.Say?.Get());
        if (t.SetPhase is { } phase && phase != f.Phase)
        {
            f.Phase = phase;
            Record(f, time, "PHASE", 0, $"Phase {phase}", null);
        }
        if (t.CancelBar is { Length: > 0 } cancel)
            f.Bars.RemoveAll(b => b.Def.Text.Matches(cancel));
        if (t.Bar is { In: > 0 } bar)
        {
            f.Bars.RemoveAll(b => b.Def == bar || b.Def.Text.Matches(bar.Text.Get()));
            f.Bars.Add(new Bar
            {
                Text = Format(bar.Text.Get(), f, ctx), EndMs = time + (long)(bar.In * 1000), TotalMs = (long)(bar.In * 1000),
                Def = bar,
            });
        }
        var sound = t.Sound ?? (t.Say is not null ? "info" : "none");
        if (sound != "none") _out.Sound(sound);
        if (t.Say?.Get() is { Length: > 0 } say) _out.Say(Format(say, f, ctx), urgent: sound == "alarm");
        var show = (t.Show ?? t.Say)?.Get();
        if (show is { Length: > 0 })
            Alert(Format(show, f, ctx), t.Color ?? (sound == "alarm" ? "#FF5050" : "#FFD24A"), time, t.ShowSeconds);
    }

    private void Unknown(Fight f, CastEvent c, string? name, string? caster)
    {
        // Adds: only named skills on the overlay (their unnamed helper casts come every second); speech only for the boss.
        if (caster is not null && name is null || _data.IsAutoAttack(c.Skill) || GameData.IsGeneric(c.Skill)) return;
        var label = (caster is not null ? caster + ": " : "") + (name ?? $"{L.T("Skill", "Fähigkeit")} {c.Skill}");
        var i = _unknown.FindIndex(u => u.Text == label);
        if (i >= 0)
        {
            if (c.TimeMs - _unknown[i].At < 8_000) goto speech;
            _unknown.RemoveAt(i);
        }
        _unknown.Add((label, c.TimeMs));
        if (_unknown.Count > 4) _unknown.RemoveAt(0);
        speech:
        if (!Options.SayUnknown || caster is not null || name is null) return;
        if (f.LastUnknownSay.TryGetValue(c.Skill, out var last) && c.TimeMs - last < 20_000) return;
        if (c.TimeMs - _lastUnknownSayMs < 2_500) return;
        f.LastUnknownSay[c.Skill] = c.TimeMs;
        _lastUnknownSayMs = c.TimeMs;
        _out.Say(name, urgent: false);
    }

    private string Format(string text, Fight f, Ctx ctx)
    {
        if (!text.Contains('{')) return text;
        return text
            .Replace("{boss}", BossName(f))
            .Replace("{skill}", ctx.Skill != 0 ? _data.SkillName(ctx.Skill) ?? ctx.Skill.ToString() : "")
            .Replace("{target}", ctx.Target == 0 ? "" : ctx.Target == _self ? L.T("you", "dich") : TargetName(ctx.Target) ?? L.T("someone", "jemanden"))
            .Replace("{stack}", ctx.Stack.ToString())
            .Replace("{hp}", double.IsNaN(f.HpPct) ? "?" : ((int)f.HpPct).ToString())
            .Replace("{npc}", ctx.Npc != 0 ? _data.NpcName(ctx.Npc) ?? ctx.Npc.ToString() : "")
            .Replace("{phase}", f.Phase.ToString());
    }

    private string? TargetName(uint target)
    {
        if (target == 0) return null;
        if (target == _self) return L.T("you", "du");
        return _ents.TryGetValue(target, out var e) ? Label(e) : null;
    }

    /// <summary>Player name, or the NPC's name in the current language.</summary>
    private string? Label(Ent e) =>
        e.Player ? e.Name : e.Npc != 0 ? _data.NpcName(e.Npc) ?? e.Name ?? $"NPC {e.Npc}" : e.Name;

    /// <summary>The boss name in the current language (game data), else the mod's name.</summary>
    private string BossName(Fight f) => _data.NpcName(f.Npc) ?? f.Def.Name;

    private static bool IsNpcSkill(uint skill) => skill is >= 1_000_000 and <= 9_999_999 and not (>= 3_000_000 and <= 3_099_999);

    private void Alert(string text, string color, long time, double seconds)
    {
        _alerts.Add((text, color, time, seconds));
        if (_alerts.Count > 3) _alerts.RemoveAt(0);
    }

    private void Record(Fight f, long time, string type, uint code, string? name, string? detail, uint actor = 0,
        uint target = 0, string? caster = null, double value = double.NaN, bool known = false)
    {
        var kind = "";
        uint targetNpc = 0;
        if (target != 0)
        {
            if (target == _self) kind = "me";
            else if (_ents.TryGetValue(target, out var e))
            {
                if (e.Player) kind = "player";
                else if (e.Npc != 0)
                {
                    kind = "npc";
                    targetNpc = e.Npc;
                }
            }
        }
        f.Records.Add(new FightRecord(time, type, code, name, detail, f.HpPct, actor, target, caster, value, known, kind,
            TargetName(target), targetNpc));
    }

    /// <summary>
    /// The user marks the attack that just happened as important (hotkey): the last NPC hit on us within 2 s, else the
    /// last boss cast within 4 s. The analysis gives marked attacks priority for countdown timers.
    /// </summary>
    public string? Mark(long now)
    {
        lock (_gate)
        {
            if (now > _now) _now = now;
            string? marked = null;
            foreach (var f in _fights)
            {
                uint code = 0;
                long at = 0;
                if (f.LastHitOnMe is { } hit && _now - hit.At <= 2_000) (code, at) = hit;
                else
                    for (var i = f.Records.Count - 1; i >= 0; i--)
                    {
                        var r = f.Records[i];
                        if (_now - r.TimeMs > 4_000) break;
                        if (r.Type == "CAST" && r.Caster is null)
                        {
                            (code, at) = (r.Code, r.TimeMs);
                            break;
                        }
                    }
                if (code == 0) continue;
                marked = _data.SkillName(code) ?? $"{L.T("Skill", "Fähigkeit")} {code}";
                Record(f, _now, "MARK", code, _data.SkillName(code), null, value: (_now - at) / 1000.0);
            }
            if (marked is null)
            {
                Alert(L.T(_fights.Count == 0 ? "No boss fight to mark" : "Nothing to mark in the last seconds",
                    _fights.Count == 0 ? "Kein Boss-Kampf zum Markieren" : "Nichts zum Markieren in den letzten Sekunden"),
                    "#C0C0C0", _now, 2.5);
                return null;
            }
            _out.Sound("info");
            Alert(L.T($"Marked: {marked}", $"Markiert: {marked}"), "#7CFC7C", _now, 2.5);
            return marked;
        }
    }

    // ------------------------------------------------------------------ clock

    /// <summary>Advances bars, delayed callouts and reset detection. <paramref name="now"/> uses the packet clock.</summary>
    public void Tick(long now)
    {
        lock (_gate)
        {
            if (now > _now) _now = now;
            now = _now;
            for (var i = 0; i < _delayed.Count; i++)
            {
                var d = _delayed[i];
                if (d.DueMs > now) continue;
                _delayed.RemoveAt(i--);
                if (_fights.Contains(d.Fight)) Run(d.Fight, d.Trigger, d.DueMs, d.Ctx);
            }
            foreach (var f in _fights.ToList())
            {
                if (now - f.LastActivityMs > f.Def.ResetAfter * 1000 ||
                    f.BattleOffMs is { } off && now - off > 8_000 && now - f.LastActivityMs > 8_000)
                {
                    End(f, now, L.T("reset (no more combat)", "Reset (kein Kampf mehr)"), reset: true);
                    continue;
                }
                for (var i = 0; i < f.Bars.Count; i++)
                {
                    var b = f.Bars[i];
                    var left = b.EndMs - now;
                    if (!b.Warned && b.Def.Warn > 0 && left <= b.Def.Warn * 1000)
                    {
                        b.Warned = true;
                        var say = b.Def.WarnSay?.Get() ?? "{text} in {warn}";
                        if (say.Length > 0)
                            _out.Say(say.Replace("{text}", b.Text).Replace("{warn}", ((int)Math.Round(b.Def.Warn)).ToString()), false);
                    }
                    if (left <= 0)
                    {
                        if (b.Def.EndSay?.Get() is { Length: > 0 } end) _out.Say(end.Replace("{text}", b.Text), urgent: true);
                        f.Bars.RemoveAt(i--);
                    }
                }
            }
            _alerts.RemoveAll(a => now - a.At > a.Seconds * 1000);
            _unknown.RemoveAll(u => now - u.At > 10_000);
        }
    }

    /// <summary>Test callout from the tray / hotkey.</summary>
    public void Test(long now)
    {
        lock (_gate)
        {
            if (now > _now) _now = now;
            _out.Sound("alarm");
            _out.Say(L.T("Test: raid announcer is ready", "Test: Raid-Ansager ist bereit"), urgent: true);
            Alert(L.T("TEST – raid announcer ready", "TEST – Raid-Ansager bereit"), "#FF5050", _now, 4);
        }
    }

    public OverlayState Snapshot()
    {
        lock (_gate)
        {
            var now = _now;
            return new OverlayState(
                _fights.Select(f => new BossView(BossName(f), f.HpPct, f.Phase, (now - f.PullMs) / 1000.0)).ToList(),
                _alerts.Select(a => new AlertView(a.Text, a.Color, (now - a.At) / 1000.0, a.Seconds)).ToList(),
                _fights.SelectMany(f => f.Bars).OrderBy(b => b.EndMs)
                    .Select(b => new BarView(b.Text, Math.Max(0, (b.EndMs - now) / 1000.0), b.TotalMs / 1000.0,
                        b.EndMs - now <= b.Def.Warn * 1000)).ToList(),
                _unknown.Select(u => u.Text).ToList(),
                _zone, ModZone(_zone));
        }
    }

    /// <summary>Ends every running fight (shutdown / end of a replay) so the logs get written.</summary>
    public void Flush(string outcome)
    {
        lock (_gate)
            foreach (var f in _fights.ToList()) End(f, _now, outcome);
    }
}
