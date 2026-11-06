using System.Buffers.Binary;
using CubeRadar.Core.Protocol;

namespace RaidAnsager.Protocol;

// Decoded game events the announcer needs. Layouts: HamMeter docs/protocol.md, TheLoop705/aion2dps PacketDecoder, and
// our own check on the dungeon recording of 2026-11-05 (boss casts arrive as 02 38 with the boss as actor and a
// 7-digit NPC skill code, e.g. Vollendete Berk 1800060 "Drana-Welle").

public abstract record GameEvent(long TimeMs);

/// <summary><c>01 40</c>: the user entered a zone (open world 1010, dungeons/raids 6xxxxx; Ludra = 600082).</summary>
public sealed record ZoneEvent(long TimeMs, uint Zone) : GameEvent(TimeMs);

/// <summary><c>4A 36</c> / <c>33 36</c>: this entity is the user.</summary>
public sealed record SelfEvent(long TimeMs, uint Entity) : GameEvent(TimeMs);

/// <summary><c>33 36</c> / <c>45 36</c>: a player's name.</summary>
public sealed record PlayerEvent(long TimeMs, uint Entity, string Name) : GameEvent(TimeMs);

/// <summary><c>41 36</c>: an NPC, summon or effect appeared. <see cref="NpcCode"/> 0 = head not recognised.</summary>
public sealed record SpawnEvent(long TimeMs, uint Entity, byte Kind, uint NpcCode, long HpCur, long HpMax) : GameEvent(TimeMs);

/// <summary><c>02 38</c>: skill use announcement (start of a cast).</summary>
public sealed record CastEvent(long TimeMs, uint Actor, uint Skill, uint Target) : GameEvent(TimeMs);

/// <summary><c>04 38</c> direct hit / heal or <c>05 38</c> periodic tick.</summary>
public sealed record HitEvent(long TimeMs, uint Actor, uint Target, uint Skill, bool Periodic) : GameEvent(TimeMs);

/// <summary><c>00 8D</c>: remaining HP of an entity (bosses).</summary>
public sealed record HpEvent(long TimeMs, uint Entity, long Hp) : GameEvent(TimeMs);

/// <summary><c>04 8D</c>: an entity died (killing blow).</summary>
public sealed record KillEvent(long TimeMs, uint Entity, uint Killer) : GameEvent(TimeMs);

/// <summary><c>42 36</c>: entity state, flag 1 loaded dead / removed, 3 died in combat, 7 gone from the world.</summary>
public sealed record GoneEvent(long TimeMs, uint Entity, uint Flag) : GameEvent(TimeMs);

/// <summary><c>2A 38</c> / <c>2B 38</c>: buff or debuff applied.</summary>
public sealed record BuffEvent(long TimeMs, uint Target, uint Buff, uint Stack, long DurationMs, uint Caster, uint SourceSkill)
    : GameEvent(TimeMs);

/// <summary><c>0E 92</c>: buff removed.</summary>
public sealed record BuffGoneEvent(long TimeMs, uint Target, uint Buff) : GameEvent(TimeMs);

/// <summary><c>21 8D</c>: an NPC entered (true) or left (false) combat.</summary>
public sealed record BattleEvent(long TimeMs, uint Entity, bool On) : GameEvent(TimeMs);

public static class Op
{
    public const ushort Zone = 0x0140;
    public const ushort UserState = 0x4A36;
    public const ushort SelfInfo = 0x3336;
    public const ushort PlayerInfo = 0x4536;
    public const ushort Spawn = 0x4136;
    public const ushort Gone = 0x4236;
    public const ushort Cast = 0x0238;
    public const ushort Hit = 0x0438;
    public const ushort Dot = 0x0538;
    public const ushort RemainHp = 0x008D;
    public const ushort Kill = 0x048D;
    public const ushort Battle = 0x218D;
    public const ushort Buff = 0x2A38;
    public const ushort Buff2 = 0x2B38;
    public const ushort BuffGone = 0x0E92;
}

/// <summary>Turns game frames into <see cref="GameEvent"/>s. Unknown opcodes and malformed bodies are ignored.</summary>
public static class CombatDecoder
{
    private const uint MaxEntity = 9_999_999;

    public static void Decode(ushort op, ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        try
        {
            switch (op)
            {
                case Op.Cast: Cast(b, t, output); break;
                case Op.Hit: Hit(b, t, output); break;
                case Op.Dot: Dot(b, t, output); break;
                case Op.Spawn: Spawn(b, t, output); break;
                case Op.RemainHp: RemainHp(b, t, output); break;
                case Op.Kill: Kill(b, t, output); break;
                case Op.Gone: Gone(b, t, output); break;
                case Op.Buff: Buff(b, t, output, multi: true); break;
                case Op.Buff2: Buff(b, t, output, multi: false); break;
                case Op.BuffGone: BuffGone(b, t, output); break;
                case Op.Battle: Battle(b, t, output); break;
                case Op.Zone:
                    if (b.Length >= 4) output.Add(new ZoneEvent(t, BinaryPrimitives.ReadUInt32LittleEndian(b)));
                    break;
                case Op.UserState:
                {
                    var o = 0;
                    if (Wire.TryVarint(b, ref o, out var id) && id is > 0 and <= MaxEntity) output.Add(new SelfEvent(t, id));
                    break;
                }
                case Op.SelfInfo:
                case Op.PlayerInfo:
                    if (PlayerMessages.TryParseIdentity(b, op == Op.SelfInfo, out var who))
                    {
                        if (op == Op.SelfInfo) output.Add(new SelfEvent(t, who.EntityId));
                        output.Add(new PlayerEvent(t, who.EntityId, who.Name));
                    }
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // a body shorter than its layout: ignore, like every other malformed frame
        }
    }

    /// <summary>actor varint, varint, skill u32, u8, kind u8, target varint, [kind 2: heading, x y z]</summary>
    private static void Cast(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var actor) || !Wire.TryVarint(b, ref o, out _) ||
            !Wire.TryU32(b, ref o, out var skill) || !Wire.TryU8(b, ref o, out _) || !Wire.TryU8(b, ref o, out _) ||
            !Wire.TryVarint(b, ref o, out var target)) return;
        if (actor == 0 || actor > MaxEntity || skill == 0 || skill > 299_999_999) return;
        output.Add(new CastEvent(t, actor, skill, target));
    }

    /// <summary>target varint, layout varint (≤ 255, low nibble 0 or 4..7), varint, actor varint, skill u32, ...</summary>
    private static void Hit(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var target) || !Wire.TryVarint(b, ref o, out var layout) ||
            !Wire.TryVarint(b, ref o, out _) || !Wire.TryVarint(b, ref o, out var actor) ||
            !Wire.TryU32(b, ref o, out var skill)) return;
        var nibble = layout & 0x0F;
        if (layout > 255 || nibble is not (0 or 4 or 5 or 6 or 7)) return;
        if (actor == 0 || actor > MaxEntity || target > MaxEntity || skill == 0 || skill > 299_999_999) return;
        output.Add(new HitEvent(t, actor, target, skill, Periodic: false));
    }

    /// <summary>target varint, effect u8, actor varint, varint, skill × 100 u32, amount varint</summary>
    private static void Dot(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var target) || !Wire.TryU8(b, ref o, out var effect) ||
            !Wire.TryVarint(b, ref o, out var actor) || !Wire.TryVarint(b, ref o, out _) ||
            !Wire.TryU32(b, ref o, out var raw)) return;
        if (effect is not (0x02 or 0x0A) || actor == 0 || actor > MaxEntity || raw < 100) return; // damage ticks only
        output.Add(new HitEvent(t, actor, target, raw / 100, Periodic: true));
    }

    /// <summary>entity varint, kind u8, flags u8, head at +2 (fallback +4): [name], npc u32, m1, m2, x y z heading,
    /// [12 bytes if m1 &amp; 08], 2 bytes, 01, hp varint, hp max varint</summary>
    private static void Spawn(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var entity) || o + 2 > b.Length) return;
        var kind = b[o];
        uint npc = 0;
        long cur = 0, max = 0;
        if (!Head(b, o + 2, ref npc, ref cur, ref max)) Head(b, o + 4, ref npc, ref cur, ref max);
        output.Add(new SpawnEvent(t, entity, kind, npc, cur, max));
    }

    private static bool Head(ReadOnlySpan<byte> b, int o, ref uint npc, ref long cur, ref long max)
    {
        if (!Wire.TryU8(b, ref o, out var gate)) return false;
        if ((gate & 1) != 0)
        {
            if (!Wire.TryU8(b, ref o, out var len) || len == 0 || o + len > b.Length) return false;
            o += len;
        }
        if (!Wire.TryU32(b, ref o, out var code) || code > 0x00FF_FFFF) return false;
        if (!Wire.TryU8(b, ref o, out var m1) || (m1 & ~0x48) != 0) return false;
        if (!Wire.TryU8(b, ref o, out var m2) || m2 is not (0x02 or 0x03)) return false;
        npc = code;
        o += 16; // x y z heading
        if ((m1 & 0x08) != 0) o += 12;
        o += 2;
        if (o >= b.Length || b[o] != 0x01) return true;
        o++;
        if (Wire.TryVarint(b, ref o, out var c) && Wire.TryVarint(b, ref o, out var m))
        {
            cur = c;
            max = m;
        }
        return true;
    }

    /// <summary>entity varint, 3 × varint, hp u64</summary>
    private static void RemainHp(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var entity) || !Wire.TryVarint(b, ref o, out _) ||
            !Wire.TryVarint(b, ref o, out _) || !Wire.TryVarint(b, ref o, out _) || o + 8 > b.Length) return;
        var hp = BinaryPrimitives.ReadInt64LittleEndian(b[o..]);
        if (entity == 0 || entity > MaxEntity || hp < 0 || hp > 1_000_000_000_000L) return;
        output.Add(new HpEvent(t, entity, hp));
    }

    /// <summary>entity varint, skill u32, killer varint, ...</summary>
    private static void Kill(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var entity) || !Wire.TryU32(b, ref o, out _)) return;
        Wire.TryVarint(b, ref o, out var killer);
        if (entity == 0 || entity > MaxEntity) return;
        output.Add(new KillEvent(t, entity, killer));
    }

    /// <summary>entity varint, varint, flag varint</summary>
    private static void Gone(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var entity) || !Wire.TryVarint(b, ref o, out _) ||
            !Wire.TryVarint(b, ref o, out var flag)) return;
        output.Add(new GoneEvent(t, entity, flag));
    }

    /// <summary>entity varint, varint, toggle varint (1 = combat, 0 = out of combat); ends exactly</summary>
    private static void Battle(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var entity) || !Wire.TryVarint(b, ref o, out _) ||
            !Wire.TryVarint(b, ref o, out var toggle) || o != b.Length || toggle > 1) return;
        output.Add(new BattleEvent(t, entity, toggle == 1));
    }

    /// <summary><c>2A 38</c>: target varint, count u8, count × entry; <c>2B 38</c>: target varint, one entry.
    /// entry = flags u8, stack varint, buff u32, duration u64, expiry u64, caster varint, level u8,
    /// [source skill u32 if flags &amp; 02], u8, 3 × f32</summary>
    private static void Buff(ReadOnlySpan<byte> b, long t, List<GameEvent> output, bool multi)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var target)) return;
        int count = 1;
        if (multi)
        {
            if (!Wire.TryU8(b, ref o, out var n) || n is < 1 or > 16) return;
            count = n;
        }
        var found = new List<BuffEvent>(count);
        for (var k = 0; k < count; k++)
        {
            if (!Wire.TryU8(b, ref o, out var flags) || !Wire.TryVarint(b, ref o, out var stack) ||
                !Wire.TryU32(b, ref o, out var buff) || o + 16 > b.Length) return;
            var duration = BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
            o += 16; // duration, expiry
            if (!Wire.TryVarint(b, ref o, out var caster) || !Wire.TryU8(b, ref o, out _)) return;
            uint source = 0;
            if ((flags & 0x02) != 0 && !Wire.TryU32(b, ref o, out source)) return;
            o += 13;
            if (o > b.Length) return;
            found.Add(new BuffEvent(t, target, buff, stack, duration >= int.MaxValue ? -1 : (long)duration, caster, source));
        }
        if (o != b.Length) return;
        output.AddRange(found);
    }

    /// <summary>target varint, count u8, count × buff u32</summary>
    private static void BuffGone(ReadOnlySpan<byte> b, long t, List<GameEvent> output)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var target) || !Wire.TryU8(b, ref o, out var n) || b.Length - o != n * 4) return;
        for (var k = 0; k < n; k++) output.Add(new BuffGoneEvent(t, target, Wire.U32(b, o + k * 4)));
    }
}
