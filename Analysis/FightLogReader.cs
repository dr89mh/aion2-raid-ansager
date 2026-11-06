using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using RaidAnsager.Engine;

namespace RaidAnsager.Analysis;

/// <summary>
/// Reads fight logs (.jsonl) back into <see cref="Fight"/>s: single files, folders (recursive) and the .zip exports
/// members send in. Understands version 2 (header line) and the headerless version 1 of 0.1/0.2.
/// </summary>
public static class FightLogReader
{
    public static bool IsLogInput(string path) =>
        Directory.Exists(path) || path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public static List<Fight> Read(IEnumerable<string> paths, GameData data, List<string> problems)
    {
        var fights = new List<Fight>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var f in Directory.EnumerateFiles(path, "*.jsonl", SearchOption.AllDirectories)) ReadFile(f);
                foreach (var z in Directory.EnumerateFiles(path, "*.zip", SearchOption.AllDirectories)) ReadZip(z);
            }
            else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ReadZip(path);
            else ReadFile(path);
        }
        return fights;

        void ReadFile(string file)
        {
            try
            {
                using var r = new StreamReader(file);
                Add(Parse(Path.GetFileName(file), r, data), file);
            }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException)
            {
                problems.Add($"{file}: {ex.Message}");
            }
        }

        void ReadZip(string zip)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zip);
                foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        using var r = new StreamReader(entry.Open());
                        Add(Parse(entry.Name, r, data), $"{Path.GetFileName(zip)}/{entry.FullName}");
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or FormatException or InvalidDataException)
                    {
                        problems.Add($"{Path.GetFileName(zip)}/{entry.FullName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                problems.Add($"{zip}: {ex.Message}");
            }
        }

        void Add(Fight? f, string source)
        {
            if (f is null) problems.Add($"{source}: kein Kampf erkannt");
            else fights.Add(f);
        }
    }

    private static Fight? Parse(string fileName, TextReader reader, GameData data)
    {
        uint npc = 0, zone = 0, entity = 0;
        long start = 0;
        double duration = 0;
        string? boss = null, outcome = null;
        bool won = false, reset = false, header = false;
        var lines = new List<JsonElement>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement.Clone();
            if (Str(e, "type") == "FIGHT")
            {
                header = true;
                npc = U32(e, "npc");
                entity = U32(e, "entity");
                zone = U32(e, "zone");
                start = e.TryGetProperty("start", out var s) ? s.GetInt64() : 0;
                duration = Num(e, "duration");
                boss = Str(e, "boss");
                outcome = Str(e, "outcome");
                won = e.TryGetProperty("won", out var w) && w.ValueKind == JsonValueKind.True;
                reset = e.TryGetProperty("reset", out var rs) && rs.ValueKind == JsonValueKind.True;
                continue;
            }
            lines.Add(e);
        }
        if (lines.Count == 0) return null;
        if (!header)
        {
            // version 1: boss from the PULL record, start time from the file name (local time)
            var pull = lines.FirstOrDefault(e => Str(e, "type") == "PULL");
            if (pull.ValueKind == JsonValueKind.Undefined) return null;
            npc = U32(pull, "code");
            boss = Str(pull, "name");
            if (DateTime.TryParseExact(fileName.Length >= 15 ? fileName[..15] : fileName, "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
                start = new DateTimeOffset(local).ToUnixTimeMilliseconds();
            duration = Num(lines[^1], "t");
            var end = lines.LastOrDefault(e => Str(e, "type") == "ENDE");
            outcome = end.ValueKind == JsonValueKind.Undefined ? null : Str(end, "name");
            won = outcome is "Sieg" or "kill";
            reset = outcome?.StartsWith("Reset", StringComparison.OrdinalIgnoreCase) == true;
        }
        if (npc == 0) return null;
        if (entity == 0)
        {
            // version 1: the boss entity is the actor of its own casts
            var cast = lines.FirstOrDefault(e => Str(e, "type") == "CAST" && Str(e, "caster") is null);
            if (cast.ValueKind != JsonValueKind.Undefined) entity = U32(cast, "actor");
        }
        var name = data.NpcName(npc) ?? boss ?? $"Boss {npc}";
        var def = new BossDef { Id = npc.ToString(CultureInfo.InvariantCulture), Name = name, Npc = { npc } };
        var fight = new Fight(Imported, def, entity, npc, zone, start)
        {
            EndMs = start + (long)(duration * 1000), Outcome = outcome ?? "", Won = won, Reset = reset,
        };
        foreach (var e in lines)
        {
            var t = start + (long)(Num(e, "t") * 1000);
            var hp = e.TryGetProperty("hp", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : double.NaN;
            var value = e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
            fight.Records.Add(new FightRecord(t, Str(e, "type") ?? "", U32(e, "code"), Str(e, "name"), Str(e, "detail"), hp,
                U32(e, "actor"), U32(e, "target"), Str(e, "caster"), value,
                e.TryGetProperty("known", out var k) && k.ValueKind == JsonValueKind.True,
                Str(e, "to") ?? "", null, U32(e, "toNpc")));
        }
        return fight;
    }

    private static readonly BossMod Imported = new() { Id = "import", Name = "Import", Source = "intern" };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static uint U32(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetUInt32(out var v) ? v : 0;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;

    /// <summary>Members of the same group log the same pull: one copy per boss, entity (the server assigns it per
    /// instance, so it is the same for the whole group) and start time (±15 s) is kept, the one with the most records.
    /// Several bosses of the same kind at the same time differ in their entity and are all kept.</summary>
    public static List<Fight> Dedupe(IEnumerable<Fight> fights)
    {
        var kept = new List<Fight>();
        foreach (var f in fights.OrderByDescending(f => f.Records.Count))
            if (!kept.Any(k => k.Npc == f.Npc && (k.Entity == 0 || f.Entity == 0 || k.Entity == f.Entity) &&
                               Math.Abs(k.PullMs - f.PullMs) <= 15_000))
                kept.Add(f);
        return kept.OrderBy(f => f.PullMs).ToList();
    }
}
