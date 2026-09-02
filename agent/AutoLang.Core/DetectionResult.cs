namespace AutoLang.Core;

/// <summary>Why the detector reached its verdict. Surfaced in the popup so the user is never guessing.</summary>
public enum DetectionReason
{
    /// <summary>No messages were supplied at all.</summary>
    NoMessages,

    /// <summary>Every message was emoji, digits, links or too short to carry a signal.</summary>
    NoUsableText,

    /// <summary>Languages are too evenly mixed to call.</summary>
    LowConfidence,

    /// <summary>One language leads, but on too little evidence to act on.</summary>
    InsufficientEvidence,

    /// <summary>Clear winner, above both thresholds.</summary>
    Confident
}

public sealed record DetectionResult(
    Language Winner,
    IReadOnlyDictionary<Language, double> Scores,
    double Confidence,
    int MessagesConsidered,
    int MessagesSkipped,
    bool IsActionable,
    DetectionReason Reason)
{
    public static DetectionResult None(DetectionReason reason, int skipped = 0) =>
        new(Language.Unknown, new Dictionary<Language, double>(), 0d, 0, skipped, false, reason);

    public double ScoreOf(Language language) => Scores.TryGetValue(language, out double s) ? s : 0d;

    public override string ToString() =>
        $"{Winner} conf={Confidence:F2} actionable={IsActionable} reason={Reason} " +
        $"scores=[{string.Join(", ", Scores.Select(kv => $"{kv.Key}={kv.Value:F2}"))}] " +
        $"considered={MessagesConsidered} skipped={MessagesSkipped}";
}
