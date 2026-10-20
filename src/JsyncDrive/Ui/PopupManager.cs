using System.Windows;
using JsyncDrive.Engine;
using JsyncDrive.Ui;

namespace JsyncDrive;

/// <summary>Shows a window for each transfer that comes to this PC, stacked in the corner of the screen.</summary>
public sealed class PopupManager
{
    private readonly AppHost _host;
    private readonly Dictionary<string, TransferPopup> _open = new();

    public PopupManager(AppHost host)
    {
        _host = host;
        host.Transfers.NeedsAnswer += t => host.UI.BeginInvoke(() => OnRequest(t));
        host.Transfers.Updated += t => host.UI.BeginInvoke(() => OnUpdate(t));
        host.Transfers.Received += (t, path) => host.UI.BeginInvoke(() => OnReceived(t, path));
        host.Transfers.Forgotten += id => host.UI.BeginInvoke(() => Close(id));
    }

    private void OnRequest(Transfer t)
    {
        if (!_host.S.TransferPopups)
        {
            // Without a window, a notification at least says a file is waiting.
            _host.Tray?.Balloon(Loc.T("{0} wants to send you a file", string.IsNullOrEmpty(t.PeerName) ? Loc.T("Another device") : t.PeerName), $"{t.Name} · {Fmt.Size(t.Size)}", "devices");
            return;
        }
        Open(t);
    }

    private void OnUpdate(Transfer t)
    {
        if (t.Sending) return; // what this PC sends is followed on the Devices page
        if (_open.TryGetValue(t.Id, out var p)) p.Update(t);
    }

    private void OnReceived(Transfer t, string path)
    {
        if (!_host.S.TransferPopups)
        {
            _host.Tray?.Balloon(Loc.T("Received “{0}”", Path.GetFileName(path)), Loc.T("Saved in {0}", Path.GetDirectoryName(path)!), "devices");
            return;
        }
        if (!_open.TryGetValue(t.Id, out var p)) p = Open(t);
        p.ShowReceived(t, path);
    }

    private TransferPopup Open(Transfer t)
    {
        if (_open.TryGetValue(t.Id, out var existing)) { existing.Update(t); return existing; }
        var p = new TransferPopup(_host, t);
        _open[t.Id] = p;
        p.Closed += (_, _) => { _open.Remove(t.Id); Layout(); };
        p.Show();
        Layout();
        return p;
    }

    private void Close(string id)
    {
        // A file that arrived stays on screen until the person is done with it, even though the node let go of it.
        if (_open.TryGetValue(id, out var p) && !p.Final) p.Close();
    }

    private void Layout()
    {
        var area = SystemParameters.WorkArea;
        var y = area.Bottom;
        foreach (var p in _open.Values.OrderBy(p => p.Tag is DateTime d ? d : DateTime.MinValue))
        {
            p.UpdateLayout();
            y -= p.ActualHeight > 0 ? p.ActualHeight : 320;
            p.Left = area.Right - p.Width;
            p.Top = y;
        }
    }
}
