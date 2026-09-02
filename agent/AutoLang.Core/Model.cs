using System.Text.Json.Serialization;

namespace AutoLang.Core;

/// <summary>Abstracts the clock so every guard in the engine is testable without sleeping.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}

public enum MessageDirection
{
    Outgoing,
    Incoming
}

/// <summary>One observed message, reduced to letter counts. Never carries text.</summary>
public sealed record MessageObservation(MessageDirection Direction, MessageStats Stats, int Index);

/// <summary>Per-conversation mode. A pin beats every form of analysis (PDR section 5, priority 1).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConversationMode>))]
public enum ConversationMode
{
    Auto,
    AlwaysHebrew,
    AlwaysEnglish
}

public sealed record ConversationPreference
{
    public ConversationMode Mode { get; init; } = ConversationMode.Auto;

    /// <summary>
    /// The layout the user was actually typing with, last time they typed here.
    ///
    /// This is ground truth about their keyboard rather than an inference from their words, which
    /// is why PDR section 5 ranks it above message analysis. It is captured at the moment the
    /// typing guard fires: a non-empty composer means the user is typing right now, with a layout
    /// we can read.
    /// </summary>
    public Language LastReliableLanguage { get; init; } = Language.Unknown;

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When the user last overrode us here. Starts the cooldown of PDR section 12.</summary>
    public DateTimeOffset? ManualOverrideAt { get; init; }

    public ConversationMode PinnedMode => Mode;

    public Language? PinnedLanguage => Mode switch
    {
        ConversationMode.AlwaysHebrew => Language.Hebrew,
        ConversationMode.AlwaysEnglish => Language.English,
        _ => null
    };
}

public sealed record SiteState
{
    public bool Paused { get; init; }
    public string AdapterVersion { get; init; } = "";
}

public sealed record Settings
{
    public static readonly Settings Default = new();

    public bool Enabled { get; init; } = true;

    /// <summary>Applied only when there is no other evidence at all.</summary>
    public Language DefaultLanguage { get; init; } = Language.Unknown;

    public double ConfidenceThreshold { get; init; } = 0.70;
    public bool ShowIndicator { get; init; } = true;

    /// <summary>PDR section 12: at most one switch per this interval.</summary>
    public TimeSpan HysteresisWindow { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>How long to stop auto-switching in a conversation after the user overrides us.</summary>
    public TimeSpan ManualCooldown { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Beyond this age a remembered language stops outranking fresh analysis.
    ///
    /// Not in the PDR - added because an unbounded memory ossifies. A conversation dormant for
    /// months may have changed language entirely, and there is no reason to trust a stale record
    /// over what the user has actually been writing.
    /// </summary>
    public TimeSpan MemoryTtl { get; init; } = TimeSpan.FromDays(90);
}
