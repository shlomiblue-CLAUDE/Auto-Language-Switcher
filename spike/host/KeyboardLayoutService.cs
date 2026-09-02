using System.Diagnostics;
using Microsoft.Win32;

namespace AutoLang.Spike;

public sealed record LayoutInfo(IntPtr Hkl, ushort LangId, string Klid, string Name)
{
    public string HklHex => $"0x{(long)Hkl:X8}";
    public override string ToString() => $"{HklHex}  langid=0x{LangId:X4}  klid={Klid}  {Name}";
}

public sealed record SwitchResult(bool Success, string Strategy, IntPtr Before, IntPtr After, long ElapsedMs, string? Error = null);

/// <summary>
/// Resolves and switches the Windows keyboard layout of the foreground window.
///
/// Design rule learned the hard way: NEVER hardcode a KLID. This machine has Hebrew installed as
/// 0002040D (Hebrew Standard) with a registry Substitute mapping 0000040d -> 0002040d, so the
/// 0000040D value in the PDR would not have matched anything. We match on the LANGID (low word of
/// the HKL) against the layouts Windows actually reports as installed.
/// </summary>
public sealed class KeyboardLayoutService
{
    public const ushort LangIdHebrew = 0x040D;
    public const ushort LangIdEnglishUs = 0x0409;

    public static ushort LangIdFor(string language) => language.ToLowerInvariant() switch
    {
        "he" or "he-il" or "hebrew" => LangIdHebrew,
        "en" or "en-us" or "english" => LangIdEnglishUs,
        _ => throw new ArgumentException($"Unsupported language '{language}'. v1 supports he and en.")
    };

    public IReadOnlyList<LayoutInfo> ListInstalled()
    {
        int count = Native.GetKeyboardLayoutList(0, null);
        if (count <= 0) return Array.Empty<LayoutInfo>();

        var buf = new IntPtr[count];
        Native.GetKeyboardLayoutList(count, buf);

        return buf.Select(h =>
        {
            var langId = (ushort)((long)h & 0xFFFF);
            var klid = KlidFromHkl(h);
            return new LayoutInfo(h, langId, klid, LayoutName(klid));
        }).ToList();
    }

    /// <summary>Finds the installed HKL for a language, or null if that layout is not installed.</summary>
    public LayoutInfo? Resolve(ushort langId) =>
        ListInstalled().FirstOrDefault(l => l.LangId == langId);

    /// <summary>
    /// HKL -> KLID. The high word is a device handle, not a layout id, so the mapping is not a cast:
    /// when the high word has the 0xF000 marker its low 12 bits are a "Layout Id" that only the
    /// registry can resolve back to a KLID.
    /// </summary>
    public static string KlidFromHkl(IntPtr hkl)
    {
        long v = (long)hkl;
        ushort low = (ushort)(v & 0xFFFF);
        ushort high = (ushort)((v >> 16) & 0xFFFF);

        if (high == 0 || high == low) return low.ToString("x8");

        if ((high & 0xF000) == 0xF000)
        {
            int layoutId = high & 0x0FFF;
            var match = FindKlidByLayoutId(layoutId);
            if (match is not null) return match;
        }

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

    public static IntPtr ForegroundHwnd() => Native.GetForegroundWindow();

    public static uint ThreadOf(IntPtr hwnd) => Native.GetWindowThreadProcessId(hwnd, out _);

    public static string ProcessNameOf(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch { return "(unavailable)"; }
    }

    /// <summary>The window that actually has the caret, which is not always the top-level window.</summary>
    public static IntPtr FocusHwndOf(uint threadId)
    {
        var gti = new Native.GUITHREADINFO();
        gti.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.GUITHREADINFO>();
        return Native.GetGUIThreadInfo(threadId, ref gti) ? gti.hwndFocus : IntPtr.Zero;
    }

    public static IntPtr CurrentLayoutOf(uint threadId) => Native.GetKeyboardLayout(threadId);

    /// <summary>
    /// Attempts a switch and then VERIFIES it by re-reading the thread's layout. A Win32 call
    /// returning success is not proof the layout changed - that is the trap the PDR warns about.
    /// </summary>
    public SwitchResult TrySwitch(IntPtr targetHkl, string strategy, IntPtr hwnd, int verifyTimeoutMs = 400)
    {
        uint threadId = ThreadOf(hwnd);
        IntPtr before = CurrentLayoutOf(threadId);
        var sw = Stopwatch.StartNew();

        try
        {
            switch (strategy)
            {
                case "post-toplevel":
                    Native.PostMessage(hwnd, Native.WM_INPUTLANGCHANGEREQUEST, 0, targetHkl);
                    break;

                case "post-focus":
                {
                    var focus = FocusHwndOf(threadId);
                    if (focus == IntPtr.Zero)
                        return new SwitchResult(false, strategy, before, before, sw.ElapsedMilliseconds, "no focus hwnd");
                    Native.PostMessage(focus, Native.WM_INPUTLANGCHANGEREQUEST, 0, targetHkl);
                    break;
                }

                case "post-syscharset":
                    Native.PostMessage(hwnd, Native.WM_INPUTLANGCHANGEREQUEST, Native.INPUTLANGCHANGE_SYSCHARSET, targetHkl);
                    break;

                case "attach-activate":
                {
                    uint self = Native.GetCurrentThreadId();
                    if (!Native.AttachThreadInput(self, threadId, true))
                        return new SwitchResult(false, strategy, before, before, sw.ElapsedMilliseconds, "AttachThreadInput failed");
                    try { Native.ActivateKeyboardLayout(targetHkl, Native.KLF_SETFORPROCESS); }
                    finally { Native.AttachThreadInput(self, threadId, false); }
                    break;
                }

                default:
                    return new SwitchResult(false, strategy, before, before, sw.ElapsedMilliseconds, "unknown strategy");
            }

            // Poll rather than sleep a flat interval, so we measure real latency.
            IntPtr after = before;
            while (sw.ElapsedMilliseconds < verifyTimeoutMs)
            {
                after = CurrentLayoutOf(threadId);
                if (after == targetHkl) break;
                Thread.Sleep(15);
            }

            sw.Stop();
            return new SwitchResult(after == targetHkl, strategy, before, after, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SwitchResult(false, strategy, before, before, sw.ElapsedMilliseconds, ex.Message);
        }
    }
}
