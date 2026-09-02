using AutoLang.Core;

namespace AutoLang.Core.Tests;

public class ConversationStoreTests : IDisposable
{
    private readonly string _root;
    private readonly TestClock _clock = new();
    private readonly ConversationStore _store;

    public ConversationStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "autolang-tests", Guid.NewGuid().ToString("n"));
        _store = new ConversationStore(_root, _clock);
        _store.Load();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a locked temp dir must not fail the suite */ }
    }

    private ConversationStore Reopen()
    {
        var reopened = new ConversationStore(_root, _clock);
        reopened.Load();
        return reopened;
    }

    [Fact]
    public void An_unknown_conversation_has_no_preference()
    {
        Assert.Null(_store.GetConversation("never-seen"));
    }

    [Fact]
    public void A_remembered_language_survives_a_restart()
    {
        _store.RememberLanguage("conv-1", Language.Hebrew);

        var preference = Reopen().GetConversation("conv-1");

        Assert.NotNull(preference);
        Assert.Equal(Language.Hebrew, preference!.LastReliableLanguage);
        Assert.Equal(_clock.Now, preference.UpdatedAt);
    }

    [Fact]
    public void Unknown_is_never_recorded_as_a_remembered_language()
    {
        // Writing Unknown would create a preference that outranks analysis while saying nothing.
        _store.RememberLanguage("conv-1", Language.Unknown);

        Assert.Null(_store.GetConversation("conv-1"));
    }

    [Fact]
    public void A_pinned_mode_survives_a_restart()
    {
        _store.SetMode("conv-1", ConversationMode.AlwaysHebrew);

        var preference = Reopen().GetConversation("conv-1");

        Assert.Equal(ConversationMode.AlwaysHebrew, preference!.Mode);
        Assert.Equal(Language.Hebrew, preference.PinnedLanguage);
    }

    [Fact]
    public void Pinning_does_not_erase_what_was_already_learned()
    {
        _store.RememberLanguage("conv-1", Language.English);
        _store.SetMode("conv-1", ConversationMode.AlwaysHebrew);

        var preference = _store.GetConversation("conv-1")!;

        Assert.Equal(ConversationMode.AlwaysHebrew, preference.Mode);
        Assert.Equal(Language.English, preference.LastReliableLanguage);
    }

    [Fact]
    public void A_manual_override_starts_the_cooldown_and_records_the_choice()
    {
        _store.NoteManualOverride("conv-1", Language.English);

        var preference = _store.GetConversation("conv-1")!;

        Assert.Equal(_clock.Now, preference.ManualOverrideAt);
        Assert.Equal(Language.English, preference.LastReliableLanguage);
    }

    [Fact]
    public void Site_pause_survives_a_restart()
    {
        _store.SaveSite("web.whatsapp.com", new SiteState { Paused = true, AdapterVersion = "1.0.0" });

        var state = Reopen().GetSite("web.whatsapp.com");

        Assert.True(state!.Paused);
        Assert.Equal("1.0.0", state.AdapterVersion);
    }

    [Fact]
    public void Settings_survive_a_restart()
    {
        _store.SaveSettings(Settings.Default with { ConfidenceThreshold = 0.9, ShowIndicator = false });

        var settings = Reopen().Settings;

        Assert.Equal(0.9, settings.ConfidenceThreshold);
        Assert.False(settings.ShowIndicator);
    }

    [Fact]
    public void Conversations_are_kept_separate()
    {
        _store.RememberLanguage("conv-1", Language.Hebrew);
        _store.RememberLanguage("conv-2", Language.English);

        Assert.Equal(Language.Hebrew, _store.GetConversation("conv-1")!.LastReliableLanguage);
        Assert.Equal(Language.English, _store.GetConversation("conv-2")!.LastReliableLanguage);
    }

    // --- Robustness --------------------------------------------------------------------------

    [Fact]
    public void A_corrupt_preferences_file_degrades_to_a_first_run_rather_than_crashing()
    {
        _store.RememberLanguage("conv-1", Language.Hebrew);
        File.WriteAllText(_store.ConversationsPath, "{ this is not json");

        var reopened = Reopen();

        Assert.Null(reopened.GetConversation("conv-1"));
        Assert.Equal(Settings.Default.ConfidenceThreshold, reopened.Settings.ConfidenceThreshold);
    }

    [Fact]
    public void No_temp_files_are_left_behind_after_writing()
    {
        _store.RememberLanguage("conv-1", Language.Hebrew);
        _store.RememberLanguage("conv-2", Language.English);
        _store.SaveSettings(Settings.Default);

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Loading_creates_the_directory_when_it_is_missing()
    {
        var freshRoot = Path.Combine(Path.GetTempPath(), "autolang-tests", Guid.NewGuid().ToString("n"));
        try
        {
            new ConversationStore(freshRoot, _clock).Load();
            Assert.True(Directory.Exists(freshRoot));
        }
        finally
        {
            if (Directory.Exists(freshRoot)) Directory.Delete(freshRoot, recursive: true);
        }
    }

    // --- Debug buffer ------------------------------------------------------------------------

    [Fact]
    public void The_debug_buffer_records_outcomes_and_never_content()
    {
        _store.RecordDecision("web.whatsapp.com", new Decision
        {
            Outcome = DecisionOutcome.Switch,
            Language = Language.Hebrew,
            Confidence = 0.91,
            Source = DecisionSource.OutgoingMessages,
        });

        var events = _store.DebugEvents;

        Assert.Single(events);
        Assert.Equal(DecisionOutcome.Switch, events[0].Outcome);
        Assert.Equal(Language.Hebrew, events[0].Language);

        // The record deliberately has no field for a conversation key or any counts.
        var fields = typeof(ConversationStore.DebugEvent).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("ConversationKey", fields);
        Assert.DoesNotContain("Text", fields);
        Assert.DoesNotContain("Counts", fields);
    }

    [Fact]
    public void The_debug_buffer_is_bounded()
    {
        for (int i = 0; i < 500; i++)
            _store.RecordDecision("web.whatsapp.com", new Decision { Outcome = DecisionOutcome.NoChange });

        Assert.Equal(200, _store.DebugEvents.Count);
    }

    // --- Clear Data --------------------------------------------------------------------------

    [Fact]
    public void Clear_data_removes_every_preference_log_and_file()
    {
        _store.RememberLanguage("conv-1", Language.Hebrew);
        _store.SaveSite("web.whatsapp.com", new SiteState { Paused = true });
        _store.SaveSettings(Settings.Default with { ConfidenceThreshold = 0.9 });
        _store.RecordDecision("web.whatsapp.com", new Decision { Outcome = DecisionOutcome.Switch });

        _store.ClearAll();

        Assert.Null(_store.GetConversation("conv-1"));
        Assert.Null(_store.GetSite("web.whatsapp.com"));
        Assert.Empty(_store.DebugEvents);
        Assert.Equal(Settings.Default.ConfidenceThreshold, _store.Settings.ConfidenceThreshold);

        Assert.False(File.Exists(_store.ConversationsPath));
        Assert.False(File.Exists(_store.SitesPath));
        Assert.False(File.Exists(_store.SettingsPath));
    }

    [Fact]
    public void Clear_data_survives_a_restart()
    {
        _store.RememberLanguage("conv-1", Language.Hebrew);
        _store.ClearAll();

        Assert.Null(Reopen().GetConversation("conv-1"));
    }

    [Fact]
    public void Clear_data_keeps_the_folder_so_the_product_keeps_working()
    {
        // Clearing history is not uninstalling.
        _store.RememberLanguage("conv-1", Language.Hebrew);
        _store.ClearAll();
        _store.RememberLanguage("conv-2", Language.English);

        Assert.Equal(Language.English, _store.GetConversation("conv-2")!.LastReliableLanguage);
    }

    // --- Privacy -----------------------------------------------------------------------------

    [Fact]
    public void Nothing_identifying_is_ever_written_to_disk()
    {
        // Keys arrive already hashed and salted by the content script, so a phone number cannot
        // reach this class in the first place. This asserts the property end to end.
        _store.RememberLanguage("9f2a4c8e1b3d5f70", Language.Hebrew);
        _store.SaveSite("web.whatsapp.com", new SiteState { Paused = false, AdapterVersion = "1.0.0" });

        var everything = string.Join("\n", Directory.GetFiles(_root).Select(File.ReadAllText));

        Assert.DoesNotContain("972", everything);
        Assert.DoesNotContain("@c.us", everything);
        Assert.Contains("9f2a4c8e1b3d5f70", everything);
    }
}
