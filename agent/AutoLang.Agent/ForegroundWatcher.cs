using System.Runtime.InteropServices;
using AutoLang.Core;

namespace AutoLang.Agent;

/// <summary>
/// Notices which window the user is in, so the product works outside the browser.
///
/// There is no text here, and there must not be. Reading what somebody has written in another
/// application means accessibility APIs and the contents of every window they have open; noticing
/// that they are typing means a global keyboard hook, which is a keylogger. This product's central
/// claim does not survive either, so the desktop source has no evidence at all.
///
/// It does not need any. Google Sheets proved that: its grid is a canvas, so nothing readable ever
/// reaches the DOM, and the product works there through two paths that need no text - a manual
/// layout change, which the engine detects and learns, and the memory that replays it next time.
/// A desktop application is that same case without even the misleading evidence.
///
/// Event-driven rather than polled. Two events are needed and the second is the one that is easy
/// to miss:
///
///   EVENT_SYSTEM_FOREGROUND   the user moved to another window
///   EVENT_OBJECT_NAMECHANGE   the window stayed and its title changed
///
/// Slack changes channel without changing the foreground window - only the title moves. Without
/// the second event every channel in Slack would share one memory, which is the thing per-window
/// identity exists to prevent.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectNameChange = 0x800C;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;

    /// <summary>Only the window's own name matters; controls inside it rename constantly.</summary>
    private const int ObjidWindow = 0;

    private readonly IKeyboardLayoutService _layouts;
    private readonly Func<string, bool> _isAllowed;
    private readonly Action<string, string> _onWindow;
    private readonly Action<string> _log;

    // Held so the delegate is not collected while Windows still holds a pointer to it. This is the
    // classic way to crash a hook, and it fails long after the fact.
    private readonly Native.WinEventProc _callback;

    private readonly List<IntPtr> _hooks = [];
    private System.Threading.Timer? _stayPoll;

    /// <summary>The last window reported, so an event that changes nothing sends nothing.</summary>
    private (string Process, string Title) _last = ("", "");

    public ForegroundWatcher(
        IKeyboardLayoutService layouts,
        Func<string, bool> isAllowed,
        Action<string, string> onWindow,
        Action<string>? log = null)
    {
        _layouts = layouts;
        _isAllowed = isAllowed;
        _onWindow = onWindow;
        _log = log ?? (_ => { });
        _callback = OnWinEvent;
    }

    /// <summary>
    /// Starts listening. Must be called on the thread that runs the message loop.
    ///
    /// SetWinEventHook delivers through the message queue of the thread that registered it, so the
    /// tray's loop is what makes this work at all - and why there is no second thread here.
    /// </summary>
    public void Start()
    {
        foreach (var eventId in new[] { EventSystemForeground, EventObjectNameChange })
        {
            var hook = Native.SetWinEventHook(
                eventId, eventId, IntPtr.Zero, _callback, 0, 0,
                WineventOutOfContext | WineventSkipOwnProcess);

            if (hook == IntPtr.Zero) _log($"could not hook window event {eventId:X}");
            else _hooks.Add(hook);
        }

        // The hooks report arrivals. Nothing reports a layout change made while the user stays put,
        // because Windows sends that to the window rather than to an observer, and the only
        // system-wide way to see it would be a keyboard hook - which is a keylogger and out of the
        // question.
        //
        // So the current window is re-observed on a slow timer. That is what turns "the layout is
        // different now" into evidence: two observations of the *same* window with a change
        // between them is a manual change, while arriving at a window with a different layout is
        // just where the user came from. Getting that backwards would have Slack learning English
        // every time somebody came to it from an English application.
        //
        // One GetForegroundWindow and one GetKeyboardLayout a second, and only while an allowed
        // application is in front. The measured cost of the whole agent is 0.006% of a core.
        _stayPoll = new System.Threading.Timer(_ => Reobserve(), null, StayPollMs, StayPollMs);
    }

    private const int StayPollMs = 1_000;

    /// <summary>
    /// Looks at the window the user is already in, so a layout change there can be noticed.
    ///
    /// Deliberately reports even when nothing about the window changed - the duplicate filter is
    /// on the window, not on the layout, and it is the layout this exists to catch.
    /// </summary>
    private void Reobserve()
    {
        try
        {
            var window = _layouts.Foreground();
            if (!window.Exists) return;
            if (KeyboardLayoutService.IsBrowserProcess(window.ProcessName)) return;
            if (!_isAllowed(window.ProcessName)) return;
            if (window.ProcessName != _last.Process || window.Title != _last.Title) return;

            _onWindow(window.ProcessName, window.Title);
        }
        catch (Exception ex)
        {
            _log($"window watcher poll: {ex.Message}");
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // A name change on a button or a list item is not a change of context. Without this filter
        // a busy application would report several times a second.
        if (eventId == EventObjectNameChange && idObject != ObjidWindow) return;

        try
        {
            var window = _layouts.Foreground();
            if (!window.Exists) return;

            // The event may be about a window that is not in front - a background application
            // renaming itself. Only what the user is looking at can be what they are typing into.
            if (eventId == EventObjectNameChange && hwnd != window.Handle) return;

            // The browser reports itself through the extension, which knows which tab and which
            // box the user is in. Watching it here as well would put two sources on one keyboard,
            // arguing over the same layout with different ideas about where the user is.
            if (KeyboardLayoutService.IsBrowserProcess(window.ProcessName)) return;

            if (!_isAllowed(window.ProcessName)) return;

            if (window.ProcessName == _last.Process && window.Title == _last.Title) return;
            _last = (window.ProcessName, window.Title);

            _onWindow(window.ProcessName, window.Title);
        }
        catch (Exception ex)
        {
            // A hook callback that throws takes the message loop with it, and the message loop is
            // the tray icon and the pipe server's host. Nothing here is worth that.
            _log($"window watcher: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _stayPoll?.Dispose();
        foreach (var hook in _hooks) Native.UnhookWinEvent(hook);
        _hooks.Clear();
    }
}
