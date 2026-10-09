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

    /// <summary>The language the user last set by hand here, so a reversal can be recognised.</summary>
    public Language LastOverrideLanguage { get; init; } = Language.Unknown;

    /// <summary>
    /// How many times the user's own corrections here have reversed direction.
    ///
    /// Counted rather than inferred, because the question it answers cannot be asked of a single
    /// observation: is this key one conversation, or a bucket holding several?
    ///
    /// An application that keeps one window title for everything puts every conversation in it
    /// under one key. The Claude desktop app is measured doing exactly that - its window is called
    /// "Claude" and nothing else, so a Hebrew chat and an English one share a memory and the last
    /// one seen wins. On this machine that single key took 479 decisions over 20 days and the user
    /// corrected it by hand 15 times, reversing direction 9 of those. ForegroundWatcher predicted
    /// the shape - "without the second event every channel in Slack would share one memory, which
    /// is the thing per-window identity exists to prevent" - and the second event does not help
    /// when the title never moves.
    /// </summary>
    public int OverrideReversals { get; init; }

    /// <summary>
    /// Reversals beyond which this key is not one conversation, so its memory must not be replayed.
    ///
    /// Three, and the number is measured rather than chosen. Counting *reversals* is the whole
    /// rule: a conversation whose language genuinely moved - Hebrew for a month, then English -
    /// changes direction once, and there is nothing wrong with it. The first version of this rule
    /// asked for two overrides in two different languages, and the log threw it out: it marked 14
    /// keys, 9 of them ordinary WhatsApp chats, which is the product's main use case.
    ///
    /// Reversals separate cleanly. Across a month, no WhatsApp chat reversed more than twice; the
    /// two buckets in the Claude app reversed 3 and 9 times. The bar sits in that gap, on the safe
    /// side: a false positive costs a real conversation its memory, which is the feature.
    ///
    /// Deliberately never decays. An identity that has shown it holds several conversations does not
    /// become a single one later. The escape hatch for a user who genuinely writes both languages
    /// in one chat already exists and is explicit - pin it, and the pin outranks everything here.
    /// </summary>
    private const int ReversalsThatMeanSeveralConversations = 3;

    /// <summary>
    /// True when this key has been shown to cover more than one conversation.
    ///
    /// Derived, so it is never written to disk and never has to be migrated. The engine's response
    /// is to stop replaying the remembered language: on a surface it can read, it decides on the
    /// evidence in front of it instead, and on one it cannot - an application - it does nothing at
    /// all, which costs a keystroke and is the right trade. The engine states that trade in its own
    /// first paragraph: a missed switch costs one keypress, a wrong one costs a deleted line.
    /// </summary>
    [JsonIgnore]
    public bool CoversSeveralConversations => OverrideReversals >= ReversalsThatMeanSeveralConversations;
}

public sealed record SiteState
{
    public bool Paused { get; init; }
    public string AdapterVersion { get; init; } = "";
}

/// <summary>
/// One application the user has allowed the Agent to watch.
///
/// Presence in the store *is* the permission. There is no Allowed flag, because a flag invites a
/// file that lists every application somebody has ever run with most of them switched off - which
/// is the record this feature exists to avoid keeping.
/// </summary>
public sealed record AppState
{
    public bool Paused { get; init; }

    /// <summary>When the user allowed it. Diagnostic, and a reason to trust the file's contents.</summary>
    public DateTimeOffset AllowedAt { get; init; }
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

    /// <summary>
    /// The salt used to hash desktop window titles, generated once on this machine.
    ///
    /// Lives here rather than beside the browser's salt because the Agent has to hash without the
    /// browser being involved at all. It is not a secret in the usual sense - losing it costs the
    /// memory of every desktop window and nothing else - but it must never leave the machine, or
    /// the stored keys stop being meaningless elsewhere.
    /// </summary>
    public string DesktopSalt { get; init; } = "";

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
