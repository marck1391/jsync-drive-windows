using System.Diagnostics;
using System.Windows;
using JsyncDrive.Engine;
using JsyncDrive.Ui;
using WinForms = System.Windows.Forms;

namespace JsyncDrive;

/// <summary>The notification-area icon: status at a glance, and the few things worth one click.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppHost _host;
    private readonly WinForms.NotifyIcon _icon;
    private WinForms.ToolStripMenuItem? _status;
    private System.Drawing.Icon? _drawn;

    public TrayIcon(AppHost host)
    {
        _host = host;
        _icon = new WinForms.NotifyIcon { Visible = true, Text = "jsync Drive" };
        using var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        _drawn = new System.Drawing.Icon(s);
        _icon.Icon = _drawn;
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) _host.ShowMain(); };
        _icon.BalloonTipClicked += (_, _) => _host.ShowMain(_lastBalloonPage);
        Rebuild();
    }

    private string? _lastBalloonPage;

    public void Rebuild()
    {
        var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false, Font = new System.Drawing.Font("Segoe UI", 9.5f) };
        _status = new WinForms.ToolStripMenuItem(_host.StatusLine()) { Enabled = false };
        menu.Items.Add(_status);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Item(Loc.T("Open jsync Drive"), () => _host.ShowMain()));
        if (_host.S.IsClient)
        {
            menu.Items.Add(Item(Loc.T("Open my Drive folder"), OpenFolder));
            menu.Items.Add(Item(Loc.T("Send a file to another device…"), () => _host.ShowMain("devices")));
        }
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Item(Loc.T("Settings"), () => _host.ShowMain("settings")));
        menu.Items.Add(Item(Loc.T("Quit jsync Drive"), () => _host.Quit()));
        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    public void Refresh()
    {
        var line = _host.StatusLine();
        if (_status != null) _status.Text = line;
        // NotifyIcon.Text is limited to 63 characters
        _icon.Text = ("jsync Drive — " + line) is { Length: > 63 } t ? t[..62] + "…" : "jsync Drive — " + line;
    }

    private void OpenFolder()
    {
        var dir = _host.S.Mount.Directory;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { /* explorer missing: nothing to do */ }
    }

    public void Balloon(string title, string text, string? page = null)
    {
        _lastBalloonPage = page;
        _icon.ShowBalloonTip(5000, title, text, WinForms.ToolTipIcon.Info);
    }

    private static WinForms.ToolStripMenuItem Item(string text, Action click)
    {
        var i = new WinForms.ToolStripMenuItem(text);
        i.Click += (_, _) => click();
        return i;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _drawn?.Dispose();
    }
}
