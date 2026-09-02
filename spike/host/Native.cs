using System.Runtime.InteropServices;
using System.Text;

namespace AutoLang.Spike;

/// <summary>Raw Win32 surface. Nothing here decides anything; it only reports what Windows says.</summary>
internal static class Native
{
    public const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

    // wParam flags. Note: FORWARD/BACKWARD mean "cycle to next/previous" and make Windows
    // IGNORE the explicit HKL in lParam. For a targeted switch wParam must be 0.
    public const nuint INPUTLANGCHANGE_SYSCHARSET = 0x0001;

    public const uint KLF_ACTIVATE = 0x00000001;
    public const uint KLF_SETFORPROCESS = 0x00000100;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    public static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    public static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int count);

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public int left, top, right, bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    public static string WindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        return GetWindowTextW(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public static string WindowClass(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        return GetClassNameW(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }
}
