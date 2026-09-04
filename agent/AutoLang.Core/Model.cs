using System.Text.Json;
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
[JsonConverter(typeof(ConversationModeConverter))]
public enum ConversationMode
{
    Auto,
    Pinned
}

/// <summary>
/// Reads the mode, and survives a file written before there were more than two languages.
///
/// The mode used to name the language: Auto, AlwaysHebrew, AlwaysEnglish - one value per language,
/// which stops working the moment there is a third. It is now Auto or Pinned, with the language
/// stored beside it.
///
/// A store written by the old shape would otherwise fail to deserialise, and it would take
/// everything with it: one unreadable enum means the whole conversations file fails to load, and
/// what is in that file is every language the product has learned. A pin is one click to restore;
/// the memory of fifty conversations is not. So an unrecognised value reads as Auto rather than
/// throwing, and the pin - not the memory - is what a user loses once.
/// </summary>
public sealed class ConversationModeConverter : JsonConverter<ConversationMode>
{
    public override ConversationMode Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        Enum.TryParse<ConversationMode>(reader.GetString(), ignoreCase: true, out var mode)
            ? mode
            : ConversationMode.Auto;

    public override void Write(Utf8JsonWriter writer, ConversationMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
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

    /// <summary>
    /// The language this conversation is pinned to, meaningful only when Mode is Pinned.
    ///
    /// Stored rather than derived from the mode, which is the change that lets a pin name any
    /// language instead of the two the mode enum used to spell out.
    /// </summary>
    public Language PinnedLanguage { get; init; } = Language.Unknown;

    /// <summary>The pin, or null when there is not one. Derived, so never written to disk.</summary>
    [JsonIgnore]
    public Language? Pin =>
        Mode == ConversationMode.Pinned && PinnedLanguage != Language.Unknown ? PinnedLanguage : null;
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

    /// <summary>
    /// The languages the product may switch to, chosen by the user in advance.
    ///
    /// Empty means "whatever Windows has installed", which is the sensible default and what a
    /// first run gets. Naming a subset is what makes the product usable for somebody who has five
    /// layouts installed but writes in two of them: without it, a page in a language they can read
    /// but never write would drag their keyboard somewhere useless.
    ///
    /// It is a filter and never a source. Nothing here can cause a switch; it can only prevent
    /// one, which keeps the evidence order in the engine the only thing that decides.
    /// </summary>
    public IReadOnlyList<Language> EnabledLanguages { get; init; } = [];

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
