using System.Text.Json.Serialization;

namespace AutoLang.Core;

/// <summary>
/// Source-generated serialisation for everything that crosses a process boundary or reaches disk.
///
/// Not an optimisation. Publishing trimmed turns reflection-based serialisation off outright -
/// the first framed message threw "Reflection-based serialization has been disabled for this
/// application" and the Agent died before answering anything. Rooting the assembly does not help,
/// because the block is a feature switch rather than the trimmer removing types.
///
/// Setting these as the options' TypeInfoResolver is enough on its own: call sites keep using the
/// ordinary generic Serialize and Deserialize, and the resolver answers instead of reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireEnvelope))]
[JsonSerializable(typeof(SignalMessage))]
[JsonSerializable(typeof(HealthMessage))]
[JsonSerializable(typeof(CommandMessage))]
[JsonSerializable(typeof(QueryMessage))]
[JsonSerializable(typeof(DecisionMessage))]
[JsonSerializable(typeof(StateMessage))]
[JsonSerializable(typeof(ErrorMessage))]
public partial class WireJsonContext : JsonSerializerContext;

/// <summary>
/// The same, for what the store writes to %LOCALAPPDATA%.
///
/// Kept separate from the wire context because the two have different shapes on purpose: stored
/// files are PascalCase and human-readable for anyone auditing what the product keeps, while the
/// wire is camelCase to match the extension.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(Dictionary<string, ConversationPreference>))]
[JsonSerializable(typeof(Dictionary<string, SiteState>))]
[JsonSerializable(typeof(Dictionary<string, AppState>))]
public partial class StoreJsonContext : JsonSerializerContext;
