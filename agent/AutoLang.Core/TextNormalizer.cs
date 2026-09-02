using System.Text.RegularExpressions;

namespace AutoLang.Core;

/// <summary>
/// Turns message text into per-language letter counts.
///
/// PDR section 5 asks to strip URLs, numbers, emoji, punctuation and whitespace. Only the first of
/// those actually needs stripping: because <see cref="ScriptTable.Classify"/> counts a character
/// only when it is a letter in a known script, digits, emoji, punctuation and whitespace are
/// already ignored. URLs and email addresses are different - they are full of Latin letters that
/// would otherwise be read as "this person writes English", which is exactly the false signal the
/// PDR warns about.
/// </summary>
public static partial class TextNormalizer
{
    [GeneratedRegex(@"\b(?:https?|ftp)://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeUrl();

    [GeneratedRegex(@"\bwww\.\S+", RegexOptions.IgnoreCase)]
    private static partial Regex WwwUrl();

    [GeneratedRegex(@"\S+@\S+\.\S+")]
    private static partial Regex Email();

    // Bare domains such as "google.com" are common in chat. The TLD allowlist keeps this from
    // eating ordinary text like "ok.thanks" - a generic \w+\.\w+ pattern would.
    [GeneratedRegex(@"\b(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+(?:com|org|net|io|me|co|il|gov|edu|info|biz|app|dev|ai|ly|uk|de|fr)\b(?:/\S*)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex BareDomain();

    /// <summary>Removes spans that carry letters but say nothing about the writer's language.</summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var s = SchemeUrl().Replace(text, " ");
        s = WwwUrl().Replace(s, " ");
        s = Email().Replace(s, " ");
        s = BareDomain().Replace(s, " ");
        return s;
    }

    /// <summary>Counts letters per language, iterating by code point so surrogate pairs stay intact.</summary>
    public static MessageStats Analyze(string? text)
    {
        var cleaned = Strip(text);
        if (cleaned.Length == 0) return MessageStats.Empty;

        var counts = new Dictionary<Language, int>();

        for (int i = 0; i < cleaned.Length; i++)
        {
            int codePoint = char.IsHighSurrogate(cleaned[i]) && i + 1 < cleaned.Length && char.IsLowSurrogate(cleaned[i + 1])
                ? char.ConvertToUtf32(cleaned[i], cleaned[++i])
                : cleaned[i];

            if (ScriptTable.Classify(codePoint) is { } language)
                counts[language] = counts.GetValueOrDefault(language) + 1;
        }

        return new MessageStats(counts);
    }
}
