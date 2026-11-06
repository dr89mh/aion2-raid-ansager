using System.Drawing.Imaging;
using System.Globalization;
using System.Speech.Synthesis;
using System.Text;
using CubeRadar.Core.Capture;
using RaidAnsager.App;
using RaidAnsager.Engine;

namespace RaidAnsager.Analysis;

/// <summary>
/// Test tools without screen or speakers:
/// <c>vorschau &lt;pcapng&gt; [--mod datei.json] [--alle] [--out ordner] [--step s] [--max n]</c> replays a recording as fast as
/// possible, writes every callout to ansagen.txt and an overlay PNG every few seconds of fight time;
/// <c>sprachtest &lt;datei.wav&gt;</c> speaks a sample into a WAV file.
/// </summary>
public static class PreviewCommand
{
    private sealed class LogOutput(List<string> lines, Func<long> now) : IAnnouncerOutput
    {
        public void Say(string text, bool urgent) => lines.Add($"{Stamp(now())}  SAGT{(urgent ? "!" : " ")} {text}");
        public void Sound(string kind) => lines.Add($"{Stamp(now())}  TON   {kind}");
        private static string Stamp(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss.f", CultureInfo.InvariantCulture);
    }

    public static int Run(string[] args, TextWriter con)
    {
        string? capture = null, modFile = null, outDir = null;
        double step = 2;
        var allBosses = false;
        var max = 60;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mod" when i + 1 < args.Length: modFile = args[++i]; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--step" when i + 1 < args.Length: step = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--max" when i + 1 < args.Length: max = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--alle": allBosses = true; break;
                default: capture = args[i]; break;
            }
        }
        if (capture is null) return 2;
        outDir ??= Path.Combine(AppPaths.Fights, "vorschau_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(outDir);

        var data = GameData.Load();
        var problems = new List<string>();
        var mods = ModLoader.LoadAll(AppPaths.ModFolders, problems);
        if (modFile is not null)
        {
            var extra = ModLoader.Parse(File.ReadAllText(modFile), modFile);
            problems.AddRange(ModLoader.Check(extra));
            mods.RemoveAll(m => m.Id == extra.Id);
            mods.Add(extra);
        }
        foreach (var p in problems) con.WriteLine("Mod-Hinweis: " + p);

        var lines = new List<string>();
        Announcer? engine = null;
        long now = 0;
        engine = new Announcer(data, mods, new LogOutput(lines, () => now), new AnnouncerOptions { SayUnknown = true, AllBosses = allBosses, InstanceBossesOnly = true });
        var fights = new List<Fight>();
        engine.FightEnded += f =>
        {
            fights.Add(f);
            lines.Add($"---- Ende {f.Def.Name}: {f.Outcome} ({FightReport.Clock((f.EndMs - f.PullMs) / 1000.0)})");
        };
        var frames = 0;
        long nextFrame = 0;
        using (var pipe = new Pipeline(engine, tickWithPackets: true))
        {
            CaptureFileSource.ReadAll(capture, raw =>
            {
                now = (long)raw.Timeval.Seconds * 1000 + (long)raw.Timeval.MicroSeconds / 1000;
                pipe.OnPacket(raw, "datei");
                if (frames >= max || now < nextFrame) return;
                var st = engine.Snapshot();
                if (st.Bosses.Count == 0) return;
                nextFrame = now + (long)(step * 1000);
                using var bmp = OverlayForm.Draw(st, null, 1.0, moveMode: false);
                var stamp = DateTimeOffset.FromUnixTimeMilliseconds(now).ToLocalTime().ToString("HHmmss_f", CultureInfo.InvariantCulture);
                bmp.Save(Path.Combine(outDir, $"overlay_{frames:000}_{stamp}.png"), ImageFormat.Png);
                frames++;
            });
            engine.Flush("Ende der Aufnahme");
        }
        File.WriteAllLines(Path.Combine(outDir, "ansagen.txt"), lines, new UTF8Encoding(true));
        con.WriteLine($"{lines.Count} Zeilen in ansagen.txt, {frames} Overlay-Bilder, {fights.Count} Kämpfe → {outDir}");
        return 0;
    }

    public static int Speech(string[] args, TextWriter con)
    {
        var file = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "raidansager_sprachtest.wav");
        using var tts = new SpeechSynthesizer();
        var de = tts.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo)
            .FirstOrDefault(v => v.Culture.Name.StartsWith("de", StringComparison.OrdinalIgnoreCase));
        if (de is not null) tts.SelectVoice(de.Name);
        tts.Rate = 2;
        tts.SetOutputToWaveFile(file);
        tts.Speak("Purpurkreis weitergeben. Bingo in fünf.");
        tts.SetOutputToNull();
        con.WriteLine($"Stimme: {de?.Name ?? "Standard"}; {new FileInfo(file).Length:N0} Bytes → {file}");
        return 0;
    }
}
