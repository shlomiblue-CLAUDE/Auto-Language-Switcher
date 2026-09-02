using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using AutoLang.Core;

namespace AutoLang.Agent;

/// <summary>
/// Accepts connections from Bridge processes over a named pipe.
///
/// Chrome starts a fresh native messaging host process for every connection, which is why the
/// Bridge cannot be the Agent: a per-connection child has no lasting state and no way to serve a
/// second signal source. The Bridge is a thin translator; this is where the process lives.
///
/// The ACL is the security boundary. A named pipe with default permissions on Windows is reachable
/// by other accounts on the machine, and this one accepts commands that change the user's keyboard.
/// Access is therefore granted to the creating user alone - not Users, not Authenticated Users.
/// </summary>
public sealed class PipeServer
{
    public const string PipeName = "AutoLang.Agent";

    private const int MaxConcurrentClients = 8;

    private readonly AgentCore _core;
    private readonly Action<string> _log;
    private readonly string _pipeName;

    /// <param name="pipeName">
    /// Overridable so tests do not collide with a real Agent. A hardcoded name meant a test client
    /// silently connected to whichever Agent happened to be running on the developer's machine and
    /// asserted against its answers - a failure that appears only when the product is installed,
    /// which is precisely when a green suite matters most.
    /// </param>
    public PipeServer(AgentCore core, Action<string>? log = null, string? pipeName = null)
    {
        _core = core;
        _log = log ?? (_ => { });
        _pipeName = pipeName ?? PipeName;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var clients = new List<Task>();

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServer(_pipeName);
            }
            catch (IOException ex)
            {
                // All instances busy. Back off rather than spinning.
                _log($"pipe unavailable: {ex.Message}");
                await Task.Delay(250, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                break;
            }

            clients.Add(ServeAsync(server, ct));
            clients.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(clients).ConfigureAwait(false);
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        var security = new PipeSecurity();
        var self = WindowsIdentity.GetCurrent().User
                   ?? throw new InvalidOperationException("Could not determine the current user SID.");

        security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            MaxConcurrentClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            while (server.IsConnected && !ct.IsCancellationRequested)
            {
                var request = await NativeMessagingCodec.ReadAsync(server, ct: ct).ConfigureAwait(false);
                if (request is null) break;

                var response = _core.Handle(request);
                if (response is not null)
                    await NativeMessagingCodec.WriteAsync(server, response, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            // A browser closing mid-message is ordinary. One client's bad framing must never take
            // the Agent down for every other conversation.
            _log($"client dropped: {ex.Message}");
        }
        finally
        {
            try { if (server.IsConnected) server.Disconnect(); } catch (IOException) { }
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
