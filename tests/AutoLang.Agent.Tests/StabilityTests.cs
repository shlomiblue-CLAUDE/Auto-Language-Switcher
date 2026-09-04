using System.Text.Json;
using AutoLang.Core;

namespace AutoLang.Agent.Tests;

/// <summary>
/// PDR section 17: thirty consecutive conversation switches with no loop, no wrong switch while
/// typing, and no lost focus.
///
/// A loop is the failure that matters most here, because it is the one a user cannot ignore: the
/// keyboard flipping under their hands while they try to type. It is also the one that unit tests
/// of individual guards do not catch - each guard behaves correctly in isolation and the loop
/// emerges from the sequence.
/// </summary>
public class StabilityTests : IDisposable
{
    private readonly string _root;
    private readonly TestClock _clock = new();
    private readonly FakeLayoutService _layouts = new();
    private readonly ConversationStore _store;
    private readonly AgentCore _core;

    public StabilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "autolang-stability", Guid.NewGuid().ToString("n"));
        _store = new ConversationStore(_root, _clock);
        _store.Load();
        _core = new AgentCore(_store, _layouts, new DecisionEngine(_clock), _clock);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Thirty-two distinct hash-shaped keys, so conversations do not share memory.</summary>
    private static string KeyFor(int index) => index.ToString("x").PadLeft(32, 'a');

    private DecisionMessage Send(string key, string language, bool composerEmpty = true)
    {
        var signal = JsonSerializer.Serialize(new SignalMessage
        {
            Site = "web.whatsapp.com",
            ConversationKey = key,
            ComposerEmpty = composerEmpty,
            ObservedAt = _clock.Now.ToUnixTimeMilliseconds(),
            Messages = Enumerable.Range(0, 10)
                .Select(i => new WireMessageStats
                {
                    Direction = "outgoing",
                    Index = i,
                    Counts = new Dictionary<string, int> { [language] = 18 },
                })
                .ToList(),
        }, Wire.Json);

        return JsonSerializer.Deserialize<DecisionMessage>(_core.Handle(signal)!, Wire.Json)!;
    }

    [Fact]
    public void Thirty_consecutive_conversation_switches_produce_exactly_thirty_switches()
    {
        // Alternating Hebrew and English conversations, each visited once, with realistic spacing.
        for (int i = 0; i < 30; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(3));
            var decision = Send(KeyFor(i), i % 2 == 0 ? "Hebrew" : "English");

            Assert.Equal("Switch", decision.Outcome);
            Assert.True(decision.Applied, $"switch {i} was not applied");
        }

        Assert.Equal(30, _layouts.SwitchRequests.Count);

        // No two consecutive requests for the same language: that would mean the engine asked
        // Windows for a layout it already had.
        for (int i = 1; i < _layouts.SwitchRequests.Count; i++)
            Assert.NotEqual(_layouts.SwitchRequests[i - 1], _layouts.SwitchRequests[i]);
    }

    [Fact]
    public void Repeated_observations_of_one_conversation_switch_once_and_then_stop()
    {
        // WhatsApp mutates its DOM constantly for reasons unrelated to messages, so the same
        // conversation is re-read many times. Only the first read should reach Windows.
        _layouts.Current = Language.English;

        for (int i = 0; i < 40; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            Send(KeyFor(1), "Hebrew");
        }

        Assert.Single(_layouts.SwitchRequests);
        Assert.Equal(Language.Hebrew, _layouts.SwitchRequests[0]);
    }

    [Fact]
    public void Rapid_alternation_between_two_conversations_cannot_produce_a_flip_flop_loop()
    {
        // The nightmare case: two conversations in different languages, switched between faster
        // than the hysteresis window. Hysteresis has to cap the rate, or the keyboard flips under
        // the user's hands.
        _layouts.Current = Language.English;

        for (int i = 0; i < 100; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(50));
            Send(KeyFor(i % 2 == 0 ? 1 : 2), i % 2 == 0 ? "Hebrew" : "English");
        }

        // 100 iterations at 50ms is 5 seconds; at one switch per 750ms that is at most 7.
        Assert.InRange(_layouts.SwitchRequests.Count, 1, 8);
    }

    [Fact]
    public void Typing_throughout_a_long_session_prevents_every_switch()
    {
        for (int i = 0; i < 30; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(3));
            var decision = Send(KeyFor(i), i % 2 == 0 ? "Hebrew" : "English", composerEmpty: false);
            Assert.Equal("UserTyping", decision.Blocker);
        }

        Assert.Empty(_layouts.SwitchRequests);
    }

    [Fact]
    public void A_background_browser_prevents_every_switch_across_a_long_session()
    {
        _layouts.BrowserInFront = false;

        for (int i = 0; i < 30; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(3));
            Assert.Equal("NotForeground", Send(KeyFor(i), "Hebrew").Blocker);
        }

        Assert.Empty(_layouts.SwitchRequests);
    }

    [Fact]
    public void A_long_session_leaves_the_store_bounded_and_readable()
    {
        _layouts.Current = Language.English;

        for (int i = 0; i < 30; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(3));
            Send(KeyFor(i), i % 2 == 0 ? "Hebrew" : "English", composerEmpty: false);
        }

        // The debug buffer is capped, so a long session cannot grow memory without limit.
        Assert.True(_store.DebugEvents.Count <= 200);

        var reopened = new ConversationStore(_root, _clock);
        reopened.Load();

        // English, in every conversation, including the ones whose messages were Hebrew.
        //
        // That is the design, not a bug, and it is the subtle part worth pinning: while the
        // composer has text the user is typing with a layout we can read, and what they are
        // actually doing outranks what their words look like. Nothing switched during this session
        // because typing blocked it, so the layout in use stayed English throughout.
        for (int i = 0; i < 30; i++)
            Assert.Equal(Language.English, reopened.GetConversation(KeyFor(i))!.LastReliableLanguage);
    }

    [Fact]
    public void What_is_learned_follows_the_layout_in_use_not_the_language_of_the_messages()
    {
        // The same rule stated on its own, because it is easy to misread the test above as a bug.
        _layouts.Current = Language.Hebrew;

        Send(KeyFor(7), "English", composerEmpty: false);

        Assert.Equal(Language.Hebrew, _store.GetConversation(KeyFor(7))!.LastReliableLanguage);
    }

    [Fact]
    public void Switching_resumes_correctly_after_a_manual_override_mid_session()
    {
        // The user disagrees once. The product must back off, then pick up again - not sulk, and
        // not immediately undo them.
        _clock.Advance(TimeSpan.FromSeconds(3));
        Send(KeyFor(1), "Hebrew");

        _core.Handle(JsonSerializer.Serialize(new CommandMessage
        {
            Command = "noteManualChange",
            ConversationKey = KeyFor(1),
            LanguageTag = "en-US",
        }, Wire.Json));

        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("Suppressed", Send(KeyFor(1), "Hebrew").Outcome);

        // After the cooldown, a different conversation is decided normally again.
        _clock.Advance(TimeSpan.FromMinutes(6));
        _layouts.Current = Language.English;
        Assert.Equal("Switch", Send(KeyFor(2), "Hebrew").Outcome);
    }
}
