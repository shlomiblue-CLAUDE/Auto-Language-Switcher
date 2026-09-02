namespace AutoLang.Core;

/// <summary>Tunable thresholds from PDR section 5. Defaults are the values the document specifies.</summary>
public sealed record DetectionOptions
{
    public static readonly DetectionOptions Default = new();

    /// <summary>How many of the user's own recent messages to weigh.</summary>
    public int MaxMessages { get; init; } = 10;

    /// <summary>A message with fewer relevant letters than this says nothing and is skipped.</summary>
    public int MinLettersPerMessage { get; init; } = 3;

    public double NewestWeight { get; init; } = 1.0;
    public double OldestWeight { get; init; } = 0.4;

    /// <summary>Below this, the engine must do nothing rather than guess.</summary>
    public double ConfidenceThreshold { get; init; } = 0.70;

    /// <summary>The winner needs this much weighted evidence, so three letters cannot decide.</summary>
    public double MinWeightedLetters { get; init; } = 5.0;
}
