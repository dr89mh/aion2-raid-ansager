using System.Text.Json;
using RaidAnsager.Engine;

namespace RaidAnsager.App;

public sealed class Settings
{
    /// <summary>Overlay position (top-left, screen pixels); -1 = centred in the upper third of the main screen.</summary>
    public int OverlayX { get; set; } = -1;
    public int OverlayY { get; set; } = -1;
    public double Scale { get; set; } = 1.0;
    public int Volume { get; set; } = 100;
    /// <summary>Speech rate −10…10; a bit faster than normal suits callouts.</summary>
    public int Rate { get; set; } = 2;
    public string? Voice { get; set; }
    public string Role { get; set; } = "alle";
    public bool SayUnknown { get; set; } = true;
    /// <summary>Also dungeon bosses without a mod (HP line, skill names, fight log).</summary>
    public bool AllBosses { get; set; } = true;
    public bool Muted { get; set; }
    public bool Tones { get; set; } = true;
    /// <summary>Keep a raw pcapng of the time spent in a mod zone (for building the boss mod after the raid).</summary>
    public bool RecordRaw { get; set; } = true;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.Settings), ModLoader.Json) ?? new Settings();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, ModLoader.Json));
        }
        catch (IOException) { }
    }
}
