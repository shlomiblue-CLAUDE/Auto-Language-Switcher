using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using AutoLang.Core;

namespace AutoLang.Agent;

/// <summary>
/// Native messaging host mode: the role Chrome launches.
///
/// This used to be a separate executable. It is the same one now, because two self-contained
/// builds meant two copies of the .NET runtime - 211MB for a tool that switches a keyboard - and
/// because they had to sit in the same folder for the Bridge to find the Agent, a requirement that
/// already caused one silent failure when only one of the two files was copied.
///
/// The role is chosen by argv, not by a flag, because Chrome's host manifest has no place to put
/// arguments: its "path" is an executable and nothing else. Chrome and Edge both pass the calling
/// extension's origin as an argument, so its presence is the signal - and its value is the
/// allowlist check that has to happen anyway.
///
/// It still does almost nothing. Chrome spawns a fresh host process per connection and kills it
/// when the port closes, so this process cannot hold state or outlive the browser. It translates
/// Chrome's stdio framing onto the named pipe and lets the resident Agent decide.
/// </summary>
internal static class BridgeMode
{
    private const int PipeConnectTimeoutMs = 3000;
    private const int AgentStartTimeoutMs = 10_000;

    private static readonly string[] AllowedExtensionIds =
    [
        "iblcjhakhfggopgijnankilmifbjbdbp",
    ];

    /// <summary>True when Chrome or Edge launched us as a native messaging host.</summary>
    public static bool IsRequested(string[] args) =>
        args.Any(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
        || args.Contains("--bridge");

    private static void Log(string message)
    {
        // stderr only. stdout carries framed messages, and one stray write corrupts the stream -
        // Chrome then kills the host with an error that names nothing useful.
        Console.Error.WriteLine($"[bridge {DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    public static async Task<int> RunAsync(string[] args)
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
            // A named code lets the extension say "the agent is not running" with a link, instead
            // of failing silently and looking like the product is simply broken.
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
    /// Checked here as well as in the host manifest, so a manifest edited to widen access still
    /// meets a second gate. Running by hand with no origin is allowed: a human at a console
    /// already has every permission this process could grant.
    /// </summary>
    private static bool IsCallerAllowed(string[] args, out string caller)
    {
        caller = args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) ?? "";

        if (caller.Length == 0)
        {
            caller = "(no origin argument)";
            return true;
        }

        var id = caller["chrome-extension://".Length..].TrimEnd('/');
        return AllowedExtensionIds.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", PipeServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(PipeConnectTimeoutMs, ct);
            return pipe;
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync();
        }

        // No Agent yet. Start one, so a user who has only just installed does not have to reboot
        // or launch anything by hand before the product works.
        Log("no agent listening; starting one");
        if (!TryStartAgent(out string reason))
            throw new IOException($"Could not start the agent: {reason}");

        var retry = new NamedPipeClientStream(".", PipeServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await retry.ConnectAsync(AgentStartTimeoutMs, ct);
        return retry;
    }

    private static bool TryStartAgent(out string reason)
    {
        // Our own path. One executable means there is nothing to locate and nothing to get wrong.
        var path = Environment.ProcessPath;

        if (path is null || !File.Exists(path))
        {
            reason = "could not determine this executable's path";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                UseShellExecute = false,
                CreateNoWindow = true,
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
}
