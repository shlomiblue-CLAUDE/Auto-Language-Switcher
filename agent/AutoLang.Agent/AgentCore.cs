using System.Text.Json;
using AutoLang.Core;

namespace AutoLang.Agent;

/// <summary>
/// The brain. One instance, one process, shared by every signal source.
///
/// PDR section 7 puts this in the browser's service worker. It lives here instead for two reasons
/// that only get truer over time: MV3 service workers are evicted aggressively - section 18 lists
/// "Service Worker נרדם" as a live risk, which a resident process simply does not have - and a
/// desktop signal source added later must reach the same engine and the same per-conversation
/// memory without a second implementation of the rules in another language.
/// </summary>
public sealed class AgentCore
{
    /// <summary>
    /// How stale an observation may be before it is ignored.
    ///
    /// PDR section 9 asks for this and the reason is concrete: the user can switch windows in the
    /// gap between the page reading the DOM and the Agent acting on it. Acting on a second-old
    /// observation means changing the layout of whatever they moved to.
    /// </summary>
    private static readonly TimeSpan MaxSignalAge = TimeSpan.FromSeconds(1);

    public const string AgentVersion = "0.1.0";

    private readonly ConversationStore _store;
    private readonly DecisionEngine _engine;
    private readonly IKeyboardLayoutService _layouts;
    private readonly IClock _clock;
    private readonly Action<string> _log;
    private readonly object _gate = new();

    private DecisionMessage? _lastDecision;

    public AgentCore(
        ConversationStore store,
        IKeyboardLayoutService layouts,
        DecisionEngine? engine = null,
        IClock? clock = null,
        Action<string>? log = null)
    {
        _store = store;
        _layouts = layouts;
        _clock = clock ?? SystemClock.Instance;
        _engine = engine ?? new DecisionEngine(_clock);
        _log = log ?? (_ => { });
    }

    /// <summary>Routes one inbound message and returns the reply, or null when none is warranted.</summary>
    public string? Handle(string json)
    {
        WireEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<WireEnvelope>(json, Wire.Json);
        }
        catch (JsonException ex)
        {
            return Serialize(new ErrorMessage { Code = ErrorCodes.BadMessage, Message = ex.Message });
        }

        if (envelope is null)
            return Serialize(new ErrorMessage { Code = ErrorCodes.BadMessage, Message = "Empty message." });

        if (envelope.ProtocolVersion != 0 && envelope.ProtocolVersion != Wire.ProtocolVersion)
        {
            return Serialize(new ErrorMessage
            {
                Code = ErrorCodes.UnsupportedProtocol,
                Message = $"Agent speaks protocol {Wire.ProtocolVersion}, message declared {envelope.ProtocolVersion}."
            });
        }

        try
        {
            return envelope.Kind switch
            {
                WireMessageKind.Signal => HandleSignal(Require<SignalMessage>(json)),
                WireMessageKind.Health => HandleHealth(Require<HealthMessage>(json)),
                WireMessageKind.Command => HandleCommand(Require<CommandMessage>(json)),
                WireMessageKind.Query => HandleQuery(Require<QueryMessage>(json)),
                _ => Serialize(new ErrorMessage { Code = ErrorCodes.BadMessage, Message = $"Unknown type '{envelope.Type}'." })
            };
        }
        catch (JsonException ex)
        {
            return Serialize(new ErrorMessage { Code = ErrorCodes.BadMessage, Message = ex.Message });
        }
    }

    /// <summary>
    /// A conversation key must look like what the content script produces: 32 lowercase hex
    /// characters from a salted SHA-256.
    ///
    /// Until this existed, the privacy guarantee rested entirely on the content script choosing to
    /// hash. Anything else speaking this protocol - a second adapter, a future desktop source, a
    /// test harness - could have written a phone number straight into conversations.json, and one
    /// did: a test using a plain Hebrew string as a key left it sitting in the real store. Checking
    /// the shape here makes the guarantee structural instead of a matter of good behaviour
    /// elsewhere.
    /// </summary>
    private static bool IsHashedKey(string key)
    {
        if (key.Length != 32) return false;

        foreach (char c in key)
        {
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!hex) return false;
        }
        return true;
    }

    private string? HandleSignal(SignalMessage signal)
    {
        if (string.IsNullOrWhiteSpace(signal.ConversationKey))
            return Serialize(new ErrorMessage { Code = ErrorCodes.BadMessage, Message = "Missing conversationKey." });

        if (!IsHashedKey(signal.ConversationKey))
        {
            // Deliberately does not echo the key back. Refusing to store something identifying and
            // then putting it in an error message would defeat the point.
            _log("rejected a signal whose conversationKey is not a salted hash");
            return Serialize(new ErrorMessage
            {
                Code = ErrorCodes.BadMessage,
                Message = "conversationKey must be 32 lowercase hex characters. Raw identifiers are not accepted."
            });
        }

        var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(signal.ObservedAt);
        var now = _clock.Now;

        if (signal.ObservedAt > 0 && now - observedAt > MaxSignalAge)
        {
            _log($"ignoring stale signal, age {(now - observedAt).TotalMilliseconds:F0}ms");
            return Serialize(new ErrorMessage { Code = ErrorCodes.StaleEvent, Message = "Observation too old to act on." });
        }

        // Foreground state and current layout are read from Windows, never taken from the page.
        // A page cannot know either, and a compromised one could lie about both.
        var request = new DecisionRequest
        {
            ConversationKey = signal.ConversationKey,
            Site = signal.Site,
            Messages = signal.Messages.Select(m => m.ToObservation()).ToList(),
            ComposerEmpty = signal.ComposerEmpty,
            BrowserIsForeground = _layouts.IsBrowserForeground(),
            CurrentLayout = _layouts.CurrentLayout(),
            ObservedAt = now,
        };

        lock (_gate)
        {
            var preference = _store.GetConversation(signal.ConversationKey);
            var site = _store.GetSite(signal.Site);
            var decision = _engine.Decide(request, _store.Settings, preference, site);

            if (decision.LearnedLanguage != Language.Unknown)
                _store.RememberLanguage(signal.ConversationKey, decision.LearnedLanguage);

            _store.RecordDecision(signal.Site, decision);

            bool applied = false;
            string? errorCode = null;

            if (decision.ShouldSwitch)
            {
                var result = _layouts.Switch(decision.Language);
                applied = result.Success;
                errorCode = result.ErrorCode;

                _log(applied
                    ? $"switched to {decision.Language} in {result.ElapsedMs}ms ({decision.Source}, conf {decision.Confidence:F2})"
                    : $"switch to {decision.Language} failed: {result.ErrorCode}");
            }

            var message = ToMessage(signal.ConversationKey, decision, applied, errorCode);
            _lastDecision = message;
            return Serialize(message);
        }
    }

    private string? HandleHealth(HealthMessage health)
    {
        if (!health.Healthy)
        {
            _log($"adapter unhealthy on {health.Site} v{health.AdapterVersion}: missing [{string.Join(", ", health.Missing)}]");

            lock (_gate)
            {
                var existing = _store.GetSite(health.Site) ?? new SiteState();
                _store.SaveSite(health.Site, existing with { AdapterVersion = health.AdapterVersion });
            }
        }
        return null;
    }

    private string? HandleCommand(CommandMessage command)
    {
        lock (_gate)
        {
            switch (command.Command)
            {
                case "setMode" when command.ConversationKey is { } key:
                    if (!IsHashedKey(key))
                        return Serialize(new ErrorMessage { Code = ErrorCodes.BadMessage, Message = "conversationKey must be a salted hash." });
                    if (!Enum.TryParse<ConversationMode>(command.Mode, ignoreCase: true, out var mode))
                        return Serialize(new ErrorMessage { Code = ErrorCodes.BadMessage, Message = $"Unknown mode '{command.Mode}'." });
                    _store.SetMode(key, mode);
                    break;

                case "pauseSite" when command.Site is { } site:
                    _store.SaveSite(site, (_store.GetSite(site) ?? new SiteState()) with { Paused = true });
                    break;

                case "resumeSite" when command.Site is { } resumeSite:
                    _store.SaveSite(resumeSite, (_store.GetSite(resumeSite) ?? new SiteState()) with { Paused = false });
                    break;

                case "setEnabled" when command.Enabled is { } enabled:
                    _store.SaveSettings(_store.Settings with { Enabled = enabled });
                    break;

                case "setSettings":
                {
                    // Applied as one write. A threshold saved without its language, or the reverse,
                    // would leave the product in a state the user never chose.
                    var settings = _store.Settings;

                    if (command.Enabled is { } isEnabled)
                        settings = settings with { Enabled = isEnabled };

                    if (command.DefaultLanguage is { } tag)
                        settings = settings with { DefaultLanguage = LanguageExtensions.FromTag(tag) };

                    if (command.ConfidenceThreshold is { } threshold)
                    {
                        // Clamped rather than rejected. A slider cannot send nonsense, but a
                        // hand-written message could, and a threshold of 0 would switch on noise.
                        settings = settings with { ConfidenceThreshold = Math.Clamp(threshold, 0.5, 0.95) };
                    }

                    if (command.ShowIndicator is { } showIndicator)
                        settings = settings with { ShowIndicator = showIndicator };

                    _store.SaveSettings(settings);
                    break;
                }

                case "noteManualChange" when command.ConversationKey is { } manualKey && IsHashedKey(manualKey):
                {
                    // The user overrode us. Record it, start the cooldown, and reset hysteresis so
                    // the product cannot immediately undo what they just did.
                    var language = LanguageExtensions.FromTag(command.LanguageTag ?? "");
                    _store.NoteManualOverride(manualKey, language);
                    _engine.NoteManualChange(language);
                    break;
                }

                case "clearData":
                    _store.ClearAll();
                    break;

                default:
                    return Serialize(new ErrorMessage { Code = ErrorCodes.BadMessage, Message = $"Unknown or incomplete command '{command.Command}'." });
            }
        }

        return HandleQuery(new QueryMessage { ConversationKey = command.ConversationKey, Site = command.Site });
    }

    private string HandleQuery(QueryMessage query)
    {
        lock (_gate)
        {
            var preference = query.ConversationKey is { } key ? _store.GetConversation(key) : null;
            var site = query.Site is { } origin ? _store.GetSite(origin) : null;

            return Serialize(new StateMessage
            {
                AgentVersion = AgentVersion,
                Enabled = _store.Settings.Enabled,
                CurrentLayout = _layouts.CurrentLayout().ToTag(),
                ConversationMode = (preference?.Mode ?? ConversationMode.Auto).ToString(),
                RememberedLanguage = (preference?.LastReliableLanguage ?? Language.Unknown).ToTag(),
                SitePaused = site?.Paused ?? false,
                AvailableLayouts = _layouts.AvailableLanguages().Select(l => l.ToTag()).ToList(),
                DefaultLanguage = _store.Settings.DefaultLanguage.ToTag(),
                ConfidenceThreshold = _store.Settings.ConfidenceThreshold,
                ShowIndicator = _store.Settings.ShowIndicator,
                LastDecision = _lastDecision,
            });
        }
    }

    private static DecisionMessage ToMessage(string conversationKey, Decision decision, bool applied, string? errorCode) => new()
    {
        ConversationKey = conversationKey,
        Language = decision.Language.ToTag(),
        Confidence = decision.Confidence,
        Outcome = decision.Outcome.ToString(),
        Source = decision.Source.ToString(),
        Blocker = decision.Blocker.ToString(),
        Applied = applied,
        ErrorCode = errorCode,
    };

    private static T Require<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Wire.Json) ?? throw new JsonException($"Could not read a {typeof(T).Name}.");

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Wire.Json);
}
