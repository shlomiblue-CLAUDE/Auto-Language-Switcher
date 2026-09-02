using System.Diagnostics;
using AutoLang.Agent;
using AutoLang.Core;

// The resident Agent. Phase 5 adds the tray icon; today it runs as a console host so the
// transport can be exercised end to end.
//
//   AutoLangAgent            run the pipe server
//   AutoLangAgent --verbose  ... and narrate decisions
//   AutoLangAgent --layouts  print installed layouts and exit
//   AutoLangAgent --status   report whether an Agent is already running

var verbose = args.Contains("--verbose") || args.Contains("-v");
var layouts = new KeyboardLayoutService();

void Log(string message)
{
    // stderr, always. If this process is ever hosted behind a stdio transport, a stray write to
    // stdout corrupts the framing and the browser kills the host with no useful error.
    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
}

if (args.Contains("--layouts"))
{
    foreach (var layout in layouts.ListInstalled()) Console.WriteLine(layout);
    Console.WriteLine();
    foreach (var language in new[] { Language.Hebrew, Language.English })
    {
        var resolved = layouts.Resolve(language);
        Console.WriteLine(resolved is null
            ? $"{language,-8} NOT INSTALLED"
            : $"{language,-8} {resolved}");
    }
    return 0;
}

// One Agent per user session. A second instance would fight the first over the same pipe and,
// worse, keep its own copy of the hysteresis state - two brains taking turns on one keyboard.
using var singleInstance = new Mutex(initiallyOwned: true, @"Local\AutoLang.Agent.SingleInstance", out bool isFirst);

if (args.Contains("--status"))
{
    Console.WriteLine(isFirst ? "not running" : "running");
    return isFirst ? 1 : 0;
}

if (!isFirst)
{
    Log("another Agent is already running; exiting.");
    return 0;
}

var store = new ConversationStore();
store.Load();

var core = new AgentCore(store, layouts, log: verbose ? Log : null);
var server = new PipeServer(core, Log);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

Log($"AutoLang Agent {AgentCore.AgentVersion} (pid {Environment.ProcessId})");
Log($"store: {store.Root}");
Log($"pipe:  \\\\.\\pipe\\{PipeServer.PipeName}");

var available = layouts.AvailableLanguages();
Log($"layouts available: {(available.Count == 0 ? "NONE" : string.Join(", ", available))}");

foreach (var language in new[] { Language.Hebrew, Language.English })
{
    if (layouts.Resolve(language) is null)
        Log($"WARNING: no {language} layout is installed. Switching to it will report {ErrorCodes.LayoutNotInstalled}.");
}

try
{
    await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // Normal shutdown.
}

Log("stopped.");
return 0;
