using System.Diagnostics;
using System.Text;
using CubeRadar.Core.Capture;
using RaidAnsager.App;
using RaidAnsager.Engine;

namespace RaidAnsager.Analysis;

/// <summary>
/// <c>RaidAnsager analyse &lt;inputs …&gt; [--out dir]</c>. Inputs: recordings (.pcapng/.pcap, replayed without sound,
/// every boss of the game data counts) and fight logs (.jsonl, folders, .zip exports from members). Writes a summary,
/// the predictions for countdowns (vorhersagen.txt) and a timer mod (dungeon-timers.json); for recordings also one log
/// per pull and draft mods.
/// </summary>
public static class AnalyseCommand
{
    public static int Run(IReadOnlyList<string> inputs, string? outDir, TextWriter con)
    {
        if (inputs.Count == 0)
        {
            con.WriteLine(L.T("Usage: RaidAnsager analyse <recording.pcapng | log.jsonl | folder | export.zip> […] [--out <folder>]",
                "Aufruf: RaidAnsager analyse <aufnahme.pcapng | protokoll.jsonl | ordner | export.zip> […] [--out <ordner>]"));
            return 2;
        }
        var captures = inputs.Where(i => !FightLogReader.IsLogInput(i)).ToList();
        var logs = inputs.Where(FightLogReader.IsLogInput).ToList();
        if (captures.Count > 0 && !LiveSniffer.IsNpcapInstalled())
        {
            con.WriteLine(L.T("Npcap is missing (npcap.com): recordings cannot be read without it.",
                "Npcap fehlt (npcap.com): ohne Npcap lassen sich keine pcapng-Dateien lesen."));
            return 3;
        }
        outDir ??= Path.Combine(AppPaths.Fights, "analyse_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(outDir);

        var data = GameData.Load();
        var problems = new List<string>();
        var mods = ModLoader.LoadAll(AppPaths.ModFolders, problems);
        foreach (var p in problems) con.WriteLine("Mod: " + p);

        // recordings
        var recorded = new List<Fight>();
        var zones = new List<uint>();
        if (captures.Count > 0)
        {
            var engine = new Announcer(data, mods, new NullOutput(), new AnnouncerOptions { AllBosses = true, SayUnknown = false });
            engine.FightEnded += f => recorded.Add(f);
            var watch = Stopwatch.StartNew();
            using var pipe = new Pipeline(engine, tickWithPackets: true);
            foreach (var file in captures)
            {
                con.WriteLine($"{L.T("reading", "lese")} {file} …");
                uint lastZone = 0;
                CaptureFileSource.ReadAll(file, raw =>
                {
                    pipe.OnPacket(raw, "file");
                    if (engine.Zone != lastZone) zones.Add(lastZone = engine.Zone);
                });
                engine.Flush(L.T("end of recording", "Ende der Aufnahme"));
            }
            con.WriteLine(L.T($"{pipe.Packets:N0} packets in {watch.Elapsed.TotalSeconds:0.0} s", $"{pipe.Packets:N0} Pakete in {watch.Elapsed.TotalSeconds:0.0} s"));
        }
        recorded = recorded.Where(f => (f.EndMs - f.PullMs) >= 10_000).ToList();
        var writer = new FightLogWriter(outDir, data);
        foreach (var f in recorded) writer.Write(f);

        // fight logs of members
        var readProblems = new List<string>();
        var imported = logs.Count == 0 ? [] : FightLogReader.Read(logs, data, readProblems);
        foreach (var p in readProblems.Take(20)) con.WriteLine(L.T("Log: ", "Protokoll: ") + p);
        var all = FightLogReader.Dedupe(recorded.Concat(imported.Where(f => (f.EndMs - f.PullMs) >= 10_000)));
        var duplicates = recorded.Count + imported.Count(f => (f.EndMs - f.PullMs) >= 10_000) - all.Count;

        var sb = new StringBuilder();
        sb.AppendLine(L.T($"Raid Announcer – analysis, {DateTime.Now:yyyy-MM-dd HH:mm}", $"Raid-Ansager – Analyse, {DateTime.Now:dd.MM.yyyy HH:mm}"));
        sb.AppendLine(L.T($"Inputs: {captures.Count} recording(s), {logs.Count} log input(s) with {imported.Count} pull(s)",
            $"Eingaben: {captures.Count} Aufnahme(n), {logs.Count} Protokoll-Eingabe(n) mit {imported.Count} Pull(s)"));
        if (zones.Count > 0) sb.AppendLine($"{L.T("Zones", "Zonen")}: {string.Join(", ", zones.Distinct())}");
        sb.AppendLine(L.T($"Boss fights ≥ 10 s: {all.Count} (the same pull from several members counted once: {duplicates} duplicates)",
            $"Boss-Kämpfe ≥ 10 s: {all.Count} (derselbe Pull von mehreren Mitgliedern zählt einmal: {duplicates} doppelt)"));
        sb.AppendLine();
        foreach (var g in all.GroupBy(f => f.Npc).OrderByDescending(g => g.Count()))
        {
            var list = g.ToList();
            sb.AppendLine($"=== {FightReport.Boss(list[0], data)} (NPC {g.Key}): {list.Count} Pull(s), " +
                          $"{list.Count(f => f.Won)} {L.T("kill(s)", "Sieg(e)")}");
            sb.AppendLine(FightReport.SkillTable(FightReport.Skills(list, data)));
        }
        if (recorded.Count > 0)
            foreach (var zone in recorded.GroupBy(f => f.Zone))
                File.WriteAllText(Path.Combine(outDir, $"entwurf_zone{zone.Key}.json"),
                    FightReport.DraftJson(FightReport.Draft(zone.ToList(), data)), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "zusammenfassung.txt"), sb.ToString(), new UTF8Encoding(true));

        // countdown predictions
        var predictions = Predictor.Run(all, data);
        File.WriteAllText(Path.Combine(outDir, "vorhersagen.txt"), Predictor.Report(predictions, data), new UTF8Encoding(true));
        var timerMod = Predictor.Mod(predictions, data);
        File.WriteAllText(Path.Combine(outDir, "dungeon-timers.json"), FightReport.DraftJson(timerMod), new UTF8Encoding(false));
        var timers = timerMod.Bosses.Sum(b => b.Triggers.Count(t => t.Bar is not null));

        con.WriteLine(sb.ToString());
        con.WriteLine(L.T($"Countdowns: {timers} timer(s) for {timerMod.Bosses.Count} boss(es) → dungeon-timers.json, details in vorhersagen.txt",
            $"Countdowns: {timers} Timer für {timerMod.Bosses.Count} Boss(e) → dungeon-timers.json, Details in vorhersagen.txt"));
        con.WriteLine(L.T($"Output: {outDir}", $"Ausgabe: {outDir}"));
        return 0;
    }
}
