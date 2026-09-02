using System.Diagnostics;
using AutoLang.Core;
using Microsoft.Win32;

namespace AutoLang.Agent;

public sealed record LayoutInfo(IntPtr Hkl, ushort LangId, string Klid, string Name)
{
    public override string ToString() => $"0x{(uint)(long)Hkl:X8} {Klid} {Name}";
}

public sealed record SwitchResult(bool Success, string? ErrorCode, long ElapsedMs, Language Applied)
{
    public static SwitchResult Failed(string code) => new(false, code, 0, Language.Unknown);
}

/// <summary>
/// Changes the keyboard layout of the foreground window.
///
/// Everything here is a direct consequence of the phase 0 spike, which is worth restating because
/// each rule looks arbitrary until it costs you a day:
///
/// - Layouts are resolved by LANGID against what Windows reports as installed, never by a
///   hardcoded KLID. This machine has Hebrew as 0002040D with a registry Substitute from
///   0000040D, so the value named in the PDR would have matched nothing.
/// - wParam is 0, not INPUTLANGCHANGE_FORWARD. FORWARD means "cycle to the next layout" and makes
///   Windows ignore the explicit HKL - the blind cycling the PDR forbids.
/// - Every switch is verified by re-reading GetKeyboardLayout. The spike found strategies that
///   returned success and changed nothing.
/// - The foreground check is enforced here, in software. The spike switched a background Edge
///   window without complaint, so Windows provides no protection to rely on.
/// </summary>
public sealed class KeyboardLayoutService : IKeyboardLayoutService
{
    private const ushort LangIdHebrew = 0x040D;
    private const ushort LangIdEnglishUs = 0x0409;

    private static readonly string[] BrowserProcessNames = ["chrome", "msedge"];

    private readonly TimeSpan _verifyTimeout = TimeSpan.FromMilliseconds(400);

    public static ushort LangIdFor(Language language) => language switch
    {
        Language.Hebrew => LangIdHebrew,
        Language.English => LangIdEnglishUs,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "v1 supports Hebrew and English.")
    };

    public static Language LanguageOf(IntPtr hkl) => ((ushort)((long)hkl & 0xFFFF)) switch
    {
        LangIdHebrew => Language.Hebrew,
        LangIdEnglishUs => Language.English,
        _ => Language.Unknown
    };

    public IReadOnlyList<LayoutInfo> ListInstalled()
    {
        int count = Native.GetKeyboardLayoutList(0, null);
        if (count <= 0) return [];

        var buffer = new IntPtr[count];
        Native.GetKeyboardLayoutList(count, buffer);

        return buffer.Select(h =>
        {
            var langId = (ushort)((long)h & 0xFFFF);
            var klid = KlidFromHkl(h);
            return new LayoutInfo(h, langId, klid, LayoutName(klid));
        }).ToList();
    }

    public LayoutInfo? Resolve(Language language)
    {
        if (language == Language.Unknown) return null;
        ushort langId = LangIdFor(language);
        return ListInstalled().FirstOrDefault(l => l.LangId == langId);
    }

    /// <summary>Languages the user could actually be switched to, for the popup and for errors.</summary>
    public IReadOnlyList<Language> AvailableLanguages() =>
        ListInstalled().Select(l => LanguageOf(l.Hkl)).Where(l => l != Language.Unknown).Distinct().ToList();

    public IntPtr ForegroundWindow() => Native.GetForegroundWindow();

    public bool IsBrowserForeground()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        return BrowserProcessNames.Contains(ProcessNameOf(hwnd), StringComparer.OrdinalIgnoreCase);
    }

    public Language CurrentLayout()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return Language.Unknown;

        uint threadId = Native.GetWindowThreadProcessId(hwnd, out _);
        return LanguageOf(Native.GetKeyboardLayout(threadId));
    }

    /// <summary>
    /// Switches the foreground window to <paramref name="language"/>, verifying the result.
    /// Refuses when the foreground window is not a supported browser.
    /// </summary>
    public SwitchResult Switch(Language language)
    {
        var target = Resolve(language);
        if (target is null) return SwitchResult.Failed(ErrorCodes.LayoutNotInstalled);

        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !IsBrowserForeground())
            return SwitchResult.Failed(ErrorCodes.NotForeground);

        uint threadId = Native.GetWindowThreadProcessId(hwnd, out _);
        var stopwatch = Stopwatch.StartNew();

        // wParam 0 with an explicit HKL in lParam. See the class comment.
        Native.PostMessage(hwnd, Native.WM_INPUTLANGCHANGEREQUEST, 0, target.Hkl);

        while (stopwatch.Elapsed < _verifyTimeout)
        {
            if (Native.GetKeyboardLayout(threadId) == target.Hkl)
            {
                stopwatch.Stop();
                return new SwitchResult(true, null, stopwatch.ElapsedMilliseconds, language);
            }
            Thread.Sleep(10);
        }

        stopwatch.Stop();
        return new SwitchResult(false, ErrorCodes.SwitchFailed, stopwatch.ElapsedMilliseconds, Language.Unknown);
    }

    private static string ProcessNameOf(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch (ArgumentException) { return ""; }
        catch (InvalidOperationException) { return ""; }
    }

    /// <summary>
    /// HKL to KLID. Not a cast: when the high word carries the 0xF000 marker, its low 12 bits are
    /// a "Layout Id" that only the registry can map back to a KLID. Diagnostics only - resolution
    /// never depends on this.
    /// </summary>
    public static string KlidFromHkl(IntPtr hkl)
    {
        long value = (long)hkl;
        ushort low = (ushort)(value & 0xFFFF);
        ushort high = (ushort)((value >> 16) & 0xFFFF);

        if (high == 0 || high == low) return low.ToString("x8");

        if ((high & 0xF000) == 0xF000 && FindKlidByLayoutId(high & 0x0FFF) is { } match)
            return match;

        return high.ToString("x8");
    }

    private static string? FindKlidByLayoutId(int layoutId)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts");
        if (root is null) return null;

        foreach (var klid in root.GetSubKeyNames())
        {
            using var sub = root.OpenSubKey(klid);
            if (sub?.GetValue("Layout Id") is not string raw) continue;
            if (int.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out int id) && id == layoutId)
                return klid;
        }
        return null;
    }

    public static string LayoutName(string klid)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{klid}");
        return key?.GetValue("Layout Text") as string ?? "(unknown)";
    }
}
