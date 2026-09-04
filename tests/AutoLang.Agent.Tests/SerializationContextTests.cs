using System.Text.Json;
using AutoLang.Core;

namespace AutoLang.Agent.Tests;

/// <summary>
/// Guards the source-generated serialisation contexts.
///
/// A trimmed build has reflection-based serialisation switched off entirely, so a type missing
/// from a context does not degrade - it throws on first use, in the published build only, at the
/// moment the user needs it. The compiler cannot catch that: adding a message type and forgetting
/// its [JsonSerializable] line compiles cleanly and passes every other test.
///
/// This is also what justifies suppressing IL2026 in the csproj. The warning says the trimmer
/// cannot prove these calls are safe; these tests prove it instead, by exercising every type
/// through the same options the product uses.
/// </summary>
public class SerializationContextTests
{
    private static void RoundTrips<T>(T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, Wire.Json);
        Assert.NotNull(json);

        var back = JsonSerializer.Deserialize<T>(json, Wire.Json);
        Assert.NotNull(back);
    }

    [Fact]
    public void Every_inbound_wire_type_round_trips()
    {
        RoundTrips(new WireEnvelope { Type = "signal", ProtocolVersion = 1 });
        RoundTrips(new SignalMessage
        {
            ConversationKey = "abc",
            Messages = [new WireMessageStats { Counts = new() { ["Hebrew"] = 3 } }],
        });
        RoundTrips(new HealthMessage { Missing = ["mainPanel"], Tiers = new() { ["composer"] = 0 } });
        RoundTrips(new CommandMessage { Command = "setMode", Mode = "Pinned", LanguageTag = "he-IL" });
        RoundTrips(new QueryMessage { Query = "state" });
    }

    [Fact]
    public void Every_outbound_wire_type_round_trips()
    {
        RoundTrips(new DecisionMessage { ConversationKey = "abc", Language = "he-IL" });
        RoundTrips(new StateMessage
        {
            AvailableLayouts = ["he-IL", "en-US"],
            LastDecision = new DecisionMessage { Language = "en-US" },
        });
        RoundTrips(new ErrorMessage { Code = ErrorCodes.StaleEvent, Message = "too old" });
    }

    [Fact]
    public void Nested_types_survive_the_round_trip_with_their_values()
    {
        var state = new StateMessage
        {
            AgentVersion = "0.1.0",
            Enabled = true,
            ConfidenceThreshold = 0.7,
            ShowIndicator = true,
            AvailableLayouts = ["he-IL", "en-US"],
            LastDecision = new DecisionMessage { Language = "he-IL", Confidence = 0.94, Applied = true },
        };

        var back = JsonSerializer.Deserialize<StateMessage>(JsonSerializer.Serialize(state, Wire.Json), Wire.Json)!;

        // Asserting values, not just non-null: a resolver that silently produces an empty object
        // would pass a null check and lose every setting.
        Assert.Equal(0.7, back.ConfidenceThreshold);
        Assert.True(back.ShowIndicator);
        Assert.Equal(["he-IL", "en-US"], back.AvailableLayouts);
        Assert.Equal("he-IL", back.LastDecision!.Language);
        Assert.Equal(0.94, back.LastDecision.Confidence);
    }

    [Fact]
    public void Dictionaries_inside_messages_survive()
    {
        var signal = new SignalMessage
        {
            ConversationKey = "abc",
            Messages =
            [
                new WireMessageStats { Direction = "outgoing", Index = 0, Counts = new() { ["Hebrew"] = 12, ["English"] = 3 } },
            ],
        };

        var back = JsonSerializer.Deserialize<SignalMessage>(JsonSerializer.Serialize(signal, Wire.Json), Wire.Json)!;

        Assert.Equal(12, back.Messages[0].Counts["Hebrew"]);
        Assert.Equal(3, back.Messages[0].Counts["English"]);
    }

    [Fact]
    public void Enums_in_stored_records_are_written_as_names_not_numbers()
    {
        // Numbers would make the stored files meaningless to anyone auditing what is kept, and
        // would silently reinterpret every preference if the enum order ever changed.
        var root = Path.Combine(Path.GetTempPath(), "autolang-json-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var store = new ConversationStore(root, new TestClock());
            store.Load();
            store.SetMode("conv-1", ConversationMode.Pinned, Language.Hebrew);
            store.RememberLanguage("conv-1", Language.Hebrew);
            store.SaveSettings(Settings.Default with { DefaultLanguage = Language.English });

            var conversations = File.ReadAllText(store.ConversationsPath);
            Assert.Contains("Pinned", conversations);
            Assert.Contains("Hebrew", conversations);
            Assert.Contains("Hebrew", conversations);

            Assert.Contains("English", File.ReadAllText(store.SettingsPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Stored_records_reload_with_their_values()
    {
        var root = Path.Combine(Path.GetTempPath(), "autolang-json-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var clock = new TestClock();
            var store = new ConversationStore(root, clock);
            store.Load();
            store.SetMode("conv-1", ConversationMode.Pinned, Language.English);
            store.SaveSettings(Settings.Default with { ConfidenceThreshold = 0.85, DefaultLanguage = Language.Hebrew });

            var reopened = new ConversationStore(root, clock);
            reopened.Load();

            Assert.Equal(ConversationMode.Pinned, reopened.GetConversation("conv-1")!.Mode);
            Assert.Equal(Language.English, reopened.GetConversation("conv-1")!.PinnedLanguage);
            Assert.Equal(0.85, reopened.Settings.ConfidenceThreshold);
            Assert.Equal(Language.Hebrew, reopened.Settings.DefaultLanguage);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
