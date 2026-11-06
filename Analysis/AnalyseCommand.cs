using System.Diagnostics;
using System.Text;
using CubeRadar.Core.Capture;
using RaidAnsager.App;
using RaidAnsager.Engine;

namespace RaidAnsager.Analysis;

/// <summary>
/// <c>RaidAnsager analyse &lt;pcapng …&gt; [--out dir]</c>: replays recordings without sound, treats every boss of the
/// game data as a fight (plus the mod bosses), and writes one log per pull, a summary and a draft mod.
/// </summary>
public static class AnalyseCommand
{
    public static int Run(IReadOnlyList<string> files, string? outDir, TextWriter con)
    {
        if (files.Count == 0)
        {
            con.WriteLine("Aufruf: RaidAnsager analyse <aufnahme.pcapng> [weitere …] [--out <ordner>]");
            return 2;
        }
        if (!LiveSniffer.IsNpcapInstalled())
        {
            con.WriteLine("Npcap fehlt (npcap.com): ohne Npcap lassen sich keine pcapng-Dateien lesen.");
            return 3;
        }
        outDir ??= Path.Combine(AppPaths.Fights, "analyse_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(outDir);

        var data = GameData.Load();
        var problems = new List<string>();
        var mods = ModLoader.LoadAll(AppPaths.ModFolders, problems);
        foreach (var p in problems) con.WriteLine("Mod-Hinweis: " + p);

        var engine = new Announcer(data, mods, new NullOutput(), new AnnouncerOptions { AllBosses = true, SayUnknown = false });
        var fights = new List<Fight>();
        var zones = new List<(long Ms, uint Zone)>();
        engine.FightEnded += f => fights.Add(f);
        var watch = Stopwatch.StartNew();
        using (var pipe = new Pipeline(engine, tickWithPackets: true))
        {
            foreach (var file in files)
            {
                con.WriteLine($"lese {file} …");
                uint lastZone = 0;
                CaptureFileSource.ReadAll(file, raw =>
                {
                    pipe.OnPacket(raw, "datei");
                    var z = engine.Zone;
                    if (z != lastZone)
                    {
                        zones.Add((pipe.LastPacketMs, z));
                        lastZone = z;
                    }
                });
                engine.Flush("Ende der Aufnahme");
            }
            con.WriteLine($"{pipe.Packets:N0} Pakete in {watch.Elapsed.TotalSeconds:0.0} s");
        }

        var real = fights.Where(f => (f.EndMs - f.PullMs) >= 10_000).ToList();
        var writer = new FightLogWriter(outDir, data);
        foreach (var f in real) writer.Write(f);

        var sb = new StringBuilder();
        sb.AppendLine($"Raid-Ansager – Analyse von {files.Count} Aufnahme(n), {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"Zonen: {string.Join(", ", zones.Select(z => z.Zone).Distinct())}");
        sb.AppendLine($"Boss-Kämpfe ≥ 10 s: {real.Count} (verworfen: {fights.Count - real.Count} kurze)");
        sb.AppendLine();
        foreach (var g in real.GroupBy(f => (f.Zone, f.Def.Id)))
        {
            var list = g.ToList();
            var def = list[0].Def;
            sb.AppendLine($"=== {def.Name} (NPC {string.Join("/", def.Npc)}), Zone {g.Key.Zone}: {list.Count} Pull(s)");
            foreach (var f in list)
                sb.AppendLine($"    {DateTimeOffset.FromUnixTimeMilliseconds(f.PullMs).ToLocalTime():dd.MM. HH:mm:ss}  " +
                              $"{FightReport.Clock((f.EndMs - f.PullMs) / 1000.0),8}  {f.Outcome}");
            sb.AppendLine(FightReport.SkillTable(FightReport.Skills(list, data)));
        }
        foreach (var zone in real.GroupBy(f => f.Zone))
        {
            var draft = FightReport.Draft(zone.ToList(), data);
            File.WriteAllText(Path.Combine(outDir, $"entwurf_zone{zone.Key}.json"), FightReport.DraftJson(draft), new UTF8Encoding(false));
        }
        var summary = Path.Combine(outDir, "zusammenfassung.txt");
        File.WriteAllText(summary, sb.ToString(), new UTF8Encoding(true));
        con.WriteLine(sb.ToString());
        con.WriteLine($"Protokolle und Entwürfe: {outDir}");
        return 0;
    }
}
