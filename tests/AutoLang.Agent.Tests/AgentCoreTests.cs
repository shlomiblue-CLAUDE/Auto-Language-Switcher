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
        string direction = "outgoing")
    {
        var message = new SignalMessage
        {
            Site = "web.whatsapp.com",
            ConversationKey = key,
            AdapterVersion = "1.0.0",
            ComposerEmpty = composerEmpty,
            ObservedAt = observedAt ?? _clock.Now.ToUnixTimeMilliseconds(),
            Messages =
            [
                new WireMessageStats { Direction = direction, Index = 0, Counts = new Dictionary<string, int> { [language] = letters } }
            ],
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
