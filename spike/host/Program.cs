using System.Diagnostics;
using AutoLang.Spike;

// Phase 0 spike CLI. Purpose: prove that a HE/EN layout switch actually lands on a
// Chrome/Edge window, before any product code is written.
//
//   AutoLangSpike list
//   AutoLangSpike watch [intervalMs]
//   AutoLangSpike set <he|en> [--delay N] [--strategy S] [--process NAME]
//   AutoLangSpike matrix [--delay N] [--process NAME]
//
// --process targets that process's main window instead of the foreground window. That is not how
// the product will behave, but it lets us test Edge while the user is working in Chrome - and it
// answers a real acceptance question: can a background window's layout be changed at all?

var svc = new KeyboardLayoutService();
var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

string[] allStrategies = ["post-toplevel", "post-focus", "post-syscharset", "attach-activate"];

static string Hx(IntPtr h) => $"0x{(uint)(long)h:X8}";

string? ArgVal(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

int DelayArg(int fallback) => int.TryParse(ArgVal("--delay"), out int n) ? n : fallback;

void Countdown(int seconds)
{
    if (seconds <= 0) return;
    Console.WriteLine($"\nFocus the target window now. Starting in {seconds}s...");
    for (int i = seconds; i > 0; i--) { Console.Write($"  {i}... "); Thread.Sleep(1000); }
    Console.WriteLine("\n");
}

/// <summary>
/// Forces a window to the foreground, which Windows normally refuses.
///
/// SetForegroundWindow alone fails whenever the caller does not already own the foreground - the
/// lock exists so background programs cannot steal focus mid-keystroke. Attaching our input queue
/// to the current foreground thread makes Windows treat the call as coming from the app that
/// already has focus, which is the documented way to raise a window on purpose.
///
/// This is diagnostic code and stays in the spike. The product must never do this: a keyboard
/// switcher that grabs focus would be worse than the problem it solves.
/// </summary>
bool Activate(IntPtr hwnd)
{
    var foreground = Native.GetForegroundWindow();
    if (foreground == hwnd) return true;

    uint foregroundThread = Native.GetWindowThreadProcessId(foreground, out _);
    uint targetThread = Native.GetWindowThreadProcessId(hwnd, out _);

    // A lone ALT press and release. Windows grants foreground rights to a process it believes the
    // user just interacted with, and this is the documented way to claim that - the same trick
    // installers and updaters use. Harmless on its own: pressing ALT and letting go focuses the
    // menu bar and immediately releases it.
    Native.keybd_event(Native.VK_MENU, 0, 0, UIntPtr.Zero);
    Native.keybd_event(Native.VK_MENU, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);

    uint self = Native.GetCurrentThreadId();
    bool attachedToForeground = foregroundThread != self && Native.AttachThreadInput(self, foregroundThread, true);
    bool attachedToTarget = targetThread != self && Native.AttachThreadInput(self, targetThread, true);

    Console.WriteLine($"activate   attach: foreground={attachedToForeground} target={attachedToTarget}");

    try
    {
        Native.ShowWindow(hwnd, Native.SW_RESTORE);
        Native.BringWindowToTop(hwnd);
        Native.SetForegroundWindow(hwnd);
    }
    finally
    {
        if (attachedToTarget) Native.AttachThreadInput(self, targetThread, false);
        if (attachedToForeground) Native.AttachThreadInput(self, foregroundThread, false);
    }

    // Windows can report success and do nothing, so the only answer that counts is what it says
    // afterwards.
    for (int i = 0; i < 20; i++)
    {
        if (Native.GetForegroundWindow() == hwnd) return true;
        Thread.Sleep(50);
    }
    return false;
}

// Returns the window to act on, plus whether it is genuinely the foreground window.
(IntPtr Hwnd, bool IsForeground, string? Error) ResolveTarget()
{
    var wanted = ArgVal("--process") ?? ArgVal("--activate");

    if (wanted is null) return (KeyboardLayoutService.ForegroundHwnd(), true, null);

    var proc = Process.GetProcessesByName(wanted).FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
    if (proc is null) return (IntPtr.Zero, false, $"no '{wanted}' process with a main window");

    var hwnd = proc.MainWindowHandle;

    if (ArgVal("--activate") is not null)
    {
        bool raised = Activate(hwnd);
        Console.WriteLine(raised
            ? $"activated  {wanted} is now the foreground window"
            : $"activated  FAILED to raise {wanted}; Windows refused");
    }

    return (hwnd, hwnd == KeyboardLayoutService.ForegroundHwnd(), null);
}

void PrintTarget(IntPtr hwnd, bool isForeground)
{
    uint tid = KeyboardLayoutService.ThreadOf(hwnd);
    Console.WriteLine($"target     : hwnd=0x{(long)hwnd:X} thread={tid} process={KeyboardLayoutService.ProcessNameOf(hwnd)}");
    Console.WriteLine($"class      : {Native.WindowClass(hwnd)}");
    Console.WriteLine($"title      : {Native.WindowTitle(hwnd)}");
    Console.WriteLine($"foreground : {(isForeground ? "YES" : "NO  <-- background window; product would refuse this")}");
    Console.WriteLine($"focus hwnd : 0x{(long)KeyboardLayoutService.FocusHwndOf(tid):X}");
    Console.WriteLine($"layout now : {Hx(KeyboardLayoutService.CurrentLayoutOf(tid))}");
}

switch (cmd)
{
    case "list":
    {
        Console.WriteLine("Installed keyboard layouts (as Windows reports them):\n");
        foreach (var l in svc.ListInstalled())
            Console.WriteLine($"  {Hx(l.Hkl)}  langid=0x{l.LangId:X4}  klid={l.Klid}  {l.Name}");

        Console.WriteLine("\nResolution used by the product:");
        foreach (var (name, id) in new[] { ("he-IL", KeyboardLayoutService.LangIdHebrew), ("en-US", KeyboardLayoutService.LangIdEnglishUs) })
        {
            var hit = svc.Resolve(id);
            Console.WriteLine(hit is null
                ? $"  {name} -> NOT INSTALLED (product must surface a clear error, never guess)"
                : $"  {name} -> {Hx(hit.Hkl)}  klid={hit.Klid}  {hit.Name}");
        }
        break;
    }

    case "watch":
    {
        int interval = int.TryParse(args.ElementAtOrDefault(1), out int ms) ? ms : 500;
        Console.WriteLine($"Polling every {interval}ms. Ctrl+C to stop.\n");
        IntPtr lastHwnd = IntPtr.Zero, lastLayout = IntPtr.Zero;
        while (true)
        {
            var hwnd = KeyboardLayoutService.ForegroundHwnd();
            uint tid = KeyboardLayoutService.ThreadOf(hwnd);
            var layout = KeyboardLayoutService.CurrentLayoutOf(tid);
            if (hwnd != lastHwnd || layout != lastLayout)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {KeyboardLayoutService.ProcessNameOf(hwnd),-12} tid={tid,-6} layout={Hx(layout)}  {Native.WindowTitle(hwnd)}");
                lastHwnd = hwnd; lastLayout = layout;
            }
            Thread.Sleep(interval);
        }
    }

    case "set":
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: set <he|en> [--delay N] [--strategy S] [--process NAME]"); return 2; }
        var target = svc.Resolve(KeyboardLayoutService.LangIdFor(args[1]));
        if (target is null) { Console.Error.WriteLine($"Layout for '{args[1]}' is not installed."); return 3; }

        Countdown(DelayArg(5));
        var (hwnd, isFg, err) = ResolveTarget();
        if (err is not null) { Console.Error.WriteLine(err); return 4; }
        PrintTarget(hwnd, isFg);

        var r = svc.TrySwitch(target.Hkl, ArgVal("--strategy") ?? "post-toplevel", hwnd);
        Console.WriteLine($"\n{(r.Success ? "PASS" : "FAIL")}  strategy={r.Strategy}  {Hx(r.Before)} -> {Hx(r.After)}  ({r.ElapsedMs}ms) {r.Error}");
        return r.Success ? 0 : 1;
    }

    case "matrix":
    {
        Countdown(DelayArg(6));
        var (hwnd, isFg, err) = ResolveTarget();
        if (err is not null) { Console.Error.WriteLine(err); return 4; }
        PrintTarget(hwnd, isFg);

        Console.WriteLine($"\n{"strategy",-18} {"lang",-5} {"result",-6} {"before",-12} {"after",-12} ms");
        Console.WriteLine(new string('-', 68));

        int pass = 0, meaningful = 0;
        foreach (var strategy in allStrategies)
            foreach (var lang in new[] { "en", "he", "en" })   // en->he->en catches "only switches one way"
            {
                var t = svc.Resolve(KeyboardLayoutService.LangIdFor(lang))!;
                var r = svc.TrySwitch(t.Hkl, strategy, hwnd);
                bool noop = r.Before == t.Hkl;
                if (!noop) { meaningful++; if (r.Success) pass++; }
                string verdict = noop ? "-noop-" : r.Success ? "PASS" : "FAIL";
                Console.WriteLine($"{strategy,-18} {lang,-5} {verdict,-6} {Hx(r.Before),-12} {Hx(r.After),-12} {r.ElapsedMs} {r.Error}");
                Thread.Sleep(250);
            }

        Console.WriteLine($"\n{pass}/{meaningful} real transitions passed against {KeyboardLayoutService.ProcessNameOf(hwnd)} (foreground={isFg})");
        return pass > 0 ? 0 : 1;
    }

    default:
        Console.Error.WriteLine("commands: list | watch [ms] | set <he|en> | matrix");
        return 2;
}

return 0;
