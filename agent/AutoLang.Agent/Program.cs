using System.Runtime.InteropServices;
using AutoLang.Agent;
using AutoLang.Core;

// The resident Agent: a tray icon and a named pipe server.
//
//   AutoLangAgent            run
//   AutoLangAgent --verbose  ... and log every decision
//   AutoLangAgent --layouts  print installed layouts and exit
//   AutoLangAgent --status   report whether an Agent is already running
//
// Built as WinExe so starting it does not flash a console window at the user. The diagnostic
// modes below reattach to the parent console so they still work from a terminal.

[DllImport("kernel32.dll")]
static extern bool AttachConsole(int processId);

const int AttachParentProcess = -1;

// One executable, two roles. Chrome's native messaging manifest has nowhere to put arguments -
// its "path" is an executable and nothing else - so the role cannot be selected by a flag we
// choose. Chrome and Edge both pass the calling extension's origin, so its presence is the signal.
if (BridgeMode.IsRequested(args))
{
    return await BridgeMode.RunAsync(args);
}

var verbose = args.Contains("--verbose") || args.Contains("-v");
var layouts = new KeyboardLayoutService();

// A tray app has no console to write to, so diagnostics go to a file the user can find through
// the tray menu. Kept small and text-free by the same rule as everywhere else.
var logPath = Path.Combine(ConversationStore.DefaultRoot(), "agent.log");

void Log(string message)
{
    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
    Console.Error.WriteLine(line);

    if (!verbose) return;
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.AppendAllText(logPath, line + Environment.NewLine);
    }
    catch (IOException)
    {
        // Losing a log line is never worth failing a switch over.
    }
}

if (args.Contains("--layouts") || args.Contains("--status"))
{
    AttachConsole(AttachParentProcess);

    if (args.Contains("--layouts"))
    {
        foreach (var layout in layouts.ListInstalled()) Console.WriteLine(layout);
        Console.WriteLine();
        foreach (var language in new[] { Language.Hebrew, Language.English })
        {
            var resolved = layouts.Resolve(language);
            Console.WriteLine(resolved is null ? $"{language,-8} NOT INSTALLED" : $"{language,-8} {resolved}");
        }
        return 0;
    }

    using var probe = new Mutex(initiallyOwned: true, @"Local\AutoLang.Agent.SingleInstance", out bool free);
    Console.WriteLine(free ? "not running" : "running");
    return free ? 1 : 0;
}

// One Agent per user session. A second would fight the first over the pipe and, worse, keep its
// own hysteresis state - two brains taking turns on one keyboard.
using var singleInstance = new Mutex(initiallyOwned: true, @"Local\AutoLang.Agent.SingleInstance", out bool isFirst);

if (!isFirst)
{
    Log("another Agent is already running; exiting.");
    return 0;
}

var store = new ConversationStore();
store.Load();

var core = new AgentCore(store, layouts, log: Log);

using var cts = new CancellationTokenSource();
var server = new PipeServer(core, Log);
var serverTask = server.RunAsync(cts.Token);

Log($"AutoLang Agent {AgentCore.AgentVersion} (pid {Environment.ProcessId})");
Log($"store: {store.Root}");
Log($"pipe:  \\\\.\\pipe\\{PipeServer.PipeName}");

var available = layouts.AvailableLanguages();
Log($"layouts available: {(available.Count == 0 ? "NONE" : string.Join(", ", available))}");

// Only languages the user actually asked for. Warning about every language the product knows -
// which is what a fresh install would do - puts three warnings in front of somebody who writes in
// two, and a warning nobody can act on is how people learn to skip them.
foreach (var language in store.Settings.EnabledLanguages)
{
    if (layouts.Resolve(language) is null)
        Log($"WARNING: no {language} layout is installed. Switching to it will report {ErrorCodes.LayoutNotInstalled}.");
}

// Started on this thread on purpose: SetWinEventHook delivers through the message queue of the
// thread that registers it, and the tray's loop below is the only one this process runs.
using var watcher = new ForegroundWatcher(
    layouts,
    isAllowed: name => store.GetApp(name) is not null,
    onWindow: core.ObserveWindow,
    log: Log);

watcher.Start();

var allowed = store.AllowedApps();
Log(allowed.Count == 0
    ? "no applications allowed; the desktop watcher will report nothing"
    : $"watching {allowed.Count} allowed application(s)");

using var tray = new TrayIcon(store, layouts, () => cts.Cancel());
tray.RunMessageLoop();

try { await serverTask; }
catch (OperationCanceledException) { }

Log("stopped.");
return 0;
