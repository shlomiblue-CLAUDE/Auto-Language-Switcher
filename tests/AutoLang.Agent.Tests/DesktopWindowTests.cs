using AutoLang.Core;

namespace AutoLang.Agent.Tests;

/// <summary>
/// The desktop source, which has no text to read and must not want any.
///
/// Reading another application's contents means accessibility APIs; noticing that somebody is
/// typing in one means a global keyboard hook. The product's central claim survives neither, so
/// this path carries no evidence at all and works the way Google Sheets already proved a page with
/// nothing readable works: the user moves the layout, the engine notices and learns, and memory
/// replays it.
///
/// Two properties here are worth more than the rest. An application that was never allowed
/// produces nothing whatsoever - not a decision, not a log line, not a stored key. And a window
/// title never reaches disk in any form.
/// </summary>
public class DesktopWindowTests : IDisposable
{
    private readonly string _root;
    private readonly TestClock _clock = new();
    private readonly FakeLayoutService _layouts = new();
    private readonly ConversationStore _store;
    private readonly AgentCore _core;

    public DesktopWindowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "autolang-desktop-tests", Guid.NewGuid().ToString("n"));
        _store = new ConversationStore(_root, _clock);
        _store.Load();
        _core = new AgentCore(_store, _layouts, new DecisionEngine(_clock), _clock);

        _layouts.Window = new ForegroundWindow(new IntPtr(7), "slack", "general");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string ConversationsJson() =>
        File.Exists(_store.ConversationsPath) ? File.ReadAllText(_store.ConversationsPath) : "";

    [Fact]
    public void An_application_that_was_never_allowed_produces_nothing()
    {
        _core.ObserveWindow("slack", "general");

        // Not "suppressed": nothing at all. A decision would mean a stored key and a log line, and
        // a record of every application somebody opens is exactly what the allowlist exists to
        // avoid keeping.
        Assert.False(File.Exists(_store.ConversationsPath));
        Assert.Empty(_store.AllowedApps());
    }

    [Fact]
    public void An_allowed_application_learns_the_layout_the_user_chose()
    {
        _store.AllowApp("slack");
        _store.AllowApp("code");

        // Arriving in a window we know nothing about decides nothing - there is no evidence here
        // and none is invented.
        _core.ObserveWindow("slack", "general");
        Assert.Empty(_layouts.SwitchRequests);

        // The user reaches for Alt+Shift while sitting in Slack. Nothing in Windows reports that
        // to an observer, which is why the watcher re-reads the window it is already in; this
        // second observation of the same window is that re-read.
        _layouts.Current = Language.Hebrew;
        _core.ObserveWindow("slack", "general");

        // Away to another application, and back. Going through one matters: arriving somewhere
        // with a different layout is where the user came from, not a choice they made here, and
        // the engine tells the two apart by whether the conversation changed in between.
        _clock.Advance(TimeSpan.FromMinutes(6));
        _layouts.Window = new ForegroundWindow(new IntPtr(8), "code", "main.ts");
        _layouts.Current = Language.English;
        _core.ObserveWindow("code", "main.ts");

        _layouts.SwitchRequests.Clear();
        _layouts.Window = new ForegroundWindow(new IntPtr(7), "slack", "general");
        _core.ObserveWindow("slack", "general");

        Assert.Contains(Language.Hebrew, _layouts.SwitchRequests);
    }

    [Fact]
    public void Arriving_with_a_different_layout_is_not_read_as_a_choice()
    {
        // The defect this exists for. The user sets Hebrew in Slack, works in an English editor,
        // and comes back. If arrival counted as a manual change, Slack would learn English every
        // time - the memory would be overwritten by wherever they happened to be last.
        _store.AllowApp("slack");
        _store.AllowApp("code");

        _core.ObserveWindow("slack", "general");
        _layouts.Current = Language.Hebrew;
        _core.ObserveWindow("slack", "general");

        _clock.Advance(TimeSpan.FromMinutes(6));
        _layouts.Window = new ForegroundWindow(new IntPtr(8), "code", "main.ts");
        _layouts.Current = Language.English;
        _core.ObserveWindow("code", "main.ts");

        _layouts.Window = new ForegroundWindow(new IntPtr(7), "slack", "general");
        _core.ObserveWindow("slack", "general");

        // Still Hebrew, because arriving taught it nothing.
        Assert.Equal(Language.Hebrew, _store.GetConversation(
            DesktopIdentity.ForWindow(_store.Settings.DesktopSalt, "slack", "general"))!.LastReliableLanguage);
    }

    [Fact]
    public void Two_windows_of_one_application_remember_separately()
    {
        // The reason identity is per window rather than per application: one Slack, two people,
        // two languages.
        _store.AllowApp("slack");

        _layouts.Window = new ForegroundWindow(new IntPtr(7), "slack", "general");
        _core.ObserveWindow("slack", "general");
        _layouts.Current = Language.Hebrew;
        _core.ObserveWindow("slack", "general");

        _layouts.Window = new ForegroundWindow(new IntPtr(7), "slack", "engineering");
        _core.ObserveWindow("slack", "engineering");
        _layouts.Current = Language.English;
        _core.ObserveWindow("slack", "engineering");

        _clock.Advance(TimeSpan.FromMinutes(6));

        _layouts.SwitchRequests.Clear();
        _layouts.Current = Language.English;
        _layouts.Window = new ForegroundWindow(new IntPtr(7), "slack", "general");
        _core.ObserveWindow("slack", "general");

        Assert.Contains(Language.Hebrew, _layouts.SwitchRequests);
    }

    [Fact]
    public void A_window_title_never_reaches_disk()
    {
        _store.AllowApp("slack");

        // A title carries a person's name, a subject line, a file path. It is hashed with a salt
        // generated on this machine and the raw value is gone before anything is written - the
        // same treatment a WhatsApp chat title gets, through the same 32-hex shape the Agent
        // refuses anything else for.
        _core.ObserveWindow("slack", "Yossi Cohen — private");
        _layouts.Current = Language.Hebrew;
        _core.ObserveWindow("slack", "Yossi Cohen — private");

        var stored = ConversationsJson();

        Assert.NotEqual("", stored);
        Assert.DoesNotContain("Yossi", stored);
        Assert.DoesNotContain("Cohen", stored);
        Assert.DoesNotContain("private", stored);

        foreach (var key in System.Text.Json.JsonDocument.Parse(stored).RootElement.EnumerateObject())
            Assert.Matches("^[0-9a-f]{32}$", key.Name);
    }

    [Fact]
    public void The_same_window_on_two_machines_does_not_share_a_key()
    {
        // What the salt is for. Without one, SHA-256 of "general" is the same value everywhere and
        // a stored file would say which Slack channels somebody is in to anyone who precomputed
        // the obvious few thousand.
        var mine = DesktopIdentity.ForWindow(DesktopIdentity.NewSalt(), "slack", "general");
        var theirs = DesktopIdentity.ForWindow(DesktopIdentity.NewSalt(), "slack", "general");

        Assert.NotEqual(mine, theirs);
        Assert.Matches("^[0-9a-f]{32}$", mine);
    }

    [Fact]
    public void Withdrawing_an_application_forgets_that_it_was_ever_there()
    {
        _store.AllowApp("slack");
        _store.BlockApp("slack");

        // Removed rather than marked as blocked. A blocked entry is a permanent note that somebody
        // once ran it, which is the record this feature is built to avoid.
        Assert.Empty(_store.AllowedApps());
        Assert.DoesNotContain("slack", File.ReadAllText(_store.AppsPath));
    }

    [Fact]
    public void A_decision_about_one_window_is_not_applied_to_another()
    {
        // The gap the switch guard closes: the user can move between the decision and the switch,
        // and "is a browser in front" said nothing about which window a desktop decision was for.
        _store.AllowApp("slack");
        _core.ObserveWindow("slack", "general");
        _layouts.Current = Language.Hebrew;
        _core.ObserveWindow("slack", "general");

        _clock.Advance(TimeSpan.FromMinutes(6));
        _layouts.Current = Language.English;

        // They alt-tab to something else in the instant before the switch lands.
        _layouts.Window = new ForegroundWindow(new IntPtr(99), "notepad", "untitled");
        _core.ObserveWindow("slack", "general");

        Assert.NotEqual(Language.Hebrew, _layouts.Current);
    }
}
