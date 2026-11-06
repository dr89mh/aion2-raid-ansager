using System.IO.Compression;
using System.Text.Json.Nodes;

namespace RaidAnsager.App;

/// <summary>
/// Packs fight logs (.jsonl) into a zip, for the upload or for sending by hand. Version 2 logs contain no player names;
/// older ones are cleaned on the way (target names and the names of the dead removed).
/// </summary>
public static class LogExport
{
    /// <summary>Every fight log of the live program (replays excluded), oldest first.</summary>
    public static List<string> LogFiles()
    {
        var dir = AppPaths.Fights;
        if (!Directory.Exists(dir)) return [];
        var replay = Path.DirectorySeparatorChar + "replay" + Path.DirectorySeparatorChar;
        var analysis = Path.DirectorySeparatorChar + "analyse_";
        return Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories)
            .Where(f => !f.Contains(replay, StringComparison.OrdinalIgnoreCase) && !f.Contains(analysis, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>Zip on the desktop with every fight log.</summary>
    public static (string Path, int Files) Create()
    {
        var files = LogFiles();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var target = Path.Combine(desktop.Length > 0 ? desktop : AppPaths.Root, $"RaidAnsager-logs-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        if (files.Count == 0) return (target, 0);
        if (File.Exists(target)) File.Delete(target); // a second export in the same minute
        using (var f = File.Create(target)) Write(files, f);
        return (target, files.Count);
    }

    public static void Write(IEnumerable<string> files, Stream output)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in files)
        {
            var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.SmallestSize);
            using var w = new StreamWriter(entry.Open());
            var v2 = false;
            foreach (var line in File.ReadLines(file))
            {
                if (line.Length == 0) continue;
                if (line.Contains("\"type\":\"FIGHT\"", StringComparison.Ordinal)) v2 = true;
                w.WriteLine(v2 ? line : Clean(line));
            }
        }
    }

    /// <summary>Version 1 lines: the detail of casts, hits, deaths and buffs could hold player names.</summary>
    private static string Clean(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return line;
            var type = o["type"]?.GetValue<string>();
            if (type is "CAST" or "HIT" or "TOD") o.Remove("detail");
            if (type == "TOD") o.Remove("name");
            if (type == "BUFF" && o["detail"]?.GetValue<string>() is { } d && d.IndexOf(" | ", StringComparison.Ordinal) is >= 0 and var i)
                o["detail"] = d[(i + 3)..];
            return o.ToJsonString();
        }
        catch (System.Text.Json.JsonException)
        {
            return "";
        }
    }
}
