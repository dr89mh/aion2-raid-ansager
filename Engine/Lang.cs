using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaidAnsager.Engine;

/// <summary>Current UI/callout language: "en" (default) or "de".</summary>
public static class L
{
    public static readonly string[] Languages = ["en", "de"];

    private static volatile string _lang = "en";

    public static string Lang
    {
        get => _lang;
        set => _lang = value == "de" ? "de" : "en";
    }

    public static bool De => _lang == "de";

    /// <summary>The English or the German text, depending on <see cref="Lang"/>.</summary>
    public static string T(string en, string de) => De ? de : en;

    public static string AppName => T("Raid Announcer", "Raid-Ansager");
}

/// <summary>
/// A text in a boss mod: either a plain string (used for every language) or an object per language,
/// <c>{ "en": "Spread out", "de": "Verteilen" }</c>.
/// </summary>
[JsonConverter(typeof(LocalTextConverter))]
public sealed class LocalText
{
    public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static implicit operator LocalText(string text) => new() { Texts = { [""] = text } };

    public static LocalText Of(string? en, string? de)
    {
        var t = new LocalText();
        if (en is not null) t.Texts["en"] = en;
        if (de is not null) t.Texts["de"] = de;
        return t;
    }

    /// <summary>The text for the current language; falls back to the plain text, then English, then any.</summary>
    public string Get() =>
        Texts.TryGetValue(L.Lang, out var t) ? t
        : Texts.TryGetValue("", out t) ? t
        : Texts.TryGetValue("en", out t) ? t
        : Texts.Values.FirstOrDefault() ?? "";

    public bool Matches(string text) => Texts.Values.Any(v => string.Equals(v, text, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Get();
}

public sealed class LocalTextConverter : JsonConverter<LocalText>
{
    public override LocalText? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString() ?? "";
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Text oder { \"en\": …, \"de\": … } erwartet");
        var t = new LocalText();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var lang = reader.GetString() ?? "";
            reader.Read();
            t.Texts[lang] = reader.GetString() ?? "";
        }
        return t;
    }

    public override void Write(Utf8JsonWriter writer, LocalText value, JsonSerializerOptions options)
    {
        if (value.Texts.Count == 1 && value.Texts.ContainsKey(""))
        {
            writer.WriteStringValue(value.Texts[""]);
            return;
        }
        writer.WriteStartObject();
        foreach (var (lang, text) in value.Texts) writer.WriteString(lang.Length == 0 ? "any" : lang, text);
        writer.WriteEndObject();
    }
}
