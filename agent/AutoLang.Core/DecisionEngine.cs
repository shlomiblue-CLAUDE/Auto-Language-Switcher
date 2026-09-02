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

        // --- The typing guard, which is also the moment we learn. ---

        if (!request.ComposerEmpty)
        {
            // The user is typing right now, with a layout we can read. That is the strongest
            // evidence there is about how they write in this conversation - stronger than parsing
            // their sent words - so the guard that blocks the switch also records the truth.
            return new Decision
            {
                Outcome = DecisionOutcome.Suppressed,
                Blocker = DecisionBlocker.UserTyping,
                LearnedLanguage = request.CurrentLayout,
            };
        }

        // --- Choose a target. ---

        var (language, confidence, source) = ChooseLanguage(request, settings, preference);

        // A pin is the user's explicit instruction, so it outranks the cooldown that exists to
        // stop us arguing with them. Every other source must wait the cooldown out.
        if (source != DecisionSource.ManualPin && IsInManualCooldown(preference, settings, request.ObservedAt))
            return Suppressed(DecisionBlocker.ManualCooldown);

        if (language == Language.Unknown)
            return Suppressed(source == DecisionSource.None ? DecisionBlocker.NoSignal : DecisionBlocker.LowConfidence);

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
        if (preference?.PinnedLanguage is { } pinned)
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

    private LanguageDetector DetectorFor(Settings settings) =>
        Math.Abs(settings.ConfidenceThreshold - DetectionOptions.Default.ConfidenceThreshold) < 0.0001
            ? _detector
            : new LanguageDetector(new DetectionOptions { ConfidenceThreshold = settings.ConfidenceThreshold });

    /// <summary>Incoming messages are weak evidence, so the fallback path demands a wider margin.</summary>
    private static double FallbackConfidence(Settings settings) =>
        Math.Min(0.95, settings.ConfidenceThreshold + 0.15);

    private static bool IsInManualCooldown(ConversationPreference? preference, Settings settings, DateTimeOffset now) =>
        preference?.ManualOverrideAt is { } overriddenAt && now - overriddenAt < settings.ManualCooldown;

    /// <summary>A pin is not something we learned, so it must not be written back as one.</summary>
    private static Language LearnedFrom(DecisionSource source, Language language) =>
        source is DecisionSource.OutgoingMessages or DecisionSource.AllMessagesFallback
            ? language
            : Language.Unknown;

    private static Decision Suppressed(DecisionBlocker blocker) =>
        new() { Outcome = DecisionOutcome.Suppressed, Blocker = blocker };

    /// <summary>
    /// Records that the user changed the layout themselves. Resets hysteresis, so the product
    /// never immediately undoes what the user just did (PDR section 18, "משתמש החליף ידנית").
    /// </summary>
    public void NoteManualChange(Language language)
    {
        _lastSwitchAt = _clock.Now;
        _lastSwitchLanguage = language;
    }

    public Language LastSwitchLanguage => _lastSwitchLanguage;
}
