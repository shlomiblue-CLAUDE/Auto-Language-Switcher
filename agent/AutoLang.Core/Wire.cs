using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoLang.Core;

/// <summary>
/// The messages that travel extension -> service worker -> Bridge -> Agent and back.
///
/// This mirrors extension/src/shared/protocol.ts. As there, the privacy guarantee is structural:
/// no type below has a field that could carry message text, a contact name or a phone number.
/// A leak would have to add one first, which is a visible change to a reviewed contract.
///
/// Note what is deliberately ABSENT from the inbound signal: whether the browser is in the
/// foreground, and which layout is currently active. A page cannot know either, and a compromised
/// one could lie about both. The Agent asks Windows directly instead.
/// </summary>
public static class Wire
{
    public const int ProtocolVersion = 1;

    // TypeInfoResolver, not reflection: a trimmed build disables reflection-based serialisation
    // entirely, and without this the Agent throws on the first message it is asked to write.
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = WireJsonContext.Default
    };

    /// <summary>Chrome's own cap on a message from an extension. Anything larger is not ours.</summary>
    public const int MaxMessageBytes = 1024 * 1024;
}

public enum WireMessageKind
{
    Unknown,
    Signal,
    Health,
    Command,
    Query,
    Decision,
    State,
    Error
}

/// <summary>Only the discriminator, so a message can be routed before it is fully parsed.</summary>
public sealed class WireEnvelope
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; }

    public WireMessageKind Kind => Type switch
    {
        "signal" => WireMessageKind.Signal,
        "health" => WireMessageKind.Health,
        "command" => WireMessageKind.Command,
        "query" => WireMessageKind.Query,
        _ => WireMessageKind.Unknown
    };
}

public sealed class WireMessageStats
{
    /// <summary>Language name to letter count, e.g. {"Hebrew": 12}.</summary>
    [JsonPropertyName("counts")] public Dictionary<string, int> Counts { get; set; } = [];

    [JsonPropertyName("direction")] public string Direction { get; set; } = "outgoing";
    [JsonPropertyName("index")] public int Index { get; set; }

    public MessageObservation ToObservation()
    {
        var counts = new Dictionary<Language, int>();
        foreach (var (name, value) in Counts)
        {
            if (value > 0 && Enum.TryParse<Language>(name, ignoreCase: true, out var language) && language != Language.Unknown)
                counts[language] = value;
        }

        var direction = Direction.Equals("incoming", StringComparison.OrdinalIgnoreCase)
            ? MessageDirection.Incoming
            : MessageDirection.Outgoing;

        return new MessageObservation(direction, new MessageStats(counts), Index);
    }
}

public sealed class SignalMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "signal";
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; } = Wire.ProtocolVersion;
    [JsonPropertyName("source")] public string Source { get; set; } = "browser";
    [JsonPropertyName("site")] public string Site { get; set; } = "";
    [JsonPropertyName("conversationKey")] public string ConversationKey { get; set; } = "";
    [JsonPropertyName("adapterVersion")] public string AdapterVersion { get; set; } = "";
    [JsonPropertyName("messages")] public List<WireMessageStats> Messages { get; set; } = [];
    [JsonPropertyName("composerEmpty")] public bool ComposerEmpty { get; set; } = true;

    /// <summary>Unix milliseconds. Used to reject stale events, per PDR section 9.</summary>
    [JsonPropertyName("observedAt")] public long ObservedAt { get; set; }
}

public sealed class HealthMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "health";
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; } = Wire.ProtocolVersion;
    [JsonPropertyName("site")] public string Site { get; set; } = "";
    [JsonPropertyName("adapterVersion")] public string AdapterVersion { get; set; } = "";
    [JsonPropertyName("healthy")] public bool Healthy { get; set; }
    [JsonPropertyName("missing")] public List<string> Missing { get; set; } = [];
    [JsonPropertyName("tiers")] public Dictionary<string, int> Tiers { get; set; } = [];
}

public sealed class CommandMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "command";
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; } = Wire.ProtocolVersion;

    /// <summary>setMode | pauseSite | resumeSite | setEnabled | clearData | noteManualChange</summary>
    [JsonPropertyName("command")] public string Command { get; set; } = "";

    [JsonPropertyName("conversationKey")] public string? ConversationKey { get; set; }
    [JsonPropertyName("site")] public string? Site { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("language")] public string? LanguageTag { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }

    // Settings, sent together by the options page so a half-applied change cannot exist.
    [JsonPropertyName("defaultLanguage")] public string? DefaultLanguage { get; set; }
    [JsonPropertyName("confidenceThreshold")] public double? ConfidenceThreshold { get; set; }
    [JsonPropertyName("showIndicator")] public bool? ShowIndicator { get; set; }
}

public sealed class QueryMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "query";
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; } = Wire.ProtocolVersion;
    [JsonPropertyName("query")] public string Query { get; set; } = "state";
    [JsonPropertyName("conversationKey")] public string? ConversationKey { get; set; }
    [JsonPropertyName("site")] public string? Site { get; set; }
}

public sealed class DecisionMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "decision";
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; } = Wire.ProtocolVersion;
    [JsonPropertyName("conversationKey")] public string ConversationKey { get; set; } = "";
    [JsonPropertyName("language")] public string Language { get; set; } = "unknown";
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("outcome")] public string Outcome { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("blocker")] public string Blocker { get; set; } = "";
    [JsonPropertyName("applied")] public bool Applied { get; set; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
}

public sealed class StateMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "state";
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; } = Wire.ProtocolVersion;
    [JsonPropertyName("agentVersion")] public string AgentVersion { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("currentLayout")] public string CurrentLayout { get; set; } = "unknown";
    [JsonPropertyName("conversationMode")] public string ConversationMode { get; set; } = "Auto";
    [JsonPropertyName("rememberedLanguage")] public string RememberedLanguage { get; set; } = "unknown";
    [JsonPropertyName("sitePaused")] public bool SitePaused { get; set; }
    [JsonPropertyName("availableLayouts")] public List<string> AvailableLayouts { get; set; } = [];
    [JsonPropertyName("defaultLanguage")] public string DefaultLanguage { get; set; } = "unknown";
    [JsonPropertyName("confidenceThreshold")] public double ConfidenceThreshold { get; set; }
    [JsonPropertyName("showIndicator")] public bool ShowIndicator { get; set; }
    [JsonPropertyName("lastDecision")] public DecisionMessage? LastDecision { get; set; }
}

public sealed class ErrorMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "error";
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; } = Wire.ProtocolVersion;
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public static class ErrorCodes
{
    public const string LayoutNotInstalled = "LAYOUT_NOT_INSTALLED";
    public const string NotForeground = "NOT_FOREGROUND";
    public const string StaleEvent = "STALE_EVENT";
    public const string SwitchFailed = "SWITCH_FAILED";
    public const string BadMessage = "BAD_MESSAGE";
    public const string UnsupportedProtocol = "UNSUPPORTED_PROTOCOL";
}
