namespace AutoLang.Core;

/// <summary>
/// Decides whether to switch the keyboard, and to what.
///
/// The engine's default answer is "do nothing". Every guard below exists to keep it that way,
/// because the failure modes are asymmetric: a missed switch costs one keypress, while a wrong
/// switch mid-sentence costs a deleted line and the user's trust. PDR section 12 is implemented
/// here in full, and the order of the checks is itself part of the design.
///
/// State kept: when we last switched, and to what. That is all hysteresis needs. Everything else
/// arrives per call, which keeps the engine deterministic under a controllable clock.
/// </summary>
public sealed class DecisionEngine
{
    private readonly IClock _clock;
    private readonly LanguageDetector _detector;

    private DateTimeOffset _lastSwitchAt = DateTimeOffset.MinValue;
    private Language _lastSwitchLanguage = Language.Unknown;

    /// <summary>
    /// The layout this engine put in place and that the user has not touched since.
    ///
    /// Exists to stop the product learning from itself. See the typing guard below.
    /// </summary>
    private Language _layoutWeImposed = Language.Unknown;

    /// <summary>
    /// The layout in effect the last time we looked, and the conversation we were looking at.
    ///
    /// This is how a manual change is noticed at all. Between two observations of the *same*
    /// conversation the only thing that can move the layout is us or the user, and we know when it
    /// was us - so a different value that we did not set is the user reaching for Alt+Shift.
    ///
    /// Both halves are needed. Comparing layouts alone attributes any change to whatever
    /// conversation happens to be in view, so moving between two conversations while the layout
    /// differs would be read as an override and put the arriving conversation into a five minute
    /// cooldown it never earned. A stability test that walks several conversations caught that.
    /// </summary>
    private string? _lastObservedIn;

    private Language _lastObservedLayout = Language.Unknown;

    public DecisionEngine(IClock? clock = null, LanguageDetector? detector = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _detector = detector ?? new LanguageDetector();
    }

    public Decision Decide(
        DecisionRequest request,
        Settings settings,
        ConversationPreference? preference,
        SiteState? siteState)
    {
        // --- Hard blocks. Nothing below matters if the product is not supposed to act at all. ---

        if (!settings.Enabled)
            return Suppressed(DecisionBlocker.Disabled);

        if (siteState?.Paused == true)
            return Suppressed(DecisionBlocker.SitePaused);

        // Windows does not enforce this for us; the spike proved a background window switches just
        // as readily as a foreground one. If we do not refuse here, nothing will.
        if (!request.BrowserIsForeground)
            return Suppressed(DecisionBlocker.NotForeground);

        // --- The user changed the layout themselves. ---
        //
        // PDR section 18 asks the product to back off when this happens, the store listing promises
        // it in those words, and NoteManualChange below has always implemented it. Nothing ever
        // called it: a manual change was only ever noticed through the typing guard, which needs a
        // non-empty composer. That covers a chat, where the user types into a box we can read.
        //
        // It does not cover an application that keeps its text somewhere we cannot see. Google
        // Sheets draws its grid on a canvas and never reports a composer as occupied, so nothing
        // was ever learned there, memory never formed, and the weak fallback - reading a few
        // hundred letters of Google's own English interface at full confidence - won every round.
        // A log of one spreadsheet shows the user setting Hebrew by hand and the product dragging
        // them back to English four times over five hours. A keyboard switcher that overrules the
        // keyboard is worse than one that does nothing.
        //
        // A layout that differs from the one seen last, which we did not set, can only be the
        // user - this is reached with the browser in front. That is not merely a reason to stop:
        // it is the clearest statement of intent available about this conversation, so it is
        // learned as well.
        //
        // Compared against the layout last *seen*, not the last one imposed, and that distinction
        // is the whole check. A layout is only imposed by switching; when a conversation is already
        // on the right one the engine reports AlreadyCorrect and imposes nothing. Keyed on the
        // imposed layout, a user who moved it by hand from an already-correct state would not be
        // noticed at all - the same defect, entering by a different door. Writing the test against
        // that assumption is what found it.
        var userMovedTheLayout =
            _lastObservedIn == request.ConversationKey
            && _lastObservedLayout != Language.Unknown
            && request.CurrentLayout != Language.Unknown
            && request.CurrentLayout != _lastObservedLayout;

        _lastObservedIn = request.ConversationKey;
        _lastObservedLayout = request.CurrentLayout;

        if (userMovedTheLayout)
        {
            NoteManualChange(request.CurrentLayout);

            return new Decision
            {
                Outcome = DecisionOutcome.Suppressed,
                Blocker = DecisionBlocker.ManualChange,
                LearnedLanguage = request.CurrentLayout,
                UserOverrode = true,
            };
        }

        // --- The typing guard, which is also the moment we learn. ---

        if (!request.ComposerEmpty)
        {
            // The user is typing right now, with a layout we can read. That is the strongest
            // evidence there is about how they write in this conversation - stronger than parsing
            // their sent words - so the guard that blocks the switch also records the truth.
            //
            // Unless the layout is our own. If we set it and the user has not touched it since,
            // then "the layout in use while they type" is our last guess, not their choice, and
            // recording it teaches the product what it already believed. One wrong switch then
            // becomes permanent, and a log of a real session showed a single conversation's
            // memory flipping Hebrew, English, Hebrew inside thirty seconds on exactly this path.
            //
            // A user who disagrees with a switch fixes it themselves, which clears this, and the
            // very next keystroke is learned normally. Nothing is lost but the echo.
            var layoutIsOurOwnGuess = request.CurrentLayout == _layoutWeImposed;

            return new Decision
            {
                Outcome = DecisionOutcome.Suppressed,
                Blocker = DecisionBlocker.UserTyping,
                LearnedLanguage = layoutIsOurOwnGuess ? Language.Unknown : request.CurrentLayout,
            };
        }

        // --- Choose a target. ---

        var (language, confidence, source) = ChooseLanguage(request, settings, preference);

        // A language the user has switched off is not a candidate, whatever the evidence said.
        // Applied here rather than inside each source so there is one place to read: the settings
        // filter what may be applied, never what may be believed.
        if (language != Language.Unknown && !IsEnabled(language, settings))
            return Suppressed(DecisionBlocker.LanguageDisabled, source, confidence);

        // A pin is the user's explicit instruction, so it outranks the cooldown that exists to
        // stop us arguing with them. Every other source must wait the cooldown out.
        if (source != DecisionSource.ManualPin && IsInManualCooldown(preference, settings, request.ObservedAt))
            return Suppressed(DecisionBlocker.ManualCooldown);

        if (language == Language.Unknown)
        {
            // Carrying the source and confidence through matters. Without them a suppressed
            // decision reports source None at confidence zero, which reads as "nothing was
            // observed" when what actually happened is "nine messages were read and they did not
            // agree". The popup shows this to the user as the reason, and the log showed it to me
            // while I was diagnosing exactly that case.
            return Suppressed(
                source == DecisionSource.None ? DecisionBlocker.NoSignal : DecisionBlocker.LowConfidence,
                source,
                confidence);
        }

        if (language == request.CurrentLayout)
        {
            return new Decision
            {
                Outcome = DecisionOutcome.NoChange,
                Language = language,
                Confidence = confidence,
                Source = source,
                Blocker = DecisionBlocker.AlreadyCorrect,
                LearnedLanguage = LearnedFrom(source, language),
            };
        }

        // --- Hysteresis, checked last so it only ever suppresses a switch we would really make. ---

        var now = _clock.Now;
        if (now - _lastSwitchAt < settings.HysteresisWindow)
            return Suppressed(DecisionBlocker.Hysteresis);

        _lastSwitchAt = now;
        _lastSwitchLanguage = language;
        _layoutWeImposed = language;

        // Our own switch, recorded as observed. Without this the very next signal would see the
        // layout differ from the last observation and blame the user for what we just did.
        _lastObservedLayout = language;

        return new Decision
        {
            Outcome = DecisionOutcome.Switch,
            Language = language,
            Confidence = confidence,
            Source = source,
            LearnedLanguage = LearnedFrom(source, language),
        };
    }

    /// <summary>
    /// PDR section 5 precedence.
    ///
    /// Memory outranks analysis because it is a different kind of evidence: what the user's
    /// keyboard was actually set to while they typed, not what we inferred from their words. It
    /// cannot ossify, because every time they type it is rewritten - so a conversation that drifts
    /// from English to Hebrew corrects itself the first time the user types Hebrew in it.
    /// </summary>
    private (Language Language, double Confidence, DecisionSource Source) ChooseLanguage(
        DecisionRequest request,
        Settings settings,
        ConversationPreference? preference)
    {
        // 1. Manual pin.
        if (preference?.Pin is { } pinned)
            return (pinned, 1.0, DecisionSource.ManualPin);

        // 2. What they were typing with last time, while it is still fresh.
        if (preference is { LastReliableLanguage: not Language.Unknown }
            && request.ObservedAt - preference.UpdatedAt <= settings.MemoryTtl)
        {
            return (preference.LastReliableLanguage, 1.0, DecisionSource.ConversationMemory);
        }

        var detector = DetectorFor(settings);

        // 3. Their own recent messages.
        var outgoing = request.Messages
            .Where(m => m.Direction == MessageDirection.Outgoing)
            .Select(m => m.Stats)
            .ToList();

        var outgoingResult = detector.Score(outgoing);
        if (outgoingResult.IsActionable)
            return (outgoingResult.Winner, outgoingResult.Confidence, DecisionSource.OutgoingMessages);

        // 4. Everything visible, as a weak fallback. Incoming messages are the other person's
        //    language, so this can only ever be a hint - hence the raised bar below.
        var all = request.Messages.Select(m => m.Stats).ToList();
        var allResult = detector.Score(all);
        if (allResult.IsActionable && allResult.Confidence >= FallbackConfidence(settings))
            return (allResult.Winner, allResult.Confidence, DecisionSource.AllMessagesFallback);

        // 5. Global default, only when nothing else said anything.
        if (settings.DefaultLanguage != Language.Unknown && request.Messages.Count == 0)
            return (settings.DefaultLanguage, 0.5, DecisionSource.GlobalDefault);

        var source = outgoingResult.Reason == DetectionReason.NoMessages && all.Count == 0
            ? DecisionSource.None
            : DecisionSource.OutgoingMessages;

        return (Language.Unknown, outgoingResult.Confidence, source);
    }

    /// <summary>Empty means every language Windows has, which is what a first run wants.</summary>
    private static bool IsEnabled(Language language, Settings settings) =>
        settings.EnabledLanguages.Count == 0 || settings.EnabledLanguages.Contains(language);

    private LanguageDetector DetectorFor(Settings settings) =>
        Math.Abs(settings.ConfidenceThreshold - DetectionOptions.Default.ConfidenceThreshold) < 0.0001
            ? _detector
            : new LanguageDetector(new DetectionOptions { ConfidenceThreshold = settings.ConfidenceThreshold });

    /// <summary>Incoming messages are weak evidence, so the fallback path demands a wider margin.</summary>
    private static double FallbackConfidence(Settings settings) =>
        Math.Min(0.95, settings.ConfidenceThreshold + 0.15);

    private static bool IsInManualCooldown(ConversationPreference? preference, Settings settings, DateTimeOffset now) =>
        preference?.ManualOverrideAt is { } overriddenAt && now - overriddenAt < settings.ManualCooldown;

    /// <summary>
    /// What may be written back as this conversation's remembered language.
    ///
    /// Only evidence about the user. A pin is an instruction, not an observation. And the
    /// all-messages fallback is the other person's words - a hint worth acting on once, and never
    /// worth recording, because memory outranks the user's own messages on every later visit.
    ///
    /// Recording it was a real defect and it behaved exactly as the priority order predicts. In a
    /// conversation where the other person writes Hebrew and the user answers in English, the
    /// fallback wrote Hebrew into memory, and from then on every visit returned Hebrew at
    /// confidence 1.00 and outranked the English the user was actually typing. It got worse over
    /// time rather than better, and clearing the store fixed it - which is what a user reported,
    /// in those words, after thirty conversation switches.
    ///
    /// The class comment above claims memory is "what the user's keyboard was actually set to
    /// while they typed". This is what makes that true.
    /// </summary>
    private static Language LearnedFrom(DecisionSource source, Language language) =>
        source is DecisionSource.OutgoingMessages ? language : Language.Unknown;

    private static Decision Suppressed(
        DecisionBlocker blocker,
        DecisionSource source = DecisionSource.None,
        double confidence = 0) =>
        new()
        {
            Outcome = DecisionOutcome.Suppressed,
            Blocker = blocker,
            Source = source,
            Confidence = confidence,
        };

    /// <summary>
    /// Records that the user changed the layout themselves. Resets hysteresis, so the product
    /// never immediately undoes what the user just did (PDR section 18, "משתמש החליף ידנית").
    /// </summary>
    public void NoteManualChange(Language language)
    {
        _lastSwitchAt = _clock.Now;
        _lastSwitchLanguage = language;
        _lastObservedLayout = language;

        // The layout is theirs again, so the typing guard may learn from it. This is the release
        // valve that keeps the anti-echo rule above from freezing a conversation on a wrong guess.
        _layoutWeImposed = Language.Unknown;
    }

    public Language LastSwitchLanguage => _lastSwitchLanguage;
}
