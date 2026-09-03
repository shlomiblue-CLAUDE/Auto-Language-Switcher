using AutoLang.Core;

namespace AutoLang.Core.Tests;

public sealed class TestClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => Now += by;
}

public class DecisionEngineTests
{
    private readonly TestClock _clock = new();
    private readonly DecisionEngine _engine;

    public DecisionEngineTests() => _engine = new DecisionEngine(_clock);

    private static MessageObservation Outgoing(Language language, int letters, int index = 0) =>
        new(MessageDirection.Outgoing, MessageStats.From((language, letters)), index);

    private static MessageObservation Incoming(Language language, int letters, int index = 0) =>
        new(MessageDirection.Incoming, MessageStats.From((language, letters)), index);

    private DecisionRequest Request(
        IEnumerable<MessageObservation>? messages = null,
        bool composerEmpty = true,
        bool foreground = true,
        Language currentLayout = Language.English,
        string key = "conv-1") => new()
    {
        ConversationKey = key,
        Site = "web.whatsapp.com",
        Messages = messages?.ToList() ?? [],
        ComposerEmpty = composerEmpty,
        BrowserIsForeground = foreground,
        CurrentLayout = currentLayout,
        ObservedAt = _clock.Now,
    };

    private Decision Decide(
        DecisionRequest request,
        Settings? settings = null,
        ConversationPreference? preference = null,
        SiteState? site = null) =>
        _engine.Decide(request, settings ?? Settings.Default, preference, site);

    // --- Guard 1: globally disabled ----------------------------------------------------------

    [Fact]
    public void Disabled_product_never_switches()
    {
        var decision = Decide(
            Request([Outgoing(Language.Hebrew, 30)]),
            Settings.Default with { Enabled = false });

        Assert.Equal(DecisionOutcome.Suppressed, decision.Outcome);
        Assert.Equal(DecisionBlocker.Disabled, decision.Blocker);
    }

    // --- Guard 2: site paused ----------------------------------------------------------------

    [Fact]
    public void Paused_site_never_switches()
    {
        var decision = Decide(
            Request([Outgoing(Language.Hebrew, 30)]),
            site: new SiteState { Paused = true });

        Assert.Equal(DecisionBlocker.SitePaused, decision.Blocker);
    }

    // --- Guard 3: focus ----------------------------------------------------------------------

    [Fact]
    public void Background_browser_never_switches()
    {
        // The spike proved Windows will change a background window's layout without complaint.
        // This guard is the only thing preventing a background tab from hijacking the keyboard
        // of whatever the user is actually typing in.
        var decision = Decide(Request([Outgoing(Language.Hebrew, 30)], foreground: false));

        Assert.Equal(DecisionBlocker.NotForeground, decision.Blocker);
    }

    [Fact]
    public void Focus_guard_outranks_even_a_manual_pin()
    {
        var decision = Decide(
            Request(foreground: false),
            preference: new ConversationPreference { Mode = ConversationMode.AlwaysHebrew });

        Assert.Equal(DecisionBlocker.NotForeground, decision.Blocker);
    }

    // --- Guard 4: typing ---------------------------------------------------------------------

    [Fact]
    public void No_switch_while_the_composer_has_text()
    {
        var decision = Decide(Request([Outgoing(Language.Hebrew, 30)], composerEmpty: false));

        Assert.Equal(DecisionOutcome.Suppressed, decision.Outcome);
        Assert.Equal(DecisionBlocker.UserTyping, decision.Blocker);
    }

    [Fact]
    public void Typing_teaches_the_engine_which_layout_the_user_actually_uses()
    {
        // The guard that blocks the switch is also the moment the strongest evidence appears:
        // the user is typing, right now, with a layout we can read.
        var decision = Decide(Request(composerEmpty: false, currentLayout: Language.Hebrew));

        Assert.Equal(DecisionBlocker.UserTyping, decision.Blocker);
        Assert.Equal(Language.Hebrew, decision.LearnedLanguage);
    }

    [Fact]
    public void Typing_guard_beats_a_manual_pin_too()
    {
        var decision = Decide(
            Request(composerEmpty: false, currentLayout: Language.English),
            preference: new ConversationPreference { Mode = ConversationMode.AlwaysHebrew });

        Assert.Equal(DecisionBlocker.UserTyping, decision.Blocker);
    }

    // --- Guard 5: uncertainty ----------------------------------------------------------------

    [Fact]
    public void Evenly_mixed_evidence_changes_nothing()
    {
        // Acceptance row "שיחה מעורבת". Note the evidence has to be mixed WITHIN a message: two
        // separate messages of equal length are not a 50/50 split once recency weighting applies.
        var mixed = new MessageObservation(
            MessageDirection.Outgoing,
            MessageStats.From((Language.Hebrew, 20), (Language.English, 20)),
            0);

        var decision = Decide(Request([mixed]));

        Assert.Equal(DecisionOutcome.Suppressed, decision.Outcome);
        Assert.Equal(DecisionBlocker.LowConfidence, decision.Blocker);
    }

    [Fact]
    public void Recency_is_allowed_to_break_a_tie_between_separate_messages()
    {
        // Two messages of identical length in different languages are not ambiguous: the newer one
        // carries weight 1.0 against the older one's 0.4, so 20 letters beat 20 letters. This is
        // the intended reading of "what is this person about to type", and pinning it here stops a
        // later change to the decay curve from silently altering behaviour.
        var decision = Decide(Request(
            [
                Outgoing(Language.Hebrew, 20, 0),
                Outgoing(Language.English, 20, 1),
            ],
            currentLayout: Language.English));

        Assert.Equal(DecisionOutcome.Switch, decision.Outcome);
        Assert.Equal(Language.Hebrew, decision.Language);
        Assert.InRange(decision.Confidence, 0.70, 0.72);
    }

    [Fact]
    public void No_messages_and_no_default_changes_nothing()
    {
        var decision = Decide(Request());

        Assert.Equal(DecisionOutcome.Suppressed, decision.Outcome);
        Assert.Equal(DecisionBlocker.NoSignal, decision.Blocker);
    }

    // --- Guard 6: hysteresis -----------------------------------------------------------------

    [Fact]
    public void A_second_switch_inside_the_hysteresis_window_is_refused()
    {
        var first = Decide(Request([Outgoing(Language.Hebrew, 30)], currentLayout: Language.English));
        Assert.Equal(DecisionOutcome.Switch, first.Outcome);

        _clock.Advance(TimeSpan.FromMilliseconds(300));

        var second = Decide(Request([Outgoing(Language.English, 30)], currentLayout: Language.Hebrew));

        Assert.Equal(DecisionOutcome.Suppressed, second.Outcome);
        Assert.Equal(DecisionBlocker.Hysteresis, second.Blocker);
    }

    [Fact]
    public void Switching_resumes_once_the_window_passes()
    {
        Decide(Request([Outgoing(Language.Hebrew, 30)], currentLayout: Language.English));

        _clock.Advance(TimeSpan.FromMilliseconds(800));

        var second = Decide(Request([Outgoing(Language.English, 30)], currentLayout: Language.Hebrew));

        Assert.Equal(DecisionOutcome.Switch, second.Outcome);
        Assert.Equal(Language.English, second.Language);
    }

    [Fact]
    public void Already_correct_language_is_reported_without_consuming_the_hysteresis_window()
    {
        var decision = Decide(Request([Outgoing(Language.Hebrew, 30)], currentLayout: Language.Hebrew));

        Assert.Equal(DecisionOutcome.NoChange, decision.Outcome);
        Assert.Equal(DecisionBlocker.AlreadyCorrect, decision.Blocker);

        // A no-op must not block a genuine switch that follows immediately.
        var next = Decide(Request([Outgoing(Language.English, 30)], currentLayout: Language.Hebrew));
        Assert.Equal(DecisionOutcome.Switch, next.Outcome);
    }

    // --- Manual override cooldown ------------------------------------------------------------

    [Fact]
    public void Auto_switching_backs_off_after_the_user_overrides_us()
    {
        var preference = new ConversationPreference { ManualOverrideAt = _clock.Now };

        var decision = Decide(Request([Outgoing(Language.Hebrew, 30)]), preference: preference);

        Assert.Equal(DecisionBlocker.ManualCooldown, decision.Blocker);
    }

    [Fact]
    public void Auto_switching_resumes_after_the_cooldown()
    {
        var preference = new ConversationPreference { ManualOverrideAt = _clock.Now };
        _clock.Advance(TimeSpan.FromMinutes(6));

        var decision = Decide(
            Request([Outgoing(Language.Hebrew, 30)]) with { ObservedAt = _clock.Now },
            preference: preference);

        Assert.Equal(DecisionOutcome.Switch, decision.Outcome);
    }

    [Fact]
    public void A_pin_still_applies_during_the_cooldown()
    {
        // A pin is the user's own instruction, so the cooldown that exists to avoid arguing with
        // them has nothing to suppress.
        var preference = new ConversationPreference
        {
            Mode = ConversationMode.AlwaysHebrew,
            ManualOverrideAt = _clock.Now,
        };

        var decision = Decide(Request(currentLayout: Language.English), preference: preference);

        Assert.Equal(DecisionOutcome.Switch, decision.Outcome);
        Assert.Equal(Language.Hebrew, decision.Language);
    }

    // --- Precedence (PDR section 5) ----------------------------------------------------------

    [Fact]
    public void A_pin_beats_overwhelming_evidence_for_the_other_language()
    {
        // Acceptance row "override": Always Hebrew wins over English text.
        var decision = Decide(
            Request([Outgoing(Language.English, 200)], currentLayout: Language.English),
            preference: new ConversationPreference { Mode = ConversationMode.AlwaysHebrew });

        Assert.Equal(Language.Hebrew, decision.Language);
        Assert.Equal(DecisionSource.ManualPin, decision.Source);
    }

    [Fact]
    public void Remembered_layout_beats_message_analysis()
    {
        var preference = new ConversationPreference
        {
            LastReliableLanguage = Language.Hebrew,
            UpdatedAt = _clock.Now,
        };

        var decision = Decide(
            Request([Outgoing(Language.English, 100)], currentLayout: Language.English),
            preference: preference);

        Assert.Equal(Language.Hebrew, decision.Language);
        Assert.Equal(DecisionSource.ConversationMemory, decision.Source);
    }

    [Fact]
    public void A_pin_is_never_written_back_as_something_we_learned()
    {
        var decision = Decide(
            Request(currentLayout: Language.English),
            preference: new ConversationPreference { Mode = ConversationMode.AlwaysHebrew });

        Assert.Equal(Language.Unknown, decision.LearnedLanguage);
    }

    [Fact]
    public void The_other_persons_language_is_acted_on_but_never_remembered()
    {
        // The defect a user found after thirty switches: it started well and got worse, and
        // clearing the store fixed it. Incoming messages are a hint good enough to act on once.
        // Written to memory they become permanent, because memory outranks the user's own
        // messages on every later visit - so a conversation where the other person writes Hebrew
        // would answer Hebrew forever, however much English the user typed into it.
        var decision = Decide(Request(
            [Incoming(Language.Hebrew, 100, 0)],
            currentLayout: Language.English));

        Assert.Equal(DecisionOutcome.Switch, decision.Outcome);
        Assert.Equal(DecisionSource.AllMessagesFallback, decision.Source);
        Assert.Equal(Language.Unknown, decision.LearnedLanguage);
    }

    [Fact]
    public void The_users_own_messages_are_still_remembered()
    {
        // The other half of the line. Narrowing what gets recorded is only correct if the evidence
        // that should be recorded still is.
        var decision = Decide(Request(
            [Outgoing(Language.Hebrew, 100, 0)],
            currentLayout: Language.English));

        Assert.Equal(DecisionSource.OutgoingMessages, decision.Source);
        Assert.Equal(Language.Hebrew, decision.LearnedLanguage);
    }

    [Fact]
    public void Memory_written_only_from_the_user_cannot_argue_with_the_user()
    {
        // The end-to-end shape of the bug, as a single test: act on the other person's language,
        // record nothing, and the next visit is still free to read what the user actually writes.
        var incoming = Decide(Request(
            [Incoming(Language.Hebrew, 100, 0)],
            currentLayout: Language.English));

        var remembered = incoming.LearnedLanguage == Language.Unknown
            ? null
            : new ConversationPreference
            {
                Mode = ConversationMode.Auto,
                LastReliableLanguage = incoming.LearnedLanguage,
                UpdatedAt = _clock.Now,
            };

        // Past the hysteresis window, so what follows is the precedence being tested and not the
        // rate limit answering for it.
        _clock.Advance(TimeSpan.FromSeconds(5));

        var next = Decide(
            Request([Outgoing(Language.English, 100, 0)], currentLayout: Language.Hebrew),
            preference: remembered);

        Assert.Equal(Language.English, next.Language);
        Assert.Equal(DecisionSource.OutgoingMessages, next.Source);
    }

    [Fact]
    public void Stale_memory_stops_outranking_fresh_analysis()
    {
        // A conversation dormant for months may have changed language entirely.
        var preference = new ConversationPreference
        {
            LastReliableLanguage = Language.Hebrew,
            UpdatedAt = _clock.Now - TimeSpan.FromDays(120),
        };

        var decision = Decide(
            Request([Outgoing(Language.English, 100)], currentLayout: Language.Hebrew),
            preference: preference);

        Assert.Equal(Language.English, decision.Language);
        Assert.Equal(DecisionSource.OutgoingMessages, decision.Source);
    }

    [Fact]
    public void Outgoing_messages_decide_when_the_other_side_writes_another_language()
    {
        // Acceptance row "הצד השני בשפה אחרת": incoming Hebrew, outgoing English => EN.
        var decision = Decide(Request(
            [
                Outgoing(Language.English, 30, 0),
                Incoming(Language.Hebrew, 200, 1),
            ],
            currentLayout: Language.Hebrew));

        Assert.Equal(DecisionOutcome.Switch, decision.Outcome);
        Assert.Equal(Language.English, decision.Language);
        Assert.Equal(DecisionSource.OutgoingMessages, decision.Source);
    }

    [Fact]
    public void Incoming_only_conversations_need_a_wider_margin_before_they_count()
    {
        // With nothing of the user's own to go on, the other person's language is a hint at best,
        // so the fallback path demands more than the ordinary threshold.
        var decision = Decide(Request(
            [Incoming(Language.Hebrew, 100, 0)],
            currentLayout: Language.English));

        Assert.Equal(DecisionOutcome.Switch, decision.Outcome);
        Assert.Equal(DecisionSource.AllMessagesFallback, decision.Source);
    }

    [Fact]
    public void A_marginal_incoming_only_conversation_is_left_alone()
    {
        var marginal = new MessageObservation(
            MessageDirection.Incoming,
            MessageStats.From((Language.Hebrew, 75), (Language.English, 25)),
            0);

        var decision = Decide(Request([marginal], currentLayout: Language.English));

        // 0.75 clears the ordinary 0.70 bar but not the 0.85 the fallback path requires.
        Assert.Equal(DecisionOutcome.Suppressed, decision.Outcome);
    }

    [Fact]
    public void A_suppressed_decision_still_says_what_it_looked_at()
    {
        // "Read nine messages and could not choose" and "saw nothing at all" are different answers
        // to the user, and to anyone debugging. Dropping the source and confidence on the way out
        // collapsed them into one: the popup and the log both reported source None at confidence
        // zero for a conversation that had been read in full.
        var mixed = new MessageObservation(
            MessageDirection.Outgoing,
            MessageStats.From((Language.Hebrew, 50), (Language.English, 50)),
            0);

        var decision = Decide(Request([mixed], currentLayout: Language.English));

        Assert.Equal(DecisionOutcome.Suppressed, decision.Outcome);
        Assert.Equal(DecisionBlocker.LowConfidence, decision.Blocker);
        Assert.Equal(DecisionSource.OutgoingMessages, decision.Source);
    }

    [Fact]
    public void A_conversation_with_nothing_in_it_is_reported_as_having_nothing_in_it()
    {
        // The other side of the same line: no messages must still report NoSignal, or the
        // distinction the test above exists to preserve is worthless.
        var decision = Decide(Request([], currentLayout: Language.English));

        Assert.Equal(DecisionOutcome.Suppressed, decision.Outcome);
        Assert.Equal(DecisionBlocker.NoSignal, decision.Blocker);
        Assert.Equal(DecisionSource.None, decision.Source);
    }

    [Fact]
    public void The_global_default_applies_only_when_nothing_else_says_anything()
    {
        var settings = Settings.Default with { DefaultLanguage = Language.Hebrew };

        var withNothing = Decide(Request(currentLayout: Language.English), settings);
        Assert.Equal(DecisionSource.GlobalDefault, withNothing.Source);
        Assert.Equal(Language.Hebrew, withNothing.Language);

        // Once there are messages, even unusable ones, the default steps aside rather than
        // overriding a conversation the engine simply could not read.
        var engine = new DecisionEngine(new TestClock());
        var withUnusable = engine.Decide(
            Request([Outgoing(Language.English, 2)], currentLayout: Language.English),
            settings, null, null);

        Assert.NotEqual(DecisionSource.GlobalDefault, withUnusable.Source);
    }

    // --- Settings ----------------------------------------------------------------------------

    [Fact]
    public void A_stricter_confidence_threshold_is_honoured()
    {
        var strict = Settings.Default with { ConfidenceThreshold = 0.95 };

        var decision = Decide(
            Request([
                Outgoing(Language.Hebrew, 80, 0),
                Outgoing(Language.English, 20, 1),
            ], currentLayout: Language.English),
            strict);

        Assert.Equal(DecisionOutcome.Suppressed, decision.Outcome);
        Assert.Equal(DecisionBlocker.LowConfidence, decision.Blocker);
    }

    [Fact]
    public void Noting_a_manual_change_resets_hysteresis_so_we_do_not_undo_it()
    {
        _engine.NoteManualChange(Language.English);

        var decision = Decide(Request([Outgoing(Language.Hebrew, 30)], currentLayout: Language.English));

        Assert.Equal(DecisionBlocker.Hysteresis, decision.Blocker);
    }

    // --- Ordering ----------------------------------------------------------------------------

    [Fact]
    public void Hard_blocks_are_checked_before_anything_is_analysed()
    {
        // Disabled beats paused beats focus beats typing. Asserting the order keeps the reported
        // blocker meaningful in the popup rather than whichever check happened to run first.
        var request = Request([Outgoing(Language.Hebrew, 30)], composerEmpty: false, foreground: false);

        Assert.Equal(DecisionBlocker.Disabled,
            Decide(request, Settings.Default with { Enabled = false }, site: new SiteState { Paused = true }).Blocker);

        Assert.Equal(DecisionBlocker.SitePaused,
            Decide(request, site: new SiteState { Paused = true }).Blocker);

        Assert.Equal(DecisionBlocker.NotForeground, Decide(request).Blocker);
    }
}
