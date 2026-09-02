using System.Text.Json;
using AutoLang.Core;

namespace AutoLang.Agent.Tests;

/// <summary>
/// The Agent refuses any conversation key that is not a salted hash.
///
/// This exists because the guarantee used to rest entirely on the content script choosing to hash.
/// Anything else speaking the protocol could write an identifier straight into conversations.json,
/// and something did: a pipe test using a plain Hebrew string as a key left it sitting in the real
/// store on this machine. The content script is still the thing that hashes; this is what makes it
/// impossible for anything else not to.
/// </summary>
public class ConversationKeyTests : IDisposable
{
    private readonly string _root;
    private readonly TestClock _clock = new();
    private readonly FakeLayoutService _layouts = new();
    private readonly ConversationStore _store;
    private readonly AgentCore _core;

    private const string ValidKey = "9f2a4c8e1b3d5f7009f2a4c8e1b3d5f7";

    public ConversationKeyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "autolang-key-tests", Guid.NewGuid().ToString("n"));
        _store = new ConversationStore(_root, _clock);
        _store.Load();
        _core = new AgentCore(_store, _layouts, new DecisionEngine(_clock), _clock);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string SignalWith(string key) => JsonSerializer.Serialize(new SignalMessage
    {
        Site = "web.whatsapp.com",
        ConversationKey = key,
        ObservedAt = _clock.Now.ToUnixTimeMilliseconds(),
        Messages = [new WireMessageStats { Direction = "outgoing", Counts = new() { ["Hebrew"] = 30 } }],
    }, Wire.Json);

    [Theory]
    [InlineData("972500000000@c.us")]                    // a phone number, the thing this prevents
    [InlineData("Supplier Group")]                       // a contact name
    [InlineData("שיחה-בעברית")]                          // the key a test actually leaked
    [InlineData("conv-1")]                               // too short
    [InlineData("9F2A4C8E1B3D5F7009F2A4C8E1B3D5F7")]     // uppercase is not what we produce
    [InlineData("9f2a4c8e1b3d5f7009f2a4c8e1b3d5f7a")]    // one character too long
    [InlineData("9f2a4c8e1b3d5f7009f2a4c8e1b3d5g7")]     // g is not hex
    [InlineData("")]
    public void A_key_that_is_not_a_salted_hash_is_refused(string key)
    {
        var reply = JsonSerializer.Deserialize<ErrorMessage>(_core.Handle(SignalWith(key))!, Wire.Json)!;

        Assert.Equal(ErrorCodes.BadMessage, reply.Code);
        Assert.Empty(_layouts.SwitchRequests);
    }

    [Fact]
    public void The_rejection_does_not_echo_the_key_back()
    {
        // Refusing to store something identifying and then repeating it in an error message would
        // defeat the point - error text ends up in logs and bug reports.
        var reply = _core.Handle(SignalWith("972500000000@c.us"))!;

        Assert.DoesNotContain("972500000000", reply);
        Assert.DoesNotContain("@c.us", reply);
    }

    [Fact]
    public void Nothing_is_written_for_a_refused_key()
    {
        _core.Handle(SignalWith("Supplier Group"));

        Assert.Null(_store.GetConversation("Supplier Group"));

        var files = Directory.Exists(_root) ? Directory.GetFiles(_root) : [];
        foreach (var file in files)
            Assert.DoesNotContain("Supplier", File.ReadAllText(file));
    }

    [Fact]
    public void A_properly_hashed_key_is_accepted()
    {
        var reply = JsonSerializer.Deserialize<DecisionMessage>(_core.Handle(SignalWith(ValidKey))!, Wire.Json)!;

        Assert.Equal("Switch", reply.Outcome);
    }

    [Fact]
    public void Pinning_a_conversation_is_refused_for_an_unhashed_key()
    {
        var command = JsonSerializer.Serialize(new CommandMessage
        {
            Command = "setMode",
            ConversationKey = "Supplier Group",
            Mode = "AlwaysHebrew",
        }, Wire.Json);

        var reply = JsonSerializer.Deserialize<ErrorMessage>(_core.Handle(command)!, Wire.Json)!;

        Assert.Equal(ErrorCodes.BadMessage, reply.Code);
        Assert.Null(_store.GetConversation("Supplier Group"));
    }

    [Fact]
    public void The_content_scripts_own_output_shape_is_accepted()
    {
        // hash.ts takes 32 characters of a SHA-256 hex digest. This asserts the two agree, which
        // is the whole point of validating a shape rather than a value.
        var digest = System.Security.Cryptography.SHA256.HashData("salt:jid:972500000000@c.us"u8.ToArray());
        var key = Convert.ToHexString(digest).ToLowerInvariant()[..32];

        var reply = JsonSerializer.Deserialize<DecisionMessage>(_core.Handle(SignalWith(key))!, Wire.Json)!;

        Assert.Equal("Switch", reply.Outcome);
    }
}
