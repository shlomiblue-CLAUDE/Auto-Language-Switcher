using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using AutoLang.Core;

namespace AutoLang.Bridge;

/// <summary>
/// The native messaging host Chrome actually launches.
///
/// It does almost nothing on purpose. Chrome spawns a fresh host process per connection and kills
/// it when the port closes, so a host cannot hold state, cannot outlive the browser, and cannot
/// serve a second signal source. This process therefore translates Chrome's stdio framing onto a
/// named pipe and lets the resident Agent be the one thing that decides.
///
/// Two hard rules of this environment:
///   - stdout carries framed messages and nothing else. One stray Console.WriteLine and Chrome
///     kills the host with an error that names nothing useful.
///   - Chrome passes the calling extension's origin as argv[1]. It is checked here as well as in
///     the host manifest, so a manifest edited to widen access still meets a second gate.
/// </summary>
internal static class Program
{
    private const int PipeConnectTimeoutMs = 3000;
    private const int AgentStartTimeoutMs = 10_000;

    private static readonly string[] AllowedExtensionIds =
    [
        "iblcjhakhfggopgijnankilmifbjbdbp",
    ];

    private static void Log(string message) =>
        Console.Error.WriteLine($"[bridge {DateTime.Now:HH:mm:ss.fff}] {message}");

    private static async Task<int> Main(string[] args)
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        if (!IsCallerAllowed(args, out string caller))
        {
            Log($"refusing caller '{caller}'");
            await Fail(stdout, ErrorCodes.BadMessage, "This extension is not permitted to use the Auto Language Switcher host.");
            return 1;
        }

        using var cts = new CancellationTokenSource();

        NamedPipeClientStream pipe;
        try
        {
            pipe = await ConnectAsync(cts.Token);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            Log($"agent unreachable: {ex.Message}");
            // A clear code lets the extension say "the Agent is not installed" with a link, rather
            // than failing silently and looking like the product is simply broken.
            await Fail(stdout, "AGENT_UNAVAILABLE", "The Auto Language Switcher agent is not running and could not be started.");
            return 2;
        }

        await using (pipe)
        {
            // Chrome closes stdin when the port closes, which ends the read loop and the process.
            var browserToAgent = PumpAsync(stdin, pipe, "browser->agent", cts.Token);
            var agentToBrowser = PumpAsync(pipe, stdout, "agent->browser", cts.Token);

            await Task.WhenAny(browserToAgent, agentToBrowser);
            cts.Cancel();

            try { await Task.WhenAll(browserToAgent, agentToBrowser); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"pump ended: {ex.Message}"); }
        }

        return 0;
    }

    /// <summary>
    /// Chrome passes the origin as chrome-extension://ID/. Edge passes the same shape. When run by
    /// hand for diagnostics there is no origin argument, which is allowed - a human at a console
    /// already has every permission this process could grant.
    /// </summary>
    private static bool IsCallerAllowed(string[] args, out string caller)
    {
        caller = args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) ?? "";

        if (caller.Length == 0)
        {
            caller = "(no origin argument)";
            return args.Length == 0 || args.All(a => a.StartsWith("--", StringComparison.Ordinal));
        }

        var id = caller["chrome-extension://".Length..].TrimEnd('/');
        return AllowedExtensionIds.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(PipeConnectTimeoutMs, ct);
            return pipe;
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync();
        }

        // No Agent yet. Start it and wait, so a user who has only just installed does not have to
        // reboot or launch anything by hand before the product works.
        Log("no agent listening; starting one");
        if (!TryStartAgent(out string reason))
            throw new IOException($"Could not start the agent: {reason}");

        var retry = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await retry.ConnectAsync(AgentStartTimeoutMs, ct);
        return retry;
    }

    private static bool TryStartAgent(out string reason)
    {
        // The Agent ships beside the Bridge, so its location never has to be configured or guessed.
        var directory = AppContext.BaseDirectory;
        var path = Path.Combine(directory, "AutoLangAgent.exe");

        if (!File.Exists(path))
        {
            reason = $"AutoLangAgent.exe not found in {directory}";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            });
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static async Task PumpAsync(Stream from, Stream to, string label, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await NativeMessagingCodec.ReadAsync(from, ct: ct);
                if (message is null) break;
                await NativeMessagingCodec.WriteAsync(to, message, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            Log($"{label} closed: {ex.Message}");
        }
    }

    private static async Task Fail(Stream stdout, string code, string message)
    {
        var error = new ErrorMessage { Code = code, Message = message };
        try { await NativeMessagingCodec.WriteAsync(stdout, JsonSerializer.Serialize(error, Wire.Json)); }
        catch (IOException) { /* the browser may already be gone */ }
    }

    /// <summary>Kept in sync with PipeServer.PipeName by the round-trip test.</summary>
    private const string PipeName = "AutoLang.Agent";
}
