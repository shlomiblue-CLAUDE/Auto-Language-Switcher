using System.Text.Json.Serialization;

namespace AutoLang.Core;

/// <summary>
/// A language the product can switch the keyboard to. v1 ships Hebrew and English; the detector
/// and script table are structured so adding another is a data row, not a code change.
/// </summary>
// Serialised by name, declared on the type so the source-generated contexts pick it up without
// a converter in the options - the non-generic JsonStringEnumConverter is not trim-safe.
[JsonConverter(typeof(JsonStringEnumConverter<Language>))]
public enum Language
{
    /// <summary>Not enough evidence to choose. Callers must treat this as "change nothing".</summary>
    Unknown = 0,
    Hebrew,
    English
}

public static class LanguageExtensions
{
    /// <summary>BCP-47 tag used on the wire and by the layout service.</summary>
    public static string ToTag(this Language language) => language switch
    {
        Language.Hebrew => "he-IL",
        Language.English => "en-US",
        _ => "unknown"
    };

    public static Language FromTag(string tag) => tag?.ToLowerInvariant() switch
    {
        "he" or "he-il" or "hebrew" => Language.Hebrew,
        "en" or "en-us" or "english" => Language.English,
        _ => Language.Unknown
    };
}
