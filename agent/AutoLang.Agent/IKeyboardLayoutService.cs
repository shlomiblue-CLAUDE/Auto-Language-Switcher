using AutoLang.Core;

namespace AutoLang.Agent;

/// <summary>
/// The seam that lets the Agent's routing be tested without a real foreground window.
///
/// Worth stating plainly: a fake here proves the routing, never the switching. The only evidence
/// that switching works is the phase 0 spike and the manual acceptance runs, because the whole
/// difficulty of this product lives in Win32 behaviour that a test double cannot reproduce.
/// </summary>
public interface IKeyboardLayoutService
{
    IReadOnlyList<Language> AvailableLanguages();
    bool IsBrowserForeground();
    Language CurrentLayout();
    SwitchResult Switch(Language language);
}
