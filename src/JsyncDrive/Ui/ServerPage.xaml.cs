using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JsyncDrive.Engine;
using JsyncDrive.Ui;
using Microsoft.Win32;

namespace JsyncDrive;

public partial class ServerPage : UserControl, IPage
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private ServiceState _svc = ServiceState.Unknown;
    private bool _busy;
    private bool _loading;
    private bool _wired;

    private Window? Owner => Window.GetWindow(this);

    public ServerPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _poll.Tick += async (_, _) => await Refresh();
        Loaded += (_, _) => _poll.Start();
        Unloaded += (_, _) => _poll.Stop();
        FillForm();
    }

    public void OnShown()
    {
        if (!_wired)
        {
            _wired = true;
            _host.Hub.Changed += () => Dispatcher.BeginInvoke(async () => await Refresh());
        }
        _ = Refresh();
    }

    private void FillForm()
    {
        var h = _host.S.Hub;
        StorageBox.Text = h.StorageDir;
        LeafPortBox.Text = h.LeafPort.ToString();
        DrivePortBox.Text = h.DrivePort.ToString();
        AccountsBox.Text = h.AccountsUrl;
        WebBox.Text = h.DriveWebUrl;
        AdminsBox.Text = string.Join(", ", h.Admins);
        HostBox.Text = h.Host;
        ClientPortBox.Text = h.ClientPort.ToString();
        DriveHostBox.Text = h.DriveHost;
    }

    private async Task Refresh()
    {
        if (_busy) return;
        _svc = await HubService.QueryService();
        var h = _host.S.Hub;
        _loading = true;
        InAppSwitch.IsChecked = _host.Hub.InAppRunning;
        InAppSwitch.IsEnabled = _svc != ServiceState.Running && h.Configured;
        _loading = false;

        var inApp = _host.Hub.InAppRunning;
        var service = _svc == ServiceState.Running;
        string title, text, dot;
        if (!h.Configured) { title = Loc.T("Not configured yet"); text = Loc.T("Fill in the form below and press Save and apply."); dot = "MutedBrush"; }
        else if (service) { title = Loc.T("Running as a Windows service"); text = ""; dot = "SuccessBrush"; }
        else if (inApp) { title = Loc.T("Running"); text = Loc.T("Only while jsync Drive is open."); dot = "SuccessBrush"; }
        else if (_host.Hub.InAppState == HostState.Failed) { title = Loc.T("Stopped"); text = _host.Hub.InAppDetail ?? ""; dot = "DangerBrush"; }
        else { title = Loc.T("Stopped"); text = ""; dot = "MutedBrush"; }
        StateTitle.Text = title;
        StateText.Text = text;
        StateText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Dot.Fill = Theme.Brush(dot);

        ServiceStateText.Text = _svc switch
        {
            ServiceState.NotInstalled => Loc.T("Not installed"),
            ServiceState.Running => Loc.T("Running"),
            ServiceState.Stopped => Loc.T("Installed, stopped"),
            ServiceState.Starting => Loc.T("Starting…"),
            ServiceState.Stopping => Loc.T("Stopping…"),
            _ => Loc.T("Unknown"),
        };
        var installed = _svc != ServiceState.NotInstalled && _svc != ServiceState.Unknown;
        InstallBtn.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        UninstallBtn.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        StartBtn.Visibility = installed && _svc != ServiceState.Running ? Visibility.Visible : Visibility.Collapsed;
        StopBtn.Visibility = installed && _svc == ServiceState.Running ? Visibility.Visible : Visibility.Collapsed;
        InstallBtn.IsEnabled = h.Configured;

        var ips = HubService.LocalAddresses().ToList();
        HubAddrs.ItemsSource = ips.Select(ip => $"{ip}:{h.LeafPort}").Append($"127.0.0.1:{h.LeafPort}").ToList();
        DriveAddrs.ItemsSource = ips.Select(ip => $"http://{ip}:{h.DrivePort}").Append($"http://127.0.0.1:{h.DrivePort}").ToList();
        var log = string.Join(Environment.NewLine, _host.Hub.LogTail().TakeLast(80));
        if (LogBox.Text != log) { LogBox.Text = log; LogBox.ScrollToEnd(); }
        MachineText.Text = _machine.Length > 0 ? Loc.T("Hub id: {0}", _machine) : "";
    }

    private string _machine = "";

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is string s)
        {
            try { Clipboard.SetText(s); } catch { /* clipboard busy */ }
        }
    }

    private void OnChooseStorage(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Where the Drive files are kept") };
        if (dlg.ShowDialog(Owner) == true) StorageBox.Text = dlg.FolderName;
    }

    // --- configuration ---

    private bool ReadForm(out HubSettings h, out string error)
    {
        h = new HubSettings
        {
            Host = HostBox.Text.Trim(), DriveHost = DriveHostBox.Text.Trim(), StorageDir = StorageBox.Text.Trim(),
            AccountsUrl = AccountsBox.Text.Trim(), DriveWebUrl = WebBox.Text.Trim(), RunInApp = _host.S.Hub.RunInApp, Configured = true,
            Admins = AdminsBox.Text.Split(',', ';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
        };
        error = "";
        if (!int.TryParse(LeafPortBox.Text, out var leaf) || leaf is < 1 or > 65535) { error = Loc.T("The hub port must be a number from 1 to 65535."); return false; }
        if (!int.TryParse(DrivePortBox.Text, out var drive) || drive is < 1 or > 65535) { error = Loc.T("The Drive port must be a number from 1 to 65535."); return false; }
        if (!int.TryParse(ClientPortBox.Text, out var client) || client is < 1 or > 65535) { error = Loc.T("The client port must be a number from 1 to 65535."); return false; }
        h.LeafPort = leaf; h.DrivePort = drive; h.ClientPort = client;
        if (h.StorageDir.Length == 0) { error = Loc.T("Choose where the Drive files are kept."); return false; }
        if (h.Host.Length == 0) h.Host = "0.0.0.0";
        if (h.DriveHost.Length == 0) h.DriveHost = "0.0.0.0";
        return true;
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (!ReadForm(out var h, out var error)) { ApplyNote.Text = error; return; }
        _busy = true;
        ApplyNote.Text = Loc.T("Saving…");
        var wasInApp = _host.Hub.InAppRunning;
        try
        {
            await _host.Hub.StopInApp();
            var (ok, text, machine, _) = await _host.Hub.Configure(h, SecretBox.Password.Length > 0 ? SecretBox.Password : null);
            if (!ok) { ApplyNote.Text = text; return; }
            _machine = machine;
            _host.Settings.Update(s => s.Hub = h);
            SecretBox.Password = "";
            ApplyNote.Text = Loc.T("Saved.");
            if (_svc == ServiceState.Running)
            {
                // The service reads its configuration when it starts.
                var (r, t) = await _host.Hub.Control("restart");
                ApplyNote.Text = r ? Loc.T("Saved. The service was restarted.") : Loc.T("Saved, but the service could not be restarted: {0}", t);
            }
            else if (wasInApp || h.RunInApp && _svc != ServiceState.Running) _host.Hub.StartInApp(h);
        }
        finally
        {
            _busy = false;
            await Refresh();
        }
    }

    // --- running it ---

    private async void OnInApp(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = InAppSwitch.IsChecked == true;
        _host.Settings.Update(s => s.Hub.RunInApp = on);
        if (on) _host.Hub.StartInApp(_host.S.Hub);
        else await _host.Hub.StopInApp();
        await Refresh();
    }

    private async Task ServiceAction(Func<Task<(bool ok, string text)>> act)
    {
        _busy = true;
        try
        {
            var (ok, text) = await act();
            if (!ok && text != "declined") Dialogs.Info(Owner, Loc.T("Something went wrong"), text);
        }
        finally { _busy = false; await Refresh(); }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        await _host.Hub.StopInApp(); // the ports are the service's now
        _host.Settings.Update(s => s.Hub.RunInApp = false);
        await ServiceAction(() => _host.Hub.InstallService(_host.S.Hub));
    }

    private async void OnServiceStart(object sender, RoutedEventArgs e)
    {
        await _host.Hub.StopInApp();
        _host.Settings.Update(s => s.Hub.RunInApp = false);
        await ServiceAction(() => _host.Hub.Control("start"));
    }

    private async void OnServiceStop(object sender, RoutedEventArgs e) => await ServiceAction(() => _host.Hub.Control("stop"));

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm(Owner, Loc.T("Uninstall the service"), Loc.T("The hub stops running when nobody has jsync Drive open. Your files and settings stay."), Loc.T("Uninstall"), true)) return;
        await ServiceAction(() => _host.Hub.Control("uninstall"));
    }
}
