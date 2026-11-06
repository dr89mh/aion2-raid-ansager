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
                return TrayApp.Run(replayFile: args[1], speed);
            }
            case "":
                return TrayApp.Run(replayFile: null, speed: 1);
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
        System.Console.WriteLine("""
            Raid-Ansager für Aion 2
              RaidAnsager                          Live-Betrieb (Symbol im Infobereich)
              RaidAnsager replay <datei.pcapng> [--speed 4]
                                                   Aufnahme abspielen, mit Overlay und Sprache (zum Testen)
              RaidAnsager analyse <datei.pcapng …> [--out <ordner>]
                                                   Boss-Kämpfe auswerten: Protokolle, Statistik, Mod-Entwurf
              RaidAnsager pruefen                  Boss-Mods laden und prüfen
            """);
        return 2;
    }
}
