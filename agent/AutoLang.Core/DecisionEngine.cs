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
    /// The layout this engine put in place, and the conversation it put it there for.
    ///
    /// Exists to stop the product learning from itself. See the typing guard below.
    ///
    /// The conversation half was missing and it mattered: this is single state on an engine shared
    /// by every conversation, so imposing Hebrew in one made Hebrew look like our own guess
    /// *everywhere*. A live log caught it on the desktop source - the user had Hebrew set in
    /// WhatsApp, the engine had put Hebrew into Claude a minute earlier, and WhatsApp was therefore
    /// unable to learn the language sitting in front of it. The rule is about a conversation
    /// confirming its own guess, so it has to know which conversation.
    /// </summary>
    private Language _layoutWeImposed = Language.Unknown;
    private string? _imposedIn;

    /// <summary>True when the layout in use here is one this engine chose for *this* conversation.</summary>
    private bool IsOurOwnGuess(DecisionRequest request) =>
        _imposedIn == request.ConversationKey && request.CurrentLayout == _layoutWeImposed;

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
    ///
    /// Both halves were still not enough, and a month of live log says how badly. "Between two
    /// observations" has to mean *consecutive* observations of a conversation nobody looked away
    /// from - and the browser side had no way to know it had looked away. The user leaves Chrome
    /// for another application, types English there, comes back to a Hebrew chat: the next signal
    /// carries the same conversation key and a different layout, which is this condition exactly,
    /// so arriving home was recorded as a deliberate override. 183 of 310 overrides in the log were
    /// that, and each one cost three things - the switch the user wanted, their conversation's
    /// remembered language (295 of 310 were overwritten with the layout they merely arrived with),
    /// and five minutes of cooldown in which the product would not correct itself.
    ///
    /// <see cref="NoteLookedAway"/> is what makes "nobody looked away" true. ForegroundWatcher
    /// already states the principle for the desktop - arriving at a window with a different layout
    /// is where the user came from, not a choice they made here - and this is that principle
    /// finally reaching the browser, which never had it.
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
        //
        // An observation from the background is also proof that we are no longer watching the place
        // it came from, so the override baseline goes with it. This returns before the baseline is
        // written below, which used to mean a background stretch left a stale one standing: the log
        // has 14 overrides detected across a gap the product could see it had not been looking at.
        if (!request.TargetIsForeground)
        {
            NoteLookedAway();
            return Suppressed(DecisionBlocker.NotForeground);
        }

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
            var layoutIsOurOwnGuess = IsOurOwnGuess(request);

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
        // stop us arguing with them.
        //
        // So does the cooldown's own subject. Switching *to* the language the override established
        // is not arguing with the user, it is putting their choice back - and that is the only
        // thing the cooldown was measurably doing. All 2835 cooldown suppressions in a month of
        // live log had a remembered language; in 2467 the layout already matched it, so nothing
        // would have happened anyway, and in the other 368 the layout contradicted it and a restore
        // was blocked. Not one of them stopped a switch to anything else, and the reason is
        // structural rather than lucky: memory outranks analysis, an override always writes memory,
        // so while the cooldown is running the only language the engine can arrive at is the one the
        // user picked.
        //
        // The user's report, after setting Hebrew in a document and coming back to it: "when I come
        // back from English to this document in Hebrew it stays English."
        //
        // The guard stays for the case it was written for, which this does not cover: a language
        // that is not theirs. Memory older than its TTL falls through to message analysis, and an
        // override that named no language leaves nothing to compare against - both still wait.
        var restoringWhatTheUserChose =
            preference?.LastReliableLanguage is { } chosen
            && chosen != Language.Unknown
            && language == chosen;

        if (source != DecisionSource.ManualPin
            && !restoringWhatTheUserChose
            && IsInManualCooldown(preference, settings, request.ObservedAt))
        {
            return Suppressed(DecisionBlocker.ManualCooldown);
        }

        // Nothing to go on, and a layout in use that we did not put there.
        //
        // The normal case wherever there is no text to read: an application outside the browser
        // offers none, no direction and no composer, so the typing guard never fires and the
        // manual-change rule needs a transition it may never see. A user who sets Hebrew in Slack
        // and simply stays there was producing no evidence at all - a live log showed `memory=none`
        // on the same window once a second for as long as they sat in it.
        //
        // It is not only outside the browser, which is what this comment used to say, and saying it
        // kept the rule from reaching the case it was named after. A spreadsheet draws its grid on
        // a canvas: the letters the user types are rendered, never written into the DOM, and what
        // is left to sample is thirteen visible characters of Google's own interface. The adapter
        // measures that and reports it - see CanReadContext - but every browser signal used to
        // claim it had read the page, so a sheet came back NoSignal and the product did nothing.
        // 103 times in a month on this machine, with `memory=none` in 91% of every decision made
        // there. The user's words: "it does not detect the written language and does not switch."
        //
        // A site with an adapter of its own is excluded at the source, by that adapter answering
        // true. WhatsApp finding no messages means the chat is empty, which is evidence; the same
        // silence on a canvas means nothing was visible. Treating them alike would have started
        // 163 of these on WhatsApp alone.
        //
        // Only when there is nothing remembered yet. Once a language is stored, only a deliberate
        // change replaces it, so the layout somebody happened to arrive with cannot overwrite what
        // they chose on purpose.
        if (language == Language.Unknown
            && !request.CanReadContext
            && preference?.LastReliableLanguage is null or Language.Unknown
            && request.CurrentLayout != Language.Unknown
            && !IsOurOwnGuess(request))
        {
            return new Decision
            {
                Outcome = DecisionOutcome.NoChange,
                Language = request.CurrentLayout,
                Confidence = 1.0,
                Source = DecisionSource.ConversationMemory,
                Blocker = DecisionBlocker.AlreadyCorrect,
                LearnedLanguage = request.CurrentLayout,
            };
        }

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
        _imposedIn = request.ConversationKey;

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

        // 2. What they were typing with last time, while it is still fresh - and while this key is
        //    still believable as one conversation.
        //
        //    Memory outranks analysis because it is the user's own keyboard rather than an
        //    inference, and that reasoning needs the key to name one thing. When it names several,
        //    "what they were typing with last time" is what they were typing in a *different*
        //    conversation, and replaying it is how an application whose window title never changes
        //    drags somebody into the wrong language on every switch. See
        //    ConversationPreference.CoversSeveralConversations for what shows that, and what it
        //    cost on this machine before it did.
        //
        //    Nothing is forgotten; it simply stops being replayed. On a page the engine falls
        //    through to the evidence actually in front of it, which is a worse source and a better
        //    answer. In an application there is no evidence, so it does nothing - a keystroke
        //    against a deleted line, which is the trade stated at the top of this file.
        if (preference is { LastReliableLanguage: not Language.Unknown }
            && !preference.CoversSeveralConversations
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
    /// The user's attention moved somewhere else, so stop treating the layout we last saw as the
    /// baseline for noticing a manual change.
    ///
    /// Called when the foreground window changes, which is the only moment at which a layout can
    /// move without either us or the user having touched it *here*: they went to another
    /// application, the layout there was different, and they came back. That is not an override.
    /// It was being recorded as one more than half the time - see <see cref="_lastObservedIn"/> for
    /// what it cost - and the user's report that opened the diagnosis was the symptom in one line:
    /// coming back to the browser from somewhere English, in a Hebrew conversation, and not being
    /// switched to Hebrew.
    ///
    /// Only the baseline is dropped. Hysteresis, the cooldown and the anti-echo record of what we
    /// imposed all survive looking away, because none of them is a statement about one window.
    ///
    /// Deliberately not a timer. The first version of this fix aged the baseline out after fifteen
    /// seconds, and the log killed it: on chatgpt.com 37% of consecutive looks at one conversation
    /// are already more than fifteen seconds apart, so a window wide enough to keep real overrides
    /// there was far too wide to stop the false ones here. Asking Windows what the user is looking
    /// at needs no threshold and is right in both places.
    /// </summary>
    public void NoteLookedAway()
    {
        _lastObservedIn = null;
        _lastObservedLayout = Language.Unknown;
    }

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
        _imposedIn = null;
    }

    public Language LastSwitchLanguage => _lastSwitchLanguage;
}
