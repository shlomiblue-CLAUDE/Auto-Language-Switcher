using AutoLang.Core;

namespace AutoLang.Agent;

/// <summary>
/// The seam that lets the Agent's routing be tested without a real foreground window.
///
/// Worth stating plainly: a fake here proves the routing, never the switching. The only evidence
/// that switching works is the phase 0 spike and the manual acceptance runs, because the whole
/// difficulty of this product lives in Win32 behaviour that a test double cannot reproduce.
/// </summary>
/// <summary>
/// The window in front, and enough about it to decide whether a signal is about that window.
///
/// The title is here because a desktop signal needs it, and it never leaves this process
/// unhashed - see DesktopIdentity. Nothing stores it, nothing logs it.
/// </summary>
public readonly record struct ForegroundWindow(IntPtr Handle, string ProcessName, string Title)
{
    public static readonly ForegroundWindow None = new(IntPtr.Zero, "", "");
    public bool Exists => Handle != IntPtr.Zero;
}

public interface IKeyboardLayoutService
{
    IReadOnlyList<Language> AvailableLanguages();
    bool IsBrowserForeground();
    ForegroundWindow Foreground();
    Language CurrentLayout();

    /// <summary>
    /// Switches, but only if <paramref name="expectedWindow"/> is still the window in front.
    ///
    /// The window is passed in rather than read here because the caller decided against it: a
    /// decision is made about one window, and by the time it is applied the user may be in
    /// another. Checking "is a browser in front" was enough while the browser was the only source;
    /// it says nothing once a desktop application can ask for a switch too, and it would have let
    /// a decision about Slack land on Word.
    /// </summary>
    SwitchResult Switch(Language language, IntPtr expectedWindow);
}
