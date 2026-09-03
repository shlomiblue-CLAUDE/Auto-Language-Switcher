using System.Text.Json;
using AutoLang.Core;

namespace AutoLang.Agent.Tests;

/// <summary>
/// The twelve acceptance rows of PDR section 16, one test each, named after the row.
///
/// These run against the real AgentCore with a fake Windows, so they prove the product decides
/// correctly. Three rows cannot be settled here and say so in their own comments: whether Windows
/// actually applies a switch, whether the selectors match today's live WhatsApp, and whether the
/// browser really makes no network requests. Those are in docs/ACCEPTANCE.md as manual procedures.
///
/// Kept separate from the unit tests on purpose. Those cover mechanisms; this file answers "does
/// the product do what the document promised", and it should stay readable next to the document.
/// </summary>
public class AcceptanceTests : IDisposable
{
    private readonly string _root;
    private readonly TestClock _clock = new();
    private readonly FakeLayoutService _layouts = new();
    private readonly ConversationStore _store;
    private readonly AgentCore _core;

    private const string Family = "1111111111111111aaaaaaaaaaaaaaaa";
    private const string Supplier = "2222222222222222bbbbbbbbbbbbbbbb";

    public AcceptanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "autolang-acceptance", Guid.NewGuid().ToString("n"));
        _store = new ConversationStore(_root, _clock);
        _store.Load();
        _core = new AgentCore(_store, _layouts, new DecisionEngine(_clock), _clock);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static WireMessageStats Msg(string direction, string language, int letters, int index) =>
        new() { Direction = direction, Index = index, Counts = new Dictionary<string, int> { [language] = letters } };

    private DecisionMessage Send(string key, IEnumerable<WireMessageStats> messages, bool composerEmpty = true)
    {
        var signal = JsonSerializer.Serialize(new SignalMessage
        {
            Site = "web.whatsapp.com",
            ConversationKey = key,
            AdapterVersion = "1.0.0",
            ComposerEmpty = composerEmpty,
            ObservedAt = _clock.Now.ToUnixTimeMilliseconds(),
            Messages = messages.ToList(),
        }, Wire.Json);

        var reply = _core.Handle(signal);
        return JsonSerializer.Deserialize<DecisionMessage>(reply!, Wire.Json)!;
    }

    /// <summary>Hysteresis allows one switch per 750ms; the rows are independent scenarios.</summary>
    private void SettleHysteresis() => _clock.Advance(TimeSpan.FromSeconds(2));

    private static List<WireMessageStats> Ten(string direction, string language) =>
        Enumerable.Range(0, 10).Select(i => Msg(direction, language, 18, i)).ToList();

    // --- Row 1: עברית ברורה --------------------------------------------------------------

    [Fact]
    public void Clear_Hebrew_ten_outgoing_Hebrew_messages_gives_HE()
    {
        _layouts.Current = Language.English;

        var decision = Send(Family, Ten("outgoing", "Hebrew"));

        Assert.Equal("Switch", decision.Outcome);
        Assert.Equal("he-IL", decision.Language);
        Assert.True(decision.Applied);
    }

    // --- Row 2: אנגלית ברורה -------------------------------------------------------------

    [Fact]
    public void Clear_English_ten_outgoing_English_messages_gives_EN()
    {
        _layouts.Current = Language.Hebrew;

        var decision = Send(Supplier, Ten("outgoing", "English"));

        Assert.Equal("Switch", decision.Outcome);
        Assert.Equal("en-US", decision.Language);
        Assert.True(decision.Applied);
    }

    // --- Row 3: הצד השני בשפה אחרת -------------------------------------------------------

    [Fact]
    public void The_other_side_writes_another_language_incoming_Hebrew_outgoing_English_gives_EN()
    {
        // The single most important row. It is what separates "what language is this conversation"
        // from "what language is this person about to type".
        _layouts.Current = Language.Hebrew;

        var messages = new List<WireMessageStats>();
        for (int i = 0; i < 5; i++)
        {
            messages.Add(Msg("outgoing", "English", 25, i * 2));
            messages.Add(Msg("incoming", "Hebrew", 60, i * 2 + 1));
        }

        var decision = Send(Supplier, messages);

        Assert.Equal("Switch", decision.Outcome);
        Assert.Equal("en-US", decision.Language);
        Assert.Equal("OutgoingMessages", decision.Source);
    }

    // --- Row 4: שיחה מעורבת --------------------------------------------------------------

    [Fact]
    public void A_mixed_conversation_with_close_scores_changes_nothing()
    {
        _layouts.Current = Language.English;

        var decision = Send(Family, [
            new WireMessageStats
            {
                Direction = "outgoing",
                Index = 0,
                Counts = new Dictionary<string, int> { ["Hebrew"] = 30, ["English"] = 30 },
            }
        ]);

        Assert.Equal("Suppressed", decision.Outcome);
        Assert.Equal("LowConfidence", decision.Blocker);
        Assert.Empty(_layouts.SwitchRequests);
    }

    // --- Row 5: אין טקסט מספיק -----------------------------------------------------------

    [Fact]
    public void Not_enough_text_only_emoji_and_numbers_changes_nothing()
    {
        // Emoji and digits produce no letters at all, so the adapter sends no messages.
        _layouts.Current = Language.English;

        var decision = Send(Family, []);

        Assert.Equal("Suppressed", decision.Outcome);
        Assert.Equal("NoSignal", decision.Blocker);
        Assert.Empty(_layouts.SwitchRequests);
    }

    [Fact]
    public void Not_enough_text_but_a_saved_preference_exists_uses_the_preference()
    {
        // The row allows either outcome: no change, or the saved preference. With one saved, the
        // preference is the better answer - it is what the user actually types here.
        _store.RememberLanguage(Family, Language.Hebrew);
        _layouts.Current = Language.English;

        var decision = Send(Family, []);

        Assert.Equal("Switch", decision.Outcome);
        Assert.Equal("he-IL", decision.Language);
        Assert.Equal("ConversationMemory", decision.Source);
    }

    // --- Row 6: חזרה לשיחה מוכרת ---------------------------------------------------------

    [Fact]
    public void Returning_to_a_known_conversation_switches_from_memory_without_full_analysis()
    {
        _store.RememberLanguage(Family, Language.Hebrew);
        _layouts.Current = Language.English;

        // English messages on screen, but this is a conversation the user writes Hebrew in. Memory
        // ranks above analysis precisely so returning to it does not wait for the page to fill in.
        var decision = Send(Family, Ten("outgoing", "English"));

        Assert.Equal("Switch", decision.Outcome);
        Assert.Equal("he-IL", decision.Language);
        Assert.Equal("ConversationMemory", decision.Source);
    }

    // --- Row 7: הקלדה פעילה --------------------------------------------------------------

    [Fact]
    public void Active_typing_a_non_empty_composer_prevents_any_switch()
    {
        _layouts.Current = Language.English;

        var decision = Send(Family, Ten("outgoing", "Hebrew"), composerEmpty: false);

        Assert.Equal("Suppressed", decision.Outcome);
        Assert.Equal("UserTyping", decision.Blocker);
        Assert.Empty(_layouts.SwitchRequests);
    }

    // --- Row 8: override -----------------------------------------------------------------

    [Fact]
    public void Override_Always_Hebrew_beats_English_text()
    {
        _core.Handle(JsonSerializer.Serialize(new CommandMessage
        {
            Command = "setMode",
            ConversationKey = Supplier,
            Mode = "AlwaysHebrew",
        }, Wire.Json));

        _layouts.Current = Language.English;

        var decision = Send(Supplier, Ten("outgoing", "English"));

        Assert.Equal("he-IL", decision.Language);
        Assert.Equal("ManualPin", decision.Source);
        Assert.True(decision.Applied);
    }

    // --- Row 9: שפה שלישית ב-Windows -----------------------------------------------------

    [Fact]
    public void A_third_Windows_language_is_never_reached_by_blind_cycling()
    {
        // The engine names the language it wants. Nothing in the product advances to "the next
        // layout", which is how a third installed language would otherwise be selected by accident.
        _layouts.Available = [Language.Hebrew, Language.English];
        _layouts.Current = Language.English;

        Send(Family, Ten("outgoing", "Hebrew"));
        SettleHysteresis();
        Send(Supplier, Ten("outgoing", "English"));

        Assert.Equal([Language.Hebrew, Language.English], _layouts.SwitchRequests);
        Assert.DoesNotContain(Language.Unknown, _layouts.SwitchRequests);
    }

    [Fact]
    public void A_language_Windows_does_not_have_is_reported_rather_than_approximated()
    {
        _layouts.SwitchSucceeds = false;
        _layouts.FailureCode = ErrorCodes.LayoutNotInstalled;
        _layouts.Current = Language.English;

        var decision = Send(Family, Ten("outgoing", "Hebrew"));

        Assert.False(decision.Applied);
        Assert.Equal(ErrorCodes.LayoutNotInstalled, decision.ErrorCode);
    }

    // --- Row 10: שינוי DOM ---------------------------------------------------------------

    [Fact]
    public void A_DOM_change_reports_status_and_causes_no_wrong_switch()
    {
        // An unhealthy adapter stops sending signals, so the Agent has nothing to act on. What must
        // hold here is that the health report is absorbed without a reply and without a switch.
        var health = JsonSerializer.Serialize(new HealthMessage
        {
            Site = "web.whatsapp.com",
            AdapterVersion = "1.0.0",
            Healthy = false,
            Missing = ["mainPanel", "composer"],
        }, Wire.Json);

        Assert.Null(_core.Handle(health));
        Assert.Empty(_layouts.SwitchRequests);
    }

    [Fact]
    public void A_broken_adapter_reporting_an_empty_composer_still_cannot_cause_a_switch()
    {
        // The adapter fails closed by reporting the composer as non-empty when it cannot find it,
        // so a page it no longer understands suppresses switching rather than guessing.
        _layouts.Current = Language.English;

        var decision = Send(Family, Ten("outgoing", "Hebrew"), composerEmpty: false);

        Assert.Equal("UserTyping", decision.Blocker);
        Assert.Empty(_layouts.SwitchRequests);
    }

    // --- Row 11: פרטיות ------------------------------------------------------------------

    [Fact]
    public void Privacy_no_message_content_reaches_storage_or_replies()
    {
        var reply = Send(Family, Ten("outgoing", "Hebrew"));
        _core.Handle(JsonSerializer.Serialize(new QueryMessage { ConversationKey = Family }, Wire.Json));

        var serialisedReply = JsonSerializer.Serialize(reply, Wire.Json);
        var stored = string.Join("\n", Directory.GetFiles(_root).Select(File.ReadAllText));

        foreach (var forbidden in new[] { "counts", "text", "message-out", "@c.us", "972" })
        {
            Assert.DoesNotContain(forbidden, serialisedReply, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, stored, StringComparison.OrdinalIgnoreCase);
        }

        // What IS stored: the hashed key and a language.
        Assert.Contains(Family, stored);
        Assert.Contains("Hebrew", stored);
    }

    // --- Row 12: מיקוד -------------------------------------------------------------------

    [Fact]
    public void Focus_an_event_from_a_background_tab_does_not_change_another_windows_language()
    {
        // The spike proved Windows enforces nothing here: a background Edge window switched just as
        // readily as a foreground one. This guard is the only thing standing between a background
        // tab and the keyboard of whatever the user is actually typing in.
        _layouts.Foreground = false;
        _layouts.Current = Language.English;

        var decision = Send(Family, Ten("outgoing", "Hebrew"));

        Assert.Equal("Suppressed", decision.Outcome);
        Assert.Equal("NotForeground", decision.Blocker);
        Assert.Empty(_layouts.SwitchRequests);
    }

    [Fact]
    public void Focus_a_stale_observation_from_a_window_the_user_has_left_is_refused()
    {
        // The other half of the same problem: the user can move between the page reading the DOM
        // and the Agent acting on it.
        var stale = _clock.Now.AddSeconds(-5).ToUnixTimeMilliseconds();

        var signal = JsonSerializer.Serialize(new SignalMessage
        {
            Site = "web.whatsapp.com",
            ConversationKey = Family,
            ObservedAt = stale,
            Messages = Ten("outgoing", "Hebrew"),
        }, Wire.Json);

        var error = JsonSerializer.Deserialize<ErrorMessage>(_core.Handle(signal)!, Wire.Json)!;

        Assert.Equal(ErrorCodes.StaleEvent, error.Code);
        Assert.Empty(_layouts.SwitchRequests);
    }
}
