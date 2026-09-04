using System.Text;
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
    /// <summary>
    /// Primary language identifiers, which is the low ten bits of a LANGID.
    ///
    /// The full LANGID names a *sublanguage*: 0x0409 is US English and 0x0809 is UK English, and
    /// there are a dozen Arabic ones. Comparing full values was a latent defect that only Hebrew
    /// and US English were narrow enough to hide - a user with a British keyboard had their layout
    /// reported as Unknown, so the product could not recognise what they were already using.
    ///
    /// The sublanguage is a regional variant of the same alphabet, which is exactly what this
    /// product does not care about. It counts letters.
    /// </summary>
    private const ushort PrimaryHebrew = 0x0D;
    private const ushort PrimaryEnglish = 0x09;
    private const ushort PrimaryRussian = 0x19;
    private const ushort PrimaryArabic = 0x01;
    private const ushort PrimaryGreek = 0x08;

    private static ushort PrimaryOf(IntPtr hkl) => (ushort)((long)hkl & 0x3FF);

    private static readonly string[] BrowserProcessNames = ["chrome", "msedge"];

    /// <summary>
    /// Exposed because the desktop watcher must skip these.
    ///
    /// A browser reports itself through the extension, which knows which tab and which box the
    /// user is in. Watching it from the outside as well would put two sources on one keyboard,
    /// disagreeing about where the user is.
    /// </summary>
    public static bool IsBrowserProcess(string processName) =>
        BrowserProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase);

    private readonly TimeSpan _verifyTimeout = TimeSpan.FromMilliseconds(400);

    public static ushort LangIdFor(Language language) => language switch
    {
        Language.Hebrew => PrimaryHebrew,
        Language.English => PrimaryEnglish,
        Language.Russian => PrimaryRussian,
        Language.Arabic => PrimaryArabic,
        Language.Greek => PrimaryGreek,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Not a language this product switches to.")
    };

    public static Language LanguageOf(IntPtr hkl) => PrimaryOf(hkl) switch
    {
        PrimaryHebrew => Language.Hebrew,
        PrimaryEnglish => Language.English,
        PrimaryRussian => Language.Russian,
        PrimaryArabic => Language.Arabic,
        PrimaryGreek => Language.Greek,
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
        ushort primary = LangIdFor(language);
        return ListInstalled().FirstOrDefault(l => (l.LangId & 0x3FF) == primary);
    }

    /// <summary>Languages the user could actually be switched to, for the popup and for errors.</summary>
    public IReadOnlyList<Language> AvailableLanguages() =>
        ListInstalled().Select(l => LanguageOf(l.Hkl)).Where(l => l != Language.Unknown).Distinct().ToList();

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
    public ForegroundWindow Foreground()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return ForegroundWindow.None;

        return new ForegroundWindow(hwnd, ProcessNameOf(hwnd), WindowTitleOf(hwnd));
    }

    private static string WindowTitleOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        int length = Native.GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, Math.Min(length, buffer.Length)) : "";
    }

    public SwitchResult Switch(Language language, IntPtr expectedWindow)
    {
        var target = Resolve(language);
        if (target is null) return SwitchResult.Failed(ErrorCodes.LayoutNotInstalled);

        var hwnd = Native.GetForegroundWindow();

        // The second of two independent locks, and it now asks a sharper question than it used to.
        // "Is a browser in front" was true of any browser window; this is true only of the window
        // the decision was actually about, which also closes the gap where the user moves between
        // deciding and applying.
        if (hwnd == IntPtr.Zero || hwnd != expectedWindow)
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
