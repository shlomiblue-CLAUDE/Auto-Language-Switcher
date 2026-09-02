using System.Diagnostics;
using System.Reflection;
using AutoLang.Core;

namespace AutoLang.Agent;

/// <summary>
/// The Agent's only visible surface.
///
/// It exists mostly so the product is not a hidden background process. A tool that silently
/// changes the user's keyboard and cannot be found, inspected or turned off is one users
/// uninstall - and rightly. The menu therefore always answers three questions: is it on, what is
/// it doing, and how do I stop it.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ConversationStore _store;
    private readonly IKeyboardLayoutService _layouts;
    private readonly Action _quit;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _enabledItem;

    public TrayIcon(ConversationStore store, IKeyboardLayoutService layouts, Action quit)
    {
        _store = store;
        _layouts = layouts;
        _quit = quit;

        _statusItem = new ToolStripMenuItem("…") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("Switch automatically", null, (_, _) => ToggleEnabled()) { CheckOnClick = true };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open data folder", null, (_, _) => OpenDataFolder()));
        menu.Items.Add(new ToolStripMenuItem("Clear stored preferences…", null, (_, _) => ClearData()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _quit()));

        // Refreshing on open, rather than on a timer, keeps an idle Agent genuinely idle.
        menu.Opening += (_, _) => Refresh();

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Auto Language Switcher",
            ContextMenuStrip = menu,
            Visible = true,
        };

        Refresh();
    }

    private static Icon LoadIcon()
    {
        // Embedded rather than loaded from disk, so a single-file publish stays a single file.
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("tray.ico", StringComparison.OrdinalIgnoreCase));

        if (name is not null)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is not null) return new Icon(stream);
        }

        // A missing icon must not stop the Agent from running; the product still works without it.
        return SystemIcons.Application;
    }

    private void Refresh()
    {
        var settings = _store.Settings;
        var layout = _layouts.CurrentLayout();
        var available = _layouts.AvailableLanguages();

        _enabledItem.Checked = settings.Enabled;

        string status =
            !settings.Enabled ? "Off"
            : available.Count < 2 ? $"Only {(available.Count == 0 ? "no layouts" : available[0].ToString())} installed"
            : layout == Language.Unknown ? "Auto — waiting"
            : $"Auto — {layout}";

        _statusItem.Text = status;

        // The tooltip is capped at 63 characters by Windows and silently truncates past that.
        _icon.Text = $"Auto Language Switcher — {status}";
    }

    private void ToggleEnabled()
    {
        _store.SaveSettings(_store.Settings with { Enabled = _enabledItem.Checked });
        Refresh();
    }

    private void OpenDataFolder()
    {
        Directory.CreateDirectory(_store.Root);
        Process.Start(new ProcessStartInfo { FileName = _store.Root, UseShellExecute = true });
    }

    private void ClearData()
    {
        var answer = MessageBox.Show(
            "Delete every stored conversation preference on this computer?\n\nThis cannot be undone.",
            "Auto Language Switcher",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.OK) return;

        _store.ClearAll();
        Refresh();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
