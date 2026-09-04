using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoLang.Core;

/// <summary>
/// Local state: settings, per-conversation preferences, and a text-free debug ring buffer.
///
/// The privacy properties are structural, not procedural. Conversation keys arrive already hashed
/// and salted by the content script, so this class has no way to write a phone number even if it
/// tried - it never sees one. The debug buffer records scores and outcomes, never counts tied to
/// content and never text.
///
/// Writes are atomic (temp file then replace). A power cut mid-write costs the last change, not
/// the whole file, which matters because a corrupt preferences file would silently disable the
/// product's memory for every conversation at once.
/// </summary>
public sealed class ConversationStore
{
    private const int DebugBufferCapacity = 200;

    // See JsonContexts.cs: a trimmed build has reflection-based serialisation switched off, so the
    // resolver is what makes reading and writing these files work at all.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = StoreJsonContext.Default
    };

    private readonly string _root;
    private readonly IClock _clock;
    private readonly object _gate = new();

    private readonly Queue<DebugEvent> _debugEvents = new();
    private Dictionary<string, ConversationPreference> _conversations = [];
    private Dictionary<string, SiteState> _sites = [];
    private Dictionary<string, AppState> _apps = [];
    private Settings _settings = Settings.Default;

    public ConversationStore(string? root = null, IClock? clock = null)
    {
        _root = root ?? DefaultRoot();
        _clock = clock ?? SystemClock.Instance;
    }

    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoLang");

    public string Root => _root;
    public string SettingsPath => Path.Combine(_root, "settings.json");
    public string ConversationsPath => Path.Combine(_root, "conversations.json");
    public string SitesPath => Path.Combine(_root, "sites.json");

    /// <summary>
    /// The applications the user has allowed the Agent to watch.
    ///
    /// Only what they added. An application that is running and not in here is not recorded
    /// anywhere - not its name, not that it was seen. That is the whole difference between an
    /// allowlist and a log of everything somebody opens.
    /// </summary>
    public string AppsPath => Path.Combine(_root, "apps.json");

    public Settings Settings
    {
        get { lock (_gate) return _settings; }
    }

    public void Load()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            _settings = ReadOrDefault(SettingsPath, Settings.Default);
            _conversations = ReadOrDefault<Dictionary<string, ConversationPreference>>(ConversationsPath, []);
            _sites = ReadOrDefault<Dictionary<string, SiteState>>(SitesPath, []);
            _apps = ReadOrDefault<Dictionary<string, AppState>>(AppsPath, []);

            // Generated on first use rather than at install, so a machine that never watches a
            // desktop application never has one written.
            if (string.IsNullOrEmpty(_settings.DesktopSalt))
            {
                _settings = _settings with { DesktopSalt = DesktopIdentity.NewSalt() };
                Write(SettingsPath, _settings);
            }
        }
    }

    public AppState? GetApp(string processName)
    {
        lock (_gate) return _apps.GetValueOrDefault(processName.ToLowerInvariant());
    }

    /// <summary>Every application the user has allowed, for the settings page to show back to them.</summary>
    public IReadOnlyList<string> AllowedApps()
    {
        lock (_gate) return _apps.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public void AllowApp(string processName)
    {
        lock (_gate)
        {
            var key = processName.ToLowerInvariant();
            if (!_apps.ContainsKey(key)) _apps[key] = new AppState { AllowedAt = _clock.Now };
            Write(AppsPath, _apps);
        }
    }

    /// <summary>
    /// Withdraws an application, and forgets it entirely.
    ///
    /// Removed rather than marked as blocked. A blocked entry would leave a permanent record that
    /// somebody once ran it, which is the thing the allowlist exists to avoid.
    /// </summary>
    public void BlockApp(string processName)
    {
        lock (_gate)
        {
            _apps.Remove(processName.ToLowerInvariant());
            Write(AppsPath, _apps);
        }
    }

    public ConversationPreference? GetConversation(string conversationKey)
    {
        lock (_gate) return _conversations.GetValueOrDefault(conversationKey);
    }

    public SiteState? GetSite(string origin)
    {
        lock (_gate) return _sites.GetValueOrDefault(origin);
    }

    public void SaveSettings(Settings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            Write(SettingsPath, settings);
        }
    }

    public void SaveSite(string origin, SiteState state)
    {
        lock (_gate)
        {
            _sites[origin] = state;
            Write(SitesPath, _sites);
        }
    }

    /// <summary>Records the layout the user was actually typing with in this conversation.</summary>
    public void RememberLanguage(string conversationKey, Language language)
    {
        if (language == Language.Unknown) return;

        Update(conversationKey, existing => existing with
        {
            LastReliableLanguage = language,
            UpdatedAt = _clock.Now
        });
    }

    /// <summary>
    /// Pins a conversation to a language, or releases it back to Auto.
    ///
    /// The language is stored alongside the mode rather than encoded in it, which is what lets a
    /// pin name any of the languages the product knows instead of the two the mode used to spell.
    /// Releasing clears the language too, so a stored file never carries a pin nobody asked for.
    /// </summary>
    public void SetMode(string conversationKey, ConversationMode mode, Language language = Language.Unknown) =>
        Update(conversationKey, existing => existing with
        {
            Mode = mode,
            PinnedLanguage = mode == ConversationMode.Pinned ? language : Language.Unknown,
            UpdatedAt = _clock.Now
        });

    /// <summary>Starts the cooldown that stops the product arguing with a user who just overrode it.</summary>
    public void NoteManualOverride(string conversationKey, Language language)
    {
        Update(conversationKey, existing => existing with
        {
            ManualOverrideAt = _clock.Now,
            LastReliableLanguage = language == Language.Unknown ? existing.LastReliableLanguage : language,
            UpdatedAt = _clock.Now
        });
    }

    private void Update(string conversationKey, Func<ConversationPreference, ConversationPreference> mutate)
    {
        lock (_gate)
        {
            var existing = _conversations.GetValueOrDefault(conversationKey) ?? new ConversationPreference();
            _conversations[conversationKey] = mutate(existing);
            DropExpired();
            Write(ConversationsPath, _conversations);
        }
    }

    /// <summary>
    /// Forgets what the engine has already stopped believing.
    ///
    /// Nothing here changes a decision: past <see cref="Settings.MemoryTtl"/> the engine ignores a
    /// remembered language and reads the conversation afresh, so these entries are already inert.
    /// What this stops is the file growing forever.
    ///
    /// That mattered little when a key was a chat. It matters now that a key is one document in a
    /// site - a single mail thread, a single document - because a heavy mail user opens thousands
    /// of them, and every one would otherwise be kept for good.
    ///
    /// A pinned conversation is never dropped, however old. The TTL exists because an observation
    /// goes stale; a pin is an instruction, and the user is entitled to expect it to hold.
    /// </summary>
    private void DropExpired()
    {
        var cutoff = _clock.Now - _settings.MemoryTtl;

        var stale = _conversations
            .Where(entry => entry.Value.Mode == ConversationMode.Auto && entry.Value.UpdatedAt < cutoff)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var key in stale) _conversations.Remove(key);
    }

    // --- Debug buffer ------------------------------------------------------------------------

    /// <summary>
    /// One decision, recorded for troubleshooting. Deliberately holds no counts, no conversation
    /// key and no text - only what the engine concluded and why.
    /// </summary>
    public sealed record DebugEvent(
        DateTimeOffset At,
        string Site,
        DecisionOutcome Outcome,
        Language Language,
        double Confidence,
        DecisionSource Source,
        DecisionBlocker Blocker);

    public void RecordDecision(string site, Decision decision)
    {
        lock (_gate)
        {
            _debugEvents.Enqueue(new DebugEvent(
                _clock.Now, site, decision.Outcome, decision.Language,
                decision.Confidence, decision.Source, decision.Blocker));

            while (_debugEvents.Count > DebugBufferCapacity) _debugEvents.Dequeue();
        }
    }

    public IReadOnlyList<DebugEvent> DebugEvents
    {
        get { lock (_gate) return _debugEvents.ToList(); }
    }

    // --- Clear Data --------------------------------------------------------------------------

    /// <summary>
    /// The "Clear Data" action of PDR section 11. Removes every stored preference and log, and
    /// leaves settings at their defaults rather than deleting the folder outright - a user
    /// clearing their history is not asking to uninstall.
    /// </summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            _conversations.Clear();
            _sites.Clear();
            _apps.Clear();
            _debugEvents.Clear();

            // The salt goes with everything else. Keeping it would leave the next desktop window
            // hashing to the same key as one the user just asked to forget.
            _settings = Settings.Default;

            foreach (var path in new[] { ConversationsPath, SitesPath, AppsPath, SettingsPath })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- Persistence -------------------------------------------------------------------------

    private T ReadOrDefault<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable file must not take the product down. Losing preferences
            // degrades to "analyse from scratch", which is exactly the first-run experience.
            return fallback;
        }
    }

    private void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(_root);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));

        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
}
