using System.Text.Json;
using AutoLang.Core;

namespace AutoLang.Agent.Tests;

public sealed class TestClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// Stands in for Windows. It proves the Agent's routing; it proves nothing about switching, which
/// only the phase 0 spike and the manual acceptance runs can.
/// </summary>
public sealed class FakeLayoutService : IKeyboardLayoutService
{
    public bool BrowserInFront { get; set; } = true;
    public Language Current { get; set; } = Language.English;

    /// <summary>Whatever window the fake claims is in front. Desktop tests move this about.</summary>
    public ForegroundWindow Window { get; set; } = new(new IntPtr(1), "chrome", "a tab");

    /// <summary>What Switch was told to expect, so a test can prove the two are compared.</summary>
    public IntPtr LastExpectedWindow { get; private set; }
    public bool SwitchSucceeds { get; set; } = true;
    public string? FailureCode { get; set; }
    public List<Language> SwitchRequests { get; } = [];
    public List<Language> Available { get; set; } = [Language.Hebrew, Language.English];

    public IReadOnlyList<Language> AvailableLanguages() => Available;
    public bool IsBrowserForeground() => BrowserInFront;
    public ForegroundWindow Foreground() => Window;
    public Language CurrentLayout() => Current;

    public SwitchResult Switch(Language language, IntPtr expectedWindow)
    {
        LastExpectedWindow = expectedWindow;
        SwitchRequests.Add(language);
        if (!SwitchSucceeds) return SwitchResult.Failed(FailureCode ?? ErrorCodes.SwitchFailed);

        // The real service refuses when the window has moved. Modelled so a test can show the
        // decision and the switch are checked against the same window.
        if (expectedWindow != Window.Handle) return SwitchResult.Failed(ErrorCodes.NotForeground);

        Current = language;
        return new SwitchResult(true, null, 25, language);
    }
}

public class AgentCoreTests : IDisposable
{
    private readonly string _root;
    private readonly TestClock _clock = new();
    private readonly FakeLayoutService _layouts = new();
    private readonly ConversationStore _store;
    private readonly AgentCore _core;

    public AgentCoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "autolang-agent-tests", Guid.NewGuid().ToString("n"));
        _store = new ConversationStore(_root, _clock);
        _store.Load();
        _core = new AgentCore(_store, _layouts, new DecisionEngine(_clock), _clock);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Shaped like what the content script produces: 32 lowercase hex characters.</summary>
    private const string HashedKey = "9f2a4c8e1b3d5f7009f2a4c8e1b3d5f7";

    private string Signal(
        string language = "Hebrew",
        int letters = 30,
        bool composerEmpty = true,
        string key = HashedKey,
        long? observedAt = null,
        string direction = "outgoing",
        bool contextReadable = true,
        string site = "web.whatsapp.com")
    {
        var message = new SignalMessage
        {
            Site = site,
            ConversationKey = key,
            AdapterVersion = "1.0.0",
            ComposerEmpty = composerEmpty,
            ContextReadable = contextReadable,
            ObservedAt = observedAt ?? _clock.Now.ToUnixTimeMilliseconds(),
            Messages =
            [
                new WireMessageStats { Direction = direction, Index = 0, Counts = new Dictionary<string, int> { [language] = letters } }
            ],
        };
        return JsonSerializer.Serialize(message, Wire.Json);
    }

    /// <summary>What a surface with nothing readable sends: no messages, and it says so.</summary>
    private string UnreadableSignal(string key = HashedKey, string site = "docs.google.com")
    {
        var message = new SignalMessage
        {
            Site = site,
            ConversationKey = key,
            AdapterVersion = "1.0.0",
            ComposerEmpty = true,
            ContextReadable = false,
            ObservedAt = _clock.Now.ToUnixTimeMilliseconds(),
            Messages = [],
        };
        return JsonSerializer.Serialize(message, Wire.Json);
    }

    private static T Parse<T>(string? json) =>
        JsonSerializer.Deserialize<T>(json ?? throw new InvalidOperationException("Expected a reply."), Wire.Json)!;

    // --- Happy path --------------------------------------------------------------------------

    [Fact]
    public void A_clear_Hebrew_signal_switches_the_layout()
    {
        var reply = Parse<DecisionMessage>(_core.Handle(Signal()));

        Assert.Equal("Switch", reply.Outcome);
        Assert.Equal("he-IL", reply.Language);
        Assert.True(reply.Applied);
        Assert.Equal([Language.Hebrew], _layouts.SwitchRequests);
    }

    [Fact]
    public void A_failed_switch_is_reported_rather_than_claimed()
    {
        _layouts.SwitchSucceeds = false;
        _layouts.FailureCode = ErrorCodes.LayoutNotInstalled;

        var reply = Parse<DecisionMessage>(_core.Handle(Signal()));

        Assert.Equal("Switch", reply.Outcome);
        Assert.False(reply.Applied);
        Assert.Equal(ErrorCodes.LayoutNotInstalled, reply.ErrorCode);
    }

    [Fact]
    public void Returning_to_the_browser_from_another_application_switches_rather_than_backing_off()
    {
        // The whole round trip for the report that opened this: "when I come back from somewhere
        // that is not the browser, in English, to a Hebrew conversation, it does not change to
        // Hebrew by itself."
        //
        // Written at this level because the defect was in the seam. The engine's rule was right
        // about what it could see, the extension sent what it was asked to, and the store did as it
        // was told; what nobody owned was the fact that the user had left. Asserting it on the
        // engine alone would have proved the rule and missed the product.

        // A Hebrew conversation, settled: the user typed Hebrew here, so Hebrew is remembered and
        // the layout is already right.
        _layouts.Current = Language.Hebrew;
        _core.Handle(Signal(language: "Hebrew", composerEmpty: false));
        Assert.Equal(Language.Hebrew, _store.GetConversation(HashedKey)!.LastReliableLanguage);

        // Off to another application - Outlook, Word, anything; it is in no allowlist and this
        // product knows nothing about it. English there.
        _core.NoteLookedAway();
        _layouts.Current = Language.English;
        _clock.Advance(TimeSpan.FromMinutes(3));

        // And back to the same chat. The page looks exactly as they left it, so the only thing that
        // moved is the layout.
        _layouts.SwitchRequests.Clear();
        var reply = Parse<DecisionMessage>(_core.Handle(Signal(language: "Hebrew")));

        Assert.Equal("Switch", reply.Outcome);
        Assert.Equal([Language.Hebrew], _layouts.SwitchRequests);

        // And it did not conclude anything about the user from the layout they walked in with.
        var stored = _store.GetConversation(HashedKey)!;
        Assert.Equal(Language.Hebrew, stored.LastReliableLanguage);
        Assert.Null(stored.ManualOverrideAt);
    }

    [Fact]
    public void A_surface_with_nothing_to_read_is_learned_from_rather_than_waited_on()
    {
        // The spreadsheet case. Google Sheets draws its grid on a canvas, so the letters the user
        // types are rendered and never written into the DOM; the adapter measures thirteen visible
        // characters of Google's own interface and reports that it could not read the surface.
        //
        // Before this, every browser signal claimed to have read the page, so the engine treated
        // that silence as "looked and found nothing" and waited for evidence that cannot arrive.
        // 103 decisions on docs.google.com in one month, `memory=none` in 91% of all of them.
        _layouts.Current = Language.Hebrew;

        var reply = Parse<DecisionMessage>(_core.Handle(UnreadableSignal()));

        // Hebrew is already in place, so there is nothing to switch - but the point is that the
        // product now has an answer for this sheet instead of none.
        Assert.Equal("NoChange", reply.Outcome);
        Assert.Equal("he-IL", reply.Language);
        Assert.Equal(Language.Hebrew, _store.GetConversation(HashedKey)!.LastReliableLanguage);

        // Which it replays on the next visit, which is the whole value: come back to this file with
        // English in place and it puts Hebrew back.
        //
        // Through NoteLookedAway, because that is what actually happens and leaving it out is a
        // modelling error rather than a shortcut - a layout that moves while the engine believes it
        // never stopped watching this sheet is a deliberate Alt+Shift, and it is right to read it
        // as one. Writing this test without it is how I found that out.
        _clock.Advance(TimeSpan.FromMinutes(10));
        _core.NoteLookedAway();
        _layouts.Current = Language.English;
        _layouts.SwitchRequests.Clear();

        Parse<DecisionMessage>(_core.Handle(UnreadableSignal()));
        Assert.Equal([Language.Hebrew], _layouts.SwitchRequests);
    }

    [Fact]
    public void What_is_already_remembered_is_never_overwritten_by_what_the_user_arrived_with()
    {
        // The guard that makes the rule above safe. It fills a blank; it does not get a vote
        // against a language the user established on purpose.
        _layouts.Current = Language.Hebrew;
        _core.Handle(Signal(language: "Hebrew", composerEmpty: false, site: "docs.google.com"));
        Assert.Equal(Language.Hebrew, _store.GetConversation(HashedKey)!.LastReliableLanguage);

        // Back later with English in place, from an unreadable surface. The arriving layout must
        // not become this file's remembered language.
        _clock.Advance(TimeSpan.FromMinutes(10));
        _layouts.Current = Language.English;
        _core.NoteLookedAway();

        _core.Handle(UnreadableSignal());

        Assert.Equal(Language.Hebrew, _store.GetConversation(HashedKey)!.LastReliableLanguage);
    }

    [Fact]
    public void A_site_that_can_be_read_still_waits_for_evidence()
    {
        // The narrowing, asserted. WhatsApp reports itself readable even when a chat has no
        // messages, because an empty chat is a fact rather than a blind spot - so silence there
        // must stay silence. Without this the product would start writing a remembered language
        // from whatever layout the user arrived at a quiet chat with, which the log says would
        // have happened 163 times in a month on that site alone.
        _layouts.Current = Language.Hebrew;

        var quietChat = new SignalMessage
        {
            Site = "web.whatsapp.com",
            ConversationKey = HashedKey,
            AdapterVersion = "1.0.0",
            ComposerEmpty = true,
            ContextReadable = true,
            ObservedAt = _clock.Now.ToUnixTimeMilliseconds(),
            Messages = [],
        };

        var reply = Parse<DecisionMessage>(_core.Handle(JsonSerializer.Serialize(quietChat, Wire.Json)));

        Assert.Equal("Suppressed", reply.Outcome);
        Assert.Equal("NoSignal", reply.Blocker);
        Assert.Null(_store.GetConversation(HashedKey));
    }

    [Fact]
    public void A_client_that_does_not_mention_readability_is_taken_to_have_read_the_page()
    {
        // Compatibility, and it is the safe direction rather than the convenient one. An extension
        // older than this field, or anything else speaking this protocol, is understood to mean "I
        // read the page" - so the engine waits for evidence instead of concluding something from a
        // silence it cannot interpret.
        var withoutTheField = """
            {"type":"signal","protocolVersion":1,"site":"docs.google.com",
             "conversationKey":"9f2a4c8e1b3d5f7009f2a4c8e1b3d5f7","adapterVersion":"1.0.0",
             "composerEmpty":true,"messages":[],"observedAt":0}
            """;

        var reply = Parse<DecisionMessage>(_core.Handle(withoutTheField));

        Assert.Equal("NoSignal", reply.Blocker);
        Assert.Null(_store.GetConversation(HashedKey));
    }

    // --- The Agent trusts Windows, not the page ----------------------------------------------

    [Fact]
    public void Foreground_state_is_read_from_Windows_and_not_taken_from_the_page()
    {
        // The signal has no field for this at all. A page cannot know whether the browser is in
        // the foreground, and a compromised one could lie about it.
        _layouts.BrowserInFront = false;

        var reply = Parse<DecisionMessage>(_core.Handle(Signal()));

        Assert.Equal("Suppressed", reply.Outcome);
        Assert.Equal("NotForeground", reply.Blocker);
        Assert.Empty(_layouts.SwitchRequests);
    }

    [Fact]
    public void The_current_layout_is_read_from_Windows_so_a_no_op_is_recognised()
    {
        _layouts.Current = Language.Hebrew;

        var reply = Parse<DecisionMessage>(_core.Handle(Signal()));

        Assert.Equal("NoChange", reply.Outcome);
        Assert.Equal("AlreadyCorrect", reply.Blocker);
        Assert.Empty(_layouts.SwitchRequests);
    }

    // --- Staleness ---------------------------------------------------------------------------

    [Fact]
    public void A_stale_observation_is_refused()
    {
        // The user can change windows between the page reading the DOM and the Agent acting.
        // Acting on a second-old observation means switching whatever they moved to.
        var stale = _clock.Now.AddSeconds(-5).ToUnixTimeMilliseconds();

        var reply = Parse<ErrorMessage>(_core.Handle(Signal(observedAt: stale)));

        Assert.Equal(ErrorCodes.StaleEvent, reply.Code);
        Assert.Empty(_layouts.SwitchRequests);
    }

    [Fact]
    public void A_fresh_observation_within_the_window_is_accepted()
    {
        var recent = _clock.Now.AddMilliseconds(-300).ToUnixTimeMilliseconds();

        var reply = Parse<DecisionMessage>(_core.Handle(Signal(observedAt: recent)));

        Assert.True(reply.Applied);
    }

    // --- Learning ----------------------------------------------------------------------------

    [Fact]
    public void Typing_is_recorded_as_what_the_user_actually_uses_here()
    {
        _layouts.Current = Language.Hebrew;

        var reply = Parse<DecisionMessage>(_core.Handle(Signal(composerEmpty: false)));

        Assert.Equal("UserTyping", reply.Blocker);
        Assert.Equal(Language.Hebrew, _store.GetConversation(HashedKey)!.LastReliableLanguage);
    }

    [Fact]
    public void A_remembered_language_is_applied_on_the_next_visit()
    {
        _store.RememberLanguage(HashedKey, Language.Hebrew);
        _layouts.Current = Language.English;

        // English messages, but the user has always typed Hebrew here.
        var reply = Parse<DecisionMessage>(_core.Handle(Signal(language: "English", letters: 100)));

        Assert.Equal("he-IL", reply.Language);
        Assert.Equal("ConversationMemory", reply.Source);
    }

    // --- Commands ----------------------------------------------------------------------------

    [Fact]
    public void Pinning_a_conversation_takes_effect_immediately()
    {
        var command = JsonSerializer.Serialize(new CommandMessage
        {
            Command = "setMode",
            ConversationKey = HashedKey,
            Mode = "Pinned",
            LanguageTag = "he-IL",
        }, Wire.Json);

        var state = Parse<StateMessage>(_core.Handle(command));
        Assert.Equal("Pinned", state.ConversationMode);
        Assert.Equal("he-IL", state.PinnedLanguage);

        var reply = Parse<DecisionMessage>(_core.Handle(Signal(language: "English", letters: 200)));
        Assert.Equal("he-IL", reply.Language);
        Assert.Equal("ManualPin", reply.Source);
    }

    [Fact]
    public void Pausing_a_site_stops_switching_there()
    {
        var command = JsonSerializer.Serialize(new CommandMessage
        {
            Command = "pauseSite",
            Site = "web.whatsapp.com",
        }, Wire.Json);

        _core.Handle(command);

        var reply = Parse<DecisionMessage>(_core.Handle(Signal()));
        Assert.Equal("SitePaused", reply.Blocker);
    }

    [Fact]
    public void A_manual_change_starts_the_cooldown_and_resets_hysteresis()
    {
        var command = JsonSerializer.Serialize(new CommandMessage
        {
            Command = "noteManualChange",
            ConversationKey = HashedKey,
            LanguageTag = "en-US",
        }, Wire.Json);

        _core.Handle(command);

        var reply = Parse<DecisionMessage>(_core.Handle(Signal()));

        // Either guard is a correct refusal; what matters is that we do not immediately undo the
        // user's own choice.
        Assert.Equal("Suppressed", reply.Outcome);
        Assert.Contains(reply.Blocker, new[] { "ManualCooldown", "Hysteresis" });
        Assert.Empty(_layouts.SwitchRequests);
    }

    [Fact]
    public void Clear_data_wipes_stored_preferences()
    {
        _store.RememberLanguage(HashedKey, Language.Hebrew);

        _core.Handle(JsonSerializer.Serialize(new CommandMessage { Command = "clearData" }, Wire.Json));

        Assert.Null(_store.GetConversation(HashedKey));
    }

    [Fact]
    public void Disabling_the_product_stops_every_switch()
    {
        _core.Handle(JsonSerializer.Serialize(new CommandMessage { Command = "setEnabled", Enabled = false }, Wire.Json));

        var reply = Parse<DecisionMessage>(_core.Handle(Signal()));
        Assert.Equal("Disabled", reply.Blocker);
    }

    // --- Queries -----------------------------------------------------------------------------

    [Fact]
    public void A_state_query_reports_what_the_popup_needs()
    {
        _store.RememberLanguage(HashedKey, Language.Hebrew);
        _layouts.Current = Language.English;

        var query = JsonSerializer.Serialize(new QueryMessage
        {
            ConversationKey = HashedKey,
            Site = "web.whatsapp.com",
        }, Wire.Json);

        var state = Parse<StateMessage>(_core.Handle(query));

        Assert.True(state.Enabled);
        Assert.Equal("en-US", state.CurrentLayout);
        Assert.Equal("he-IL", state.RememberedLanguage);
        Assert.False(state.SitePaused);
        Assert.Equal(["he-IL", "en-US"], state.AvailableLayouts);
    }

    [Fact]
    public void A_state_query_reports_when_a_layout_is_missing_from_Windows()
    {
        _layouts.Available = [Language.English];

        var state = Parse<StateMessage>(_core.Handle(JsonSerializer.Serialize(new QueryMessage(), Wire.Json)));

        Assert.Equal(["en-US"], state.AvailableLayouts);
    }

    // --- Bad input ---------------------------------------------------------------------------

    [Fact]
    public void Malformed_json_is_answered_rather_than_crashing_the_agent()
    {
        // One browser sending nonsense must not take down every other conversation.
        var reply = Parse<ErrorMessage>(_core.Handle("{ not json"));

        Assert.Equal(ErrorCodes.BadMessage, reply.Code);
    }

    [Fact]
    public void An_unknown_message_type_is_refused()
    {
        var reply = Parse<ErrorMessage>(_core.Handle("""{"type":"launchMissiles"}"""));

        Assert.Equal(ErrorCodes.BadMessage, reply.Code);
    }

    [Fact]
    public void A_future_protocol_version_is_refused_clearly()
    {
        var reply = Parse<ErrorMessage>(_core.Handle("""{"type":"signal","protocolVersion":99}"""));

        Assert.Equal(ErrorCodes.UnsupportedProtocol, reply.Code);
    }

    [Fact]
    public void A_signal_without_a_conversation_key_is_refused()
    {
        var reply = Parse<ErrorMessage>(_core.Handle("""{"type":"signal","protocolVersion":1,"messages":[]}"""));

        Assert.Equal(ErrorCodes.BadMessage, reply.Code);
    }

    [Fact]
    public void An_unhealthy_adapter_report_is_absorbed_without_a_reply()
    {
        var health = JsonSerializer.Serialize(new HealthMessage
        {
            Site = "web.whatsapp.com",
            AdapterVersion = "1.0.0",
            Healthy = false,
            Missing = ["mainPanel"],
        }, Wire.Json);

        Assert.Null(_core.Handle(health));
    }

    // --- Privacy -----------------------------------------------------------------------------

    [Fact]
    public void Replies_carry_no_message_content()
    {
        var reply = _core.Handle(Signal())!;

        Assert.DoesNotContain("counts", reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("text", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_layout_the_user_set_by_hand_is_stored_and_stops_the_argument()
    {
        // The whole loop, not the engine's half of it. The engine notices that the layout moved to
        // something it did not ask for; what makes the product stop switching back is this class
        // writing the override into the store, and nothing tested that branch.
        //
        // Modelled on five hours of a real log in Google Sheets, where the grid is a canvas so the
        // composer never reads as occupied: the user set Hebrew by hand four times and was pulled
        // back to English four times.
        _core.Handle(Signal(language: "English", letters: 40));
        Assert.Equal(Language.English, _layouts.Current);

        // The user reaches for Alt+Shift. The page has not changed and the composer is still empty.
        _layouts.Current = Language.Hebrew;
        var reply = Parse<DecisionMessage>(_core.Handle(Signal(language: "English", letters: 40)));

        Assert.Equal(nameof(DecisionOutcome.Suppressed), reply.Outcome);
        Assert.Equal(nameof(DecisionBlocker.ManualChange), reply.Blocker);

        var stored = _store.GetConversation(HashedKey);
        Assert.NotNull(stored);
        Assert.Equal(Language.Hebrew, stored!.LastReliableLanguage);
        Assert.NotNull(stored.ManualOverrideAt);

        // And the next observation leaves the layout where the user put it.
        _clock.Advance(TimeSpan.FromSeconds(5));
        _core.Handle(Signal(language: "English", letters: 40));

        Assert.Equal(Language.Hebrew, _layouts.Current);
    }
}
