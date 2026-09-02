using System.Runtime.InteropServices;
using AutoLang.Core;

namespace AutoLang.Agent;

/// <summary>
/// The Agent's only visible surface: a tray icon and its menu.
///
/// Written against Win32 rather than WinForms, and the reason is size. WinForms forces a
/// self-contained build to carry the whole Microsoft.WindowsDesktop pack - including all of WPF,
/// which this product never touches - and the .NET SDK refuses to trim it at all (NETSDK1175).
/// That put the download at 155MB for a tool that switches a keyboard. Roughly 250 lines of
/// P/Invoke removes the dependency, and with it about 130MB.
///
/// It exists at all so the product is not a hidden background process. Something that silently
/// changes your keyboard and cannot be found, inspected or switched off is something people
/// uninstall, and rightly. The menu always answers three questions: is it on, what is it doing,
/// and how do I stop it.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_DESTROY = 0x0002;
    private const int WM_COMMAND = 0x0111;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_NULL = 0x0000;
    private const int WM_TRAYICON = 0x0400 + 1; // WM_APP + 1

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_GRAYED = 0x00000001;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint MB_OKCANCEL = 0x00000001;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_DEFBUTTON2 = 0x00000100;
    private const int IDOK = 1;

    private enum Command
    {
        None = 0,
        ToggleEnabled = 1,
        OpenDataFolder = 2,
        ClearData = 3,
        Exit = 4,
    }

    private readonly ConversationStore _store;
    private readonly IKeyboardLayoutService _layouts;
    private readonly Action _quit;

    // Held as a field: the delegate is passed to unmanaged code, and letting it be collected
    // means the window procedure is called into freed memory the next time a message arrives.
    private readonly WndProc _wndProc;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _disposed;

    public TrayIcon(ConversationStore store, IKeyboardLayoutService layouts, Action quit)
    {
        _store = store;
        _layouts = layouts;
        _quit = quit;
        _wndProc = WindowProcedure;

        _hwnd = CreateMessageWindow();
        _icon = LoadOwnIcon();

        var data = NewIconData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _icon;
        data.szTip = Tooltip();

        if (!Shell_NotifyIcon(NIM_ADD, ref data))
            throw new InvalidOperationException("Could not add the tray icon.");
    }

    /// <summary>Pumps messages until the menu's Exit item posts a quit.</summary>
    public void RunMessageLoop()
    {
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private IntPtr CreateMessageWindow()
    {
        var className = $"AutoLangTray_{Environment.ProcessId}";

        var wndClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };

        if (RegisterClassEx(ref wndClass) == 0)
            throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()}).");

        // HWND_MESSAGE: a window that exists only to receive messages. It is never shown, never
        // appears in the taskbar, and costs nothing.
        var hwnd = CreateWindowEx(0, className, "AutoLang", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");

        return hwnd;
    }

    /// <summary>
    /// Takes the icon from our own executable, where the build already embedded it as the
    /// application icon. No temp file, no second copy, nothing to lose in a single-file publish.
    /// </summary>
    private static IntPtr LoadOwnIcon()
    {
        var path = Environment.ProcessPath;
        if (path is not null)
        {
            var icon = ExtractIcon(GetModuleHandle(null), path, 0);
            // ExtractIcon returns 1, not 0, when the file holds no icons at all.
            if (icon != IntPtr.Zero && icon != new IntPtr(1)) return icon;
        }

        // A missing icon must never stop the Agent from running.
        return LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    private NOTIFYICONDATA NewIconData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
    };

    private string Tooltip()
    {
        // Windows caps the tooltip at 63 characters and silently truncates past that.
        var text = $"Auto Language Switcher — {Status()}";
        return text.Length <= 63 ? text : text[..63];
    }

    private string Status()
    {
        var settings = _store.Settings;
        if (!settings.Enabled) return "Off";

        var available = _layouts.AvailableLanguages();
        if (available.Count < 2)
            return available.Count == 0 ? "No layouts installed" : $"Only {available[0]} installed";

        var layout = _layouts.CurrentLayout();
        return layout == Language.Unknown ? "Auto — waiting" : $"Auto — {layout}";
    }

    private void RefreshTooltip()
    {
        var data = NewIconData();
        data.uFlags = NIF_TIP;
        data.szTip = Tooltip();
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_TRAYICON:
                if ((int)lParam is WM_RBUTTONUP or WM_LBUTTONUP) ShowMenu();
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            AppendMenu(menu, MF_STRING | MF_GRAYED, (UIntPtr)Command.None, Status());
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);

            var enabled = _store.Settings.Enabled ? MF_CHECKED : 0;
            AppendMenu(menu, MF_STRING | enabled, (UIntPtr)Command.ToggleEnabled, "Switch automatically");

            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)Command.OpenDataFolder, "Open data folder");
            AppendMenu(menu, MF_STRING, (UIntPtr)Command.ClearData, "Clear stored preferences…");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)Command.Exit, "Exit");

            GetCursorPos(out POINT cursor);

            // Both of these are required and neither is obvious. Without SetForegroundWindow the
            // menu ignores the first click elsewhere and refuses to close; without the WM_NULL
            // afterwards it can stay on screen. Documented Win32 behaviour since Windows 95.
            SetForegroundWindow(_hwnd);

            var chosen = (Command)TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, cursor.X, cursor.Y, 0, _hwnd, IntPtr.Zero);

            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            Handle(chosen);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void Handle(Command command)
    {
        switch (command)
        {
            case Command.ToggleEnabled:
                _store.SaveSettings(_store.Settings with { Enabled = !_store.Settings.Enabled });
                RefreshTooltip();
                break;

            case Command.OpenDataFolder:
                Directory.CreateDirectory(_store.Root);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _store.Root,
                    UseShellExecute = true,
                });
                break;

            case Command.ClearData:
            {
                int answer = MessageBox(
                    _hwnd,
                    "Delete every stored conversation preference on this computer?\n\nThis cannot be undone.",
                    "Auto Language Switcher",
                    MB_OKCANCEL | MB_ICONWARNING | MB_DEFBUTTON2);

                if (answer == IDOK) _store.ClearAll();
                break;
            }

            case Command.Exit:
                _quit();
                PostQuitMessage(0);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var data = NewIconData();
        Shell_NotifyIcon(NIM_DELETE, ref data);

        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }

    // --- Interop ------------------------------------------------------------------------------

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string file, int iconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? item);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
