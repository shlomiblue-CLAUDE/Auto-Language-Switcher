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

    public void SetMode(string conversationKey, ConversationMode mode) =>
        Update(conversationKey, existing => existing with { Mode = mode, UpdatedAt = _clock.Now });

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
            Write(ConversationsPath, _conversations);
        }
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
            _debugEvents.Clear();
            _settings = Settings.Default;

            foreach (var path in new[] { ConversationsPath, SitesPath, SettingsPath })
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
