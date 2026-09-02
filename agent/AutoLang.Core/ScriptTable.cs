namespace AutoLang.Core;

/// <summary>An inclusive range of Unicode code points belonging to one script.</summary>
public readonly record struct ScriptRange(int Start, int End)
{
    public bool Contains(int codePoint) => codePoint >= Start && codePoint <= End;
}

/// <summary>Maps a script's code point ranges to the language whose keyboard layout it implies.</summary>
public sealed record ScriptDefinition(Language Language, string Name, IReadOnlyList<ScriptRange> Ranges);

/// <summary>
/// The single place that decides which characters count as evidence for which language.
///
/// Adding Russian or Arabic later is one entry here plus a layout mapping - no detector changes.
/// That is what PDR section 19 ("שפות נוספות באמצעות Unicode-script detection") requires.
/// </summary>
public static class ScriptTable
{
    public static readonly IReadOnlyList<ScriptDefinition> Definitions =
    [
        new(Language.Hebrew, "Hebrew",
        [
            new ScriptRange(0x0590, 0x05FF),   // Hebrew block, per PDR section 5
            new ScriptRange(0xFB1D, 0xFB4F)    // Hebrew presentation forms (ligatures, e.g. U+FB4F)
        ]),

        new(Language.English, "Latin",
        [
            new ScriptRange(0x0041, 0x005A),   // A-Z
            new ScriptRange(0x0061, 0x007A)    // a-z
        ])

        // Future: new(Language.Russian, "Cyrillic", [new ScriptRange(0x0400, 0x04FF)])
        // Future: new(Language.Arabic,  "Arabic",   [new ScriptRange(0x0600, 0x06FF)])
    ];

    /// <summary>
    /// Which language a code point is evidence for, or null if it is evidence for nothing.
    ///
    /// The <c>IsLetter</c> filter matters more than it looks: the Hebrew block also holds niqqud and
    /// cantillation marks (U+0591-U+05C7), which .NET classifies as NonSpacingMark. Counting those
    /// as letters would inflate Hebrew scores on vocalised text - a single verse could outweigh a
    /// whole conversation. The PDR names the block; we count only the letters inside it.
    /// </summary>
    public static Language? Classify(int codePoint)
    {
        if (!IsLetterCodePoint(codePoint)) return null;

        foreach (var def in Definitions)
            foreach (var range in def.Ranges)
                if (range.Contains(codePoint))
                    return def.Language;

        return null;
    }

    private static bool IsLetterCodePoint(int codePoint)
    {
        if (codePoint <= 0xFFFF) return char.IsLetter((char)codePoint);
        var s = char.ConvertFromUtf32(codePoint);
        return char.IsLetter(s, 0);
    }
}
