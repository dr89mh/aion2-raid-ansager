namespace RaidAnsager.App;

public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RaidAnsager");

    /// <summary>Boss mods of the user (a newer version replaces the built-in one).</summary>
    public static string Mods => Path.Combine(Root, "mods");

    /// <summary>Log of every pull (.txt readable, .jsonl for analysis, .entwurf.json draft mod).</summary>
    public static string Fights => Path.Combine(Root, "kaempfe");

    /// <summary>Raw pcapng recordings of the time spent in a mod zone.</summary>
    public static string Raw => Path.Combine(Root, "mitschnitte");

    public static string Settings => Path.Combine(Root, "einstellungen.json");

    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>Mods next to the exe (for handing out an updated mod together with the program).</summary>
    public static string ExeMods => Path.Combine(ExeDir, "mods");

    public static IEnumerable<string> ModFolders => [ExeMods, Mods];
}
