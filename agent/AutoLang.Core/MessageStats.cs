namespace AutoLang.Core;

/// <summary>
/// Letter counts for one message, keyed by the language each letter is evidence for.
///
/// This - not the message text - is what crosses every boundary in the system. The browser content
/// script produces it locally and the Agent consumes it, so PDR section 11's guarantee holds by
/// construction: there is no field here that could carry message content out of the page.
/// </summary>
public sealed record MessageStats(IReadOnlyDictionary<Language, int> Counts)
{
    public static readonly MessageStats Empty = new(new Dictionary<Language, int>());

    public int Of(Language language) => Counts.TryGetValue(language, out int n) ? n : 0;

    /// <summary>Total letters that are evidence for some supported language.</summary>
    public int Relevant => Counts.Values.Sum();

    public static MessageStats From(params (Language Language, int Count)[] counts) =>
        new(counts.Where(c => c.Count > 0).ToDictionary(c => c.Language, c => c.Count));
}
