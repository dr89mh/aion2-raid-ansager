using System.Runtime.InteropServices;
using RaidAnsager.Analysis;
using RaidAnsager.App;
using RaidAnsager.Engine;

namespace RaidAnsager;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        // language: from the settings, "--lang en|de" overrides it for this run
        L.Lang = Settings.Load().Language;
        var lang = Array.IndexOf(args, "--lang");
        if (lang >= 0 && lang + 1 < args.Length)
        {
            L.Lang = args[lang + 1];
            args = args.Where((_, i) => i != lang && i != lang + 1).ToArray();
        }
        // the downloaded new version, started by the old one to put itself in its place
        if (args.Length > 0 && args[0] == Updater.InstallFlag) return Updater.RunInstaller(args[1..]);
        var afterUpdate = args.Contains(Updater.AfterUpdateFlag);
        args = args.Where(a => a != Updater.AfterUpdateFlag).ToArray();
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (cmd)
        {
            case "analyse":
            case "analyze":
            {
                Console();
                string? outDir = null;
                var files = new List<string>();
                for (var i = 1; i < args.Length; i++)
                {
                    if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
                    else files.Add(args[i]);
                }
                return AnalyseCommand.Run(files, outDir, System.Console.Out);
            }
            case "pruefen":
            case "check":
            {
                Console();
                var problems = new List<string>();
                var mods = ModLoader.LoadAll(AppPaths.ModFolders, problems);
                foreach (var m in mods)
                    System.Console.WriteLine($"{m.Id} {m.Version}: {m.Bosses.Count} Bosse, " +
                                             $"{m.Bosses.Sum(b => b.Triggers.Count)} Trigger ({m.Source})");
                foreach (var p in problems) System.Console.WriteLine("Problem: " + p);
                return problems.Count == 0 ? 0 : 1;
            }
            case "hochladen":
            case "upload":
            {
                Console();
                var (sent, error) = Uploader.UploadAsync(Settings.Load()).GetAwaiter().GetResult();
                System.Console.WriteLine(error ?? L.T($"{sent} fight log(s) uploaded to {Uploader.Service}", $"{sent} Kampfprotokoll(e) hochgeladen nach {Uploader.Service}"));
                return error is null ? 0 : 1;
            }
            case "abholen":
            case "collect":
            {
                Console();
                System.Console.WriteLine(L.T($"Collecting uploads from {Uploader.Service} …", $"Hole Uploads von {Uploader.Service} …"));
                var (fresh, total, error) = Uploader.CollectAsync(args.Contains("--loeschen") || args.Contains("--delete"), System.Console.Out)
                    .GetAwaiter().GetResult();
                if (error is not null)
                {
                    System.Console.WriteLine(error);
                    return 1;
                }
                System.Console.WriteLine(L.T($"{fresh} new of {total} upload(s) → {Uploader.Inbox}", $"{fresh} neu von {total} Upload(s) → {Uploader.Inbox}"));
                if (args.Contains("--ohne-analyse") || args.Contains("--no-analysis")) return 0;
                // the uploads of all members plus the own logs
                return AnalyseCommand.Run([Uploader.Inbox, AppPaths.Fights], null, System.Console.Out);
            }
            case "export":
            {
                Console();
                var (path, files) = LogExport.Create();
                System.Console.WriteLine(L.T($"{files} fight log(s) → {path}", $"{files} Kampfprotokoll(e) → {path}"));
                return 0;
            }
            case "vorschau":
                Console();
                return PreviewCommand.Run(args, System.Console.Out);
            case "sprachtest":
                Console();
                return PreviewCommand.Speech(args, System.Console.Out);
            case "replay":
            {
                if (args.Length < 2) return Usage();
                var speed = 1.0;
                for (var i = 2; i < args.Length - 1; i++)
                    if (args[i] == "--speed" && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var s)) speed = s;
                return TrayApp.Run(replayFile: args[1], speed, afterUpdate: false);
            }
            case "":
                return TrayApp.Run(replayFile: null, speed: 1, afterUpdate);
            default:
                return Usage();
        }
    }

    private static void Console()
    {
        AttachConsole(-1);
        System.Console.OutputEncoding = System.Text.Encoding.UTF8;
        System.Console.WriteLine();
    }

    private static int Usage()
    {
        Console();
        System.Console.WriteLine(L.T("""
            Raid Announcer for Aion 2
              RaidAnsager                          live (icon in the notification area)
              RaidAnsager replay <file.pcapng> [--speed 4]
                                                   play a recording with overlay and speech (for testing)
              RaidAnsager analyse <file.pcapng …> [--out <folder>]
                                                   boss fights: logs, statistics, draft mod
              RaidAnsager vorschau <file.pcapng> [--mod <file>] [--alle]
                                                   no sound: list of callouts and overlay images
              RaidAnsager pruefen                  load and check the boss mods
              --lang en|de                         language for this run
            """, """
            Raid-Ansager für Aion 2
              RaidAnsager                          Live-Betrieb (Symbol im Infobereich)
              RaidAnsager replay <datei.pcapng> [--speed 4]
                                                   Aufnahme abspielen, mit Overlay und Sprache (zum Testen)
              RaidAnsager analyse <datei.pcapng …> [--out <ordner>]
                                                   Boss-Kämpfe auswerten: Protokolle, Statistik, Mod-Entwurf
              RaidAnsager vorschau <datei.pcapng> [--mod <datei>] [--alle]
                                                   ohne Ton: Ansageliste und Overlay-Bilder
              RaidAnsager pruefen                  Boss-Mods laden und prüfen
              --lang en|de                         Sprache für diesen Aufruf
            """));
        return 2;
    }
}
