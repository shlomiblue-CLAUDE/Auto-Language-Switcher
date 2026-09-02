namespace AutoLang.Core;

/// <summary>
/// Decides which language the user is likely to type in, from their own recent messages.
///
/// Pure and deterministic: no clock, no I/O, no state. Everything it knows arrives as arguments,
/// which is what makes PDR section 15 phase 1 ("ממש פונקציות pure") testable without a browser.
///
/// The detector never answers "what language is this conversation in". It answers "what language
/// is this person about to type", which is why callers must feed it the user's OUTGOING messages.
/// </summary>
public sealed class LanguageDetector
{
    private readonly DetectionOptions _options;

    public LanguageDetector(DetectionOptions? options = null) => _options = options ?? DetectionOptions.Default;

    /// <summary>Convenience entry point for callers that hold text, such as tests and future desktop sources.</summary>
    public DetectionResult Detect(IReadOnlyList<string?> messagesNewestFirst) =>
        Score(messagesNewestFirst.Select(TextNormalizer.Analyze).ToList());

    /// <summary>
    /// The real entry point. The browser content script analyses text in the page and sends only
    /// these counts, so the Agent decides without ever holding message content.
    /// </summary>
    public DetectionResult Score(IReadOnlyList<MessageStats> statsNewestFirst)
    {
        if (statsNewestFirst.Count == 0) return DetectionResult.None(DetectionReason.NoMessages);

        var window = statsNewestFirst.Take(_options.MaxMessages).ToList();

        // Weight by position among the messages that actually carry a signal, not among all of them.
        // Otherwise a single "👍" at the top of the conversation would demote the newest real
        // message from 1.0 to something lower for no reason.
        var usable = window.Where(m => m.Relevant >= _options.MinLettersPerMessage).ToList();
        int skipped = window.Count - usable.Count;

        if (usable.Count == 0) return DetectionResult.None(DetectionReason.NoUsableText, skipped);

        var scores = new Dictionary<Language, double>();
        for (int i = 0; i < usable.Count; i++)
        {
            double weight = WeightAt(i, usable.Count);
            foreach (var (language, letters) in usable[i].Counts)
                scores[language] = scores.GetValueOrDefault(language) + weight * letters;
        }

        double total = scores.Values.Sum();
        if (total <= 0d) return DetectionResult.None(DetectionReason.NoUsableText, skipped);

        // Strict > means ties resolve by ScriptTable order deterministically. A tie can never be
        // actionable anyway - confidence would be 0.5, far below the threshold.
        var winner = Language.Unknown;
        double best = 0d;
        foreach (var def in ScriptTable.Definitions)
        {
            double s = scores.GetValueOrDefault(def.Language);
            if (s > best) { best = s; winner = def.Language; }
        }

        double confidence = best / total;
        bool confident = confidence >= _options.ConfidenceThreshold;
        bool enoughEvidence = best >= _options.MinWeightedLetters;

        var reason = (confident, enoughEvidence) switch
        {
            (false, _) => DetectionReason.LowConfidence,
            (true, false) => DetectionReason.InsufficientEvidence,
            _ => DetectionReason.Confident
        };

        return new DetectionResult(
            winner, scores, confidence, usable.Count, skipped,
            IsActionable: confident && enoughEvidence && winner != Language.Unknown,
            reason);
    }

    /// <summary>Linear recency decay: newest gets NewestWeight, oldest in the window gets OldestWeight.</summary>
    private double WeightAt(int index, int count)
    {
        if (count <= 1) return _options.NewestWeight;
        double t = (double)index / (count - 1);
        return _options.NewestWeight - t * (_options.NewestWeight - _options.OldestWeight);
    }
}
