using System.Text.Json.Serialization;

namespace AutoLang.Core;

/// <summary>
/// A language the product can switch the keyboard to.
///
/// These are the languages whose *script* the detector can tell apart. That distinction is the
/// limit of the method and worth stating plainly: Hebrew, Arabic, Cyrillic and Greek each have
/// their own alphabet, so a page written in one is recognisable. French, German and Spanish are
/// written in the same Latin alphabet as English and cannot be told from it by counting letters -
/// they would need a different kind of evidence, and are not here.
///
/// Which of these the product may actually switch to is a user setting; see Settings.
/// </summary>
// Serialised by name, declared on the type so the source-generated contexts pick it up without
// a converter in the options - the non-generic JsonStringEnumConverter is not trim-safe.
[JsonConverter(typeof(JsonStringEnumConverter<Language>))]
public enum Language
{
    /// <summary>Not enough evidence to choose. Callers must treat this as "change nothing".</summary>
    Unknown = 0,
    Hebrew,
    English,
    Russian,
    Arabic,
    Greek
}

public static class LanguageExtensions
{
    /// <summary>BCP-47 tag used on the wire and by the layout service.</summary>
    public static string ToTag(this Language language) => language switch
    {
        Language.Hebrew => "he-IL",
        Language.English => "en-US",
        Language.Russian => "ru-RU",
        Language.Arabic => "ar-SA",
        Language.Greek => "el-GR",
        _ => "unknown"
    };

    public static Language FromTag(string tag) => tag?.ToLowerInvariant() switch
    {
        "he" or "he-il" or "hebrew" => Language.Hebrew,
        "en" or "en-us" or "english" => Language.English,
        "ru" or "ru-ru" or "russian" => Language.Russian,
        "ar" or "ar-sa" or "arabic" => Language.Arabic,
        "el" or "el-gr" or "greek" => Language.Greek,
        _ => Language.Unknown
    };

    /// <summary>Every language the product knows how to detect and switch to.</summary>
    public static readonly IReadOnlyList<Language> All =
        [Language.Hebrew, Language.English, Language.Russian, Language.Arabic, Language.Greek];
}
