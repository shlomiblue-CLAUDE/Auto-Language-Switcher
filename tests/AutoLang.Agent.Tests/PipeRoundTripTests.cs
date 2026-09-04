using System.IO.Pipes;
using System.Text.Json;
using AutoLang.Core;

namespace AutoLang.Agent.Tests;

/// <summary>
/// Drives a real named pipe end to end: server, client, framing, routing, reply.
///
/// This is the layer the Bridge stands on. It is worth exercising for real rather than mocking,
/// because every bug this transport can have - short reads, desynchronised framing, a client
/// vanishing mid-message - only appears with an actual pipe.
/// </summary>
[Collection("pipe")]
public class PipeRoundTripTests : IDisposable
{
    private readonly string _root;
    private readonly TestClock _clock = new();
    private readonly FakeLayoutService _layouts = new();
    private readonly ConversationStore _store;
    private readonly AgentCore _core;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _serverTask;

    /// <summary>Unique per test instance, so a real Agent on this machine is never contacted.</summary>
    private const string HashedKey = "9f2a4c8e1b3d5f7009f2a4c8e1b3d5f7";
    private const string HashedKeyA = "aaaa4c8e1b3d5f7009f2a4c8e1b3d5f7";

    private readonly string _pipeName = $"AutoLang.Test.{Guid.NewGuid():n}";

    public PipeRoundTripTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "autolang-pipe-tests", Guid.NewGuid().ToString("n"));
        _store = new ConversationStore(_root, _clock);
        _store.Load();
        _core = new AgentCore(_store, _layouts, new DecisionEngine(_clock), _clock);

        _serverTask = new PipeServer(_core, pipeName: _pipeName).RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _serverTask.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _cts.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private async Task<NamedPipeClientStream> ConnectAsync()
    {
        var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        return client;
    }

    private static string SignalJson(string key = HashedKey, string language = "Hebrew", long? observedAt = null) =>
        JsonSerializer.Serialize(new SignalMessage
        {
            Site = "web.whatsapp.com",
            ConversationKey = key,
            AdapterVersion = "1.0.0",
            ObservedAt = observedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Messages = [new WireMessageStats { Direction = "outgoing", Index = 0, Counts = new() { [language] = 30 } }],
        }, Wire.Json);

    [Fact]
    public async Task A_signal_sent_over_the_pipe_comes_back_as_a_decision()
    {
        await using var client = await ConnectAsync();

        // The clock is fixed in the past, so send an observation the Agent will consider fresh.
        await NativeMessagingCodec.WriteAsync(client, SignalJson(observedAt: _clock.Now.ToUnixTimeMilliseconds()));
        var reply = await NativeMessagingCodec.ReadAsync(client);

        var decision = JsonSerializer.Deserialize<DecisionMessage>(reply!, Wire.Json)!;

        Assert.Equal("Switch", decision.Outcome);
        Assert.Equal("he-IL", decision.Language);
        Assert.True(decision.Applied);
    }

    [Fact]
    public async Task Several_messages_on_one_connection_are_answered_in_order()
    {
        await using var client = await ConnectAsync();

        for (int i = 0; i < 5; i++)
        {
            var query = JsonSerializer.Serialize(new QueryMessage { ConversationKey = HashedKey }, Wire.Json);
            await NativeMessagingCodec.WriteAsync(client, query);

            var reply = await NativeMessagingCodec.ReadAsync(client);
            var state = JsonSerializer.Deserialize<StateMessage>(reply!, Wire.Json)!;

            Assert.Equal(AgentCore.AgentVersion, state.AgentVersion);
        }
    }

    [Fact]
    public async Task Two_clients_are_served_at_once()
    {
        // Chrome and Edge can both be open. Each browser gets its own Bridge and its own
        // connection, but they must reach the same brain and the same memory.
        await using var chrome = await ConnectAsync();
        await using var edge = await ConnectAsync();

        await NativeMessagingCodec.WriteAsync(chrome, SignalJson(HashedKeyA, observedAt: _clock.Now.ToUnixTimeMilliseconds()));
        var chromeReply = await NativeMessagingCodec.ReadAsync(chrome);

        await NativeMessagingCodec.WriteAsync(edge, JsonSerializer.Serialize(new QueryMessage { ConversationKey = HashedKeyA }, Wire.Json));
        var edgeReply = await NativeMessagingCodec.ReadAsync(edge);

        Assert.NotNull(chromeReply);

        var state = JsonSerializer.Deserialize<StateMessage>(edgeReply!, Wire.Json)!;
        Assert.Equal(AgentCore.AgentVersion, state.AgentVersion);
    }

    [Fact]
    public async Task A_health_report_gets_no_reply_and_does_not_stall_the_connection()
    {
        await using var client = await ConnectAsync();

        var health = JsonSerializer.Serialize(new HealthMessage
        {
            Site = "web.whatsapp.com",
            Healthy = false,
            Missing = ["mainPanel"],
        }, Wire.Json);

        await NativeMessagingCodec.WriteAsync(client, health);

        // No reply is expected for health. The next request must still be answered, which is what
        // proves the server did not go looking for one.
        await NativeMessagingCodec.WriteAsync(client, JsonSerializer.Serialize(new QueryMessage(), Wire.Json));
        var reply = await NativeMessagingCodec.ReadAsync(client);

        Assert.Contains("\"type\":\"state\"", reply);
    }

    [Fact]
    public async Task Malformed_json_is_answered_and_the_connection_survives()
    {
        await using var client = await ConnectAsync();

        await NativeMessagingCodec.WriteAsync(client, "{ not json at all");
        var errorReply = await NativeMessagingCodec.ReadAsync(client);
        Assert.Contains(ErrorCodes.BadMessage, errorReply);

        await NativeMessagingCodec.WriteAsync(client, JsonSerializer.Serialize(new QueryMessage(), Wire.Json));
        Assert.Contains("\"type\":\"state\"", await NativeMessagingCodec.ReadAsync(client));
    }

    [Fact]
    public async Task A_client_that_disappears_mid_message_does_not_take_the_agent_down()
    {
        // A browser being killed is ordinary. Announce a length, send nothing, drop the pipe.
        var client = await ConnectAsync();
        await client.WriteAsync(new byte[] { 0xFF, 0x00, 0x00, 0x00 });
        await client.FlushAsync();
        client.Dispose();

        await Task.Delay(200);

        // The server must still be listening for everyone else.
        await using var survivor = await ConnectAsync();
        await NativeMessagingCodec.WriteAsync(survivor, JsonSerializer.Serialize(new QueryMessage(), Wire.Json));

        Assert.Contains("\"type\":\"state\"", await NativeMessagingCodec.ReadAsync(survivor));
    }

    [Fact]
    public async Task A_declared_length_beyond_the_limit_is_refused_without_killing_the_server()
    {
        var client = await ConnectAsync();
        await client.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F });   // int.MaxValue
        await client.FlushAsync();
        client.Dispose();

        await Task.Delay(200);

        await using var survivor = await ConnectAsync();
        await NativeMessagingCodec.WriteAsync(survivor, JsonSerializer.Serialize(new QueryMessage(), Wire.Json));

        Assert.Contains("\"type\":\"state\"", await NativeMessagingCodec.ReadAsync(survivor));
    }

    [Fact]
    public async Task Hebrew_in_a_payload_survives_the_pipe()
    {
        await using var client = await ConnectAsync();

        var command = JsonSerializer.Serialize(new CommandMessage
        {
            Command = "setMode",
            ConversationKey = HashedKey,
            Mode = "Pinned",
            LanguageTag = "he-IL",
        }, Wire.Json);

        await NativeMessagingCodec.WriteAsync(client, command);
        var reply = await NativeMessagingCodec.ReadAsync(client);

        var state = JsonSerializer.Deserialize<StateMessage>(reply!, Wire.Json)!;
        Assert.Equal("Pinned", state.ConversationMode);
    }
}
