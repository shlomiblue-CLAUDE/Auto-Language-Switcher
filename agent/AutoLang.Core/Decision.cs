namespace AutoLang.Core;

/// <summary>What the engine was asked about. Everything it needs, nothing it does not.</summary>
public sealed record DecisionRequest
{
    public required string ConversationKey { get; init; }
    public required string Site { get; init; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<MessageObservation> Messages { get; init; } = [];

    /// <summary>False means the user is mid-sentence right now.</summary>
    public bool ComposerEmpty { get; init; } = true;

    /// <summary>
    /// Whether the browser owns the foreground window.
    ///
    /// The phase 0 spike proved Windows will happily change a background window's layout, so this
    /// is not a courtesy check - it is the only thing standing between a background tab and the
    /// layout of whatever the user is actually typing in.
    /// </summary>
    public bool BrowserIsForeground { get; init; } = true;

    /// <summary>The layout in effect right now, so we never ask for a switch that is already true.</summary>
    public Language CurrentLayout { get; init; } = Language.Unknown;

    public required DateTimeOffset ObservedAt { get; init; }
}

/// <summary>Where a chosen language came from. Shown in the popup so the user is never guessing.</summary>
public enum DecisionSource
{
    None,
    ManualPin,
    ConversationMemory,
    OutgoingMessages,
    AllMessagesFallback,
    GlobalDefault
}

/// <summary>Why no switch happened. Also surfaced in the popup.</summary>
public enum DecisionBlocker
{
    None,
    Disabled,
    SitePaused,
    NotForeground,
    UserTyping,
    ManualCooldown,

    /// <summary>The user changed the layout by hand, and we are getting out of the way.</summary>
    ManualChange,

    LowConfidence,

    /// <summary>The evidence pointed at a language the user has switched off.</summary>
    LanguageDisabled,


    NoSignal,
    Hysteresis,
    AlreadyCorrect
}

public enum DecisionOutcome
{
    /// <summary>Ask the layout service to switch.</summary>
    Switch,

    /// <summary>A language is known and already active. Nothing to do.</summary>
    NoChange,

    /// <summary>A guard refused. The blocker says which.</summary>
    Suppressed
}

public sealed record Decision
{
    public required DecisionOutcome Outcome { get; init; }
    public Language Language { get; init; } = Language.Unknown;
    public double Confidence { get; init; }
    public DecisionSource Source { get; init; } = DecisionSource.None;
    public DecisionBlocker Blocker { get; init; } = DecisionBlocker.None;

    /// <summary>
    /// True when this observation taught us something worth persisting, so the caller knows to
    /// save without the engine reaching for storage itself.
    /// </summary>
    public Language LearnedLanguage { get; init; } = Language.Unknown;

    /// <summary>
    /// True when the layout changed to something we did not ask for, which can only be the user.
    ///
    /// Separate from LearnedLanguage because it means more than "remember this": it starts the
    /// cooldown of PDR section 12, so the product stops arguing for a while rather than switching
    /// back on the next observation.
    /// </summary>
    public bool UserOverrode { get; init; }

    public bool ShouldSwitch => Outcome == DecisionOutcome.Switch;

    public override string ToString() =>
        $"{Outcome} {Language} conf={Confidence:F2} source={Source} blocker={Blocker}";
}
