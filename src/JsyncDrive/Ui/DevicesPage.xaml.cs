using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JsyncDrive.Engine;
using JsyncDrive.Ui;
using Microsoft.Win32;

namespace JsyncDrive;

public sealed class DeviceVm
{
    public Device D { get; }
    public bool Trusted { get; }
    public DeviceVm(Device d, bool trusted) { D = d; Trusted = trusted; }
    public string Name => D.Name;
    public Geometry Icon => (Geometry)Application.Current.FindResource(LooksLikePhone(D.Name) ? "IconPhone" : "IconDesktop");
    public Brush DotBrush => Theme.Brush(D.Online ? "SuccessBrush" : "MutedBrush");
    public string StateText => D.Online ? Loc.T("Online") : Loc.T("Offline");
    public Brush StarBrush => Theme.Brush(Trusted ? "WarnBrush" : "BorderBrush");
    public string TrustTip => Trusted ? Loc.T("Always accepted. Click to ask first again.") : Loc.T("Always accept files from this device");

    private static bool LooksLikePhone(string n) =>
        new[] { "android", "phone", "pixel", "iphone", "ipad", "huawei", "samsung", "galaxy", "xiaomi", "redmi", "oneplus", "mobile", "tablet" }
            .Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase));
}

public sealed class LiveVm
{
    public Transfer T { get; }
    public LiveVm(Transfer t) { T = t; }
    public string Title => T.Name;
    public bool Incoming => !T.Sending && T.Status == TransferStatus.Incoming;
    public bool Cancellable => !Incoming && !T.Finished;
    public bool ShowProgress => T.Status is TransferStatus.Active or TransferStatus.Connecting or TransferStatus.Waiting;
    public double Percent => T.Status == TransferStatus.Active ? T.Fraction * 100 : 0;
    public Geometry Icon => (Geometry)Application.Current.FindResource(T.Sending ? "IconArrowUp" : "IconArrowDown");
    public Brush IconBrush => Theme.Brush("AccentBrush");

    public string Subtitle
    {
        get
        {
            var who = T.Sending ? Loc.T("To {0}", T.PeerName) : Loc.T("From {0}", T.PeerName);
            return T.Status switch
            {
                TransferStatus.Incoming => Loc.T("{0} wants to send you this · {1}", T.PeerName, Fmt.Size(T.Size)),
                TransferStatus.Active => $"{who} · {Fmt.Size(T.Bytes)} / {Fmt.Size(T.Size)}" + (T.Speed > 0 ? $" · {Fmt.Size((long)T.Speed)}/s" : "") + (string.IsNullOrEmpty(T.Route) ? "" : " · " + T.Route),
                TransferStatus.Connecting or TransferStatus.Waiting => $"{who} · " + (string.IsNullOrEmpty(T.Note) ? Loc.T("Connecting…") : T.Note),
                _ => who,
            };
        }
    }
}

public sealed class HistoryVm
{
    public HistoryItem Item { get; }
    public HistoryVm(HistoryItem i) { Item = i; }
    public bool CanOpen => Item.Exists;
    public Geometry Icon => (Geometry)Application.Current.FindResource(Item.Sent ? "IconArrowUp" : "IconArrowDown");
    public Brush BadgeBg => Theme.Brush(Item.Ok ? "AccentSoftBrush" : "SurfaceAltBrush");
    public Brush BadgeFg => Theme.Brush(Item.Ok ? "AccentBrush" : "MutedBrush");

    public string Detail
    {
        get
        {
            var who = Item.Sent ? Loc.T("To {0}", Item.Peer) : Loc.T("From {0}", Item.Peer);
            var s = $"{who} · {Fmt.Size(Item.Size)} · {Fmt.Ago(Item.When)}";
            if (!Item.Ok && !string.IsNullOrEmpty(Item.Note)) s += " · " + Item.Note;
            return s;
        }
    }

    public string Result => Item.Result switch
    {
        TransferStatus.Done => Item.Sent ? Loc.T("Sent") : Loc.T("Received"),
        TransferStatus.Declined => Loc.T("Declined"),
        TransferStatus.Cancelled => Loc.T("Cancelled"),
        _ => Loc.T("Failed"),
    };

    public Brush ResultBrush => Theme.Brush(Item.Ok ? "SuccessBrush" : Item.Result == TransferStatus.Error ? "DangerBrush" : "MutedBrush");
}

public partial class DevicesPage : UserControl, IPage
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(10) };
    private List<Device> _devices = new();
    private bool _wired;

    private Window? Owner => Window.GetWindow(this);

    public DevicesPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        SendAnyBtn.Content = Loc.T("Send a file…");
        _poll.Tick += (_, _) => _ = LoadDevices();
        Loaded += (_, _) => _poll.Start();
        Unloaded += (_, _) => _poll.Stop();
        ReceiveDirBox.Text = host.S.ReceiveDirectory;
        _setting = true;
        PopupSwitch.IsChecked = host.S.TransferPopups;
        AutoAllSwitch.IsChecked = host.S.AcceptFromAllMyDevices;
        _setting = false;
    }

    public void OnShown()
    {
        if (!_wired)
        {
            _wired = true;
            _host.Transfers.Updated += _ => Dispatcher.BeginInvoke(RefreshLive);
            _host.Transfers.Forgotten += _ => Dispatcher.BeginInvoke(RefreshLive);
            _host.History.Changed += () => Dispatcher.BeginInvoke(RefreshHistory);
            _host.Settings.Changed += () => Dispatcher.BeginInvoke(RefreshTrusted);
            _host.Node.StatusChanged += () => Dispatcher.BeginInvoke(() => _ = LoadDevices());
        }
        _ = LoadDevices();
        RefreshLive();
        RefreshHistory();
        RefreshTrusted();
        _poll.Start();
    }

    private async Task LoadDevices()
    {
        var api = _host.Node.Api;
        if (api == null || !_host.Node.Status.Online)
        {
            DeviceList.ItemsSource = null;
            DevicesEmpty.Text = _host.S.IsClient ? Loc.T("Connect this PC to your Drive to see your other devices.") : Loc.T("This PC is not set up as a client.");
            DevicesEmpty.Visibility = Visibility.Visible;
            SendAnyBtn.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            _devices = (await api.Devices()).OrderByDescending(d => d.Online).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (NodeException e)
        {
            DevicesEmpty.Text = e.Message;
            DevicesEmpty.Visibility = Visibility.Visible;
            return;
        }
        DeviceList.ItemsSource = _devices.Select(d => new DeviceVm(d, _host.Settings.IsTrusted(d.Id))).ToList();
        DevicesEmpty.Text = Loc.T("No other device has signed in to your account yet. Install jsync Drive on your phone or open the web app on another computer.");
        DevicesEmpty.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SendAnyBtn.Visibility = _devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshLive()
    {
        var live = _host.Transfers.Live.Where(t => !t.Finished).Select(t => new LiveVm(t)).ToList();
        LiveList.ItemsSource = live;
        LiveSection.Visibility = live.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshHistory()
    {
        var items = _host.History.Items.Select(i => new HistoryVm(i)).ToList();
        HistoryList.ItemsSource = items;
        HistoryCard.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        ClearBtn.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshTrusted()
    {
        var t = _host.S.Trusted.ToList();
        TrustedList.ItemsSource = t;
        TrustedEmpty.Visibility = t.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _setting = true;
        AutoAllSwitch.IsChecked = _host.S.AcceptFromAllMyDevices;
        _setting = false;
        if (_devices.Count > 0) _ = LoadDevices();
    }

    // --- sending ---

    private static Device DeviceOf(object sender) => ((DeviceVm)((FrameworkElement)sender).DataContext).D;

    private async Task Send(Device d, IEnumerable<string> files)
    {
        var skipped = 0;
        foreach (var f in files)
        {
            if (!File.Exists(f)) { skipped++; continue; }
            try { await _host.Transfers.SendFile(d, f); }
            catch (Exception e) { Dialogs.Info(Owner, Loc.T("Could not send"), e.Message); return; }
        }
        if (skipped > 0) Dialogs.Info(Owner, Loc.T("Folders are not sent"), Loc.T("Only files can be sent to another device. Put a folder in a zip first, or share it from your Drive."));
        RefreshLive();
    }

    private void OnSendTo(object sender, RoutedEventArgs e)
    {
        var d = DeviceOf(sender);
        var dlg = new OpenFileDialog { Multiselect = true, Title = Loc.T("Send to {0}", d.Name) };
        if (dlg.ShowDialog(Owner) == true) _ = Send(d, dlg.FileNames);
    }

    private void OnSendToAny(object sender, RoutedEventArgs e)
    {
        var d = Dialogs.PickDevice(Owner, _devices, Loc.T("Which device should get the file?"));
        if (d == null) return;
        var dlg = new OpenFileDialog { Multiselect = true, Title = Loc.T("Send to {0}", d.Name) };
        if (dlg.ShowDialog(Owner) == true) _ = Send(d, dlg.FileNames);
    }

    private void OnDeviceDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok && sender is Border b) b.BorderBrush = Theme.Brush("AccentBrush");
        e.Handled = true;
    }

    private void OnDeviceDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = Theme.Brush("BorderBrush");
    }

    private void OnDeviceDrop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = Theme.Brush("BorderBrush");
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = Send(DeviceOf(sender), files);
        e.Handled = true;
    }

    private void OnToggleTrust(object sender, RoutedEventArgs e)
    {
        var d = DeviceOf(sender);
        if (_host.Settings.IsTrusted(d.Id)) _host.Settings.Untrust(d.Id);
        else _host.Settings.Trust(d.Id, d.Name);
    }

    // --- live transfers ---

    private static Transfer TransferOf(object sender) => ((LiveVm)((FrameworkElement)sender).DataContext).T;

    private async void OnLiveAccept(object sender, RoutedEventArgs e) => await Guard(() => _host.Transfers.Accept(TransferOf(sender), false));
    private async void OnLiveAlways(object sender, RoutedEventArgs e) => await Guard(() => _host.Transfers.Accept(TransferOf(sender), true));
    private async void OnLiveDecline(object sender, RoutedEventArgs e) => await Guard(() => _host.Transfers.Decline(TransferOf(sender)));
    private async void OnLiveCancel(object sender, RoutedEventArgs e) => await Guard(() => _host.Transfers.Cancel(TransferOf(sender)));

    private async Task Guard(Func<Task> act)
    {
        try { await act(); } catch (NodeException ex) { Dialogs.Info(Owner, Loc.T("Something went wrong"), ex.Message); }
        RefreshLive();
    }

    // --- history ---

    private static HistoryItem ItemOf(object sender) => ((HistoryVm)((FrameworkElement)sender).DataContext).Item;

    private void OnHistoryOpen(object sender, RoutedEventArgs e)
    {
        var i = ItemOf(sender);
        try { if (i.Exists) Process.Start(new ProcessStartInfo(i.Path!) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Info(Owner, Loc.T("Could not open the file"), ex.Message); }
    }

    private void OnHistoryShow(object sender, RoutedEventArgs e)
    {
        var i = ItemOf(sender);
        if (i.Exists) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{i.Path}\"") { UseShellExecute = true });
    }

    private void OnHistoryRemove(object sender, RoutedEventArgs e) => _host.History.Remove(ItemOf(sender).Id);

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (Dialogs.Confirm(Owner, Loc.T("Clear history"), Loc.T("Forget the list of shared and received files? The files themselves stay where they are."), Loc.T("Clear"))) _host.History.Clear();
    }

    // --- receiving ---

    private void OnChangeReceiveDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Save received files in"), InitialDirectory = _host.S.ReceiveDirectory };
        if (dlg.ShowDialog(Owner) != true) return;
        _host.Settings.Update(s => s.ReceiveDirectory = dlg.FolderName);
        ReceiveDirBox.Text = dlg.FolderName;
    }

    private void OnOpenReceiveDir(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_host.S.ReceiveDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_host.S.ReceiveDirectory}\"") { UseShellExecute = true });
    }

    private bool _setting;
    private void OnPopupSwitch(object sender, RoutedEventArgs e)
    {
        if (!_setting) _host.Settings.Update(s => s.TransferPopups = PopupSwitch.IsChecked == true);
    }

    private void OnAutoAllSwitch(object sender, RoutedEventArgs e)
    {
        if (!_setting) _host.Settings.Update(s => s.AcceptFromAllMyDevices = AutoAllSwitch.IsChecked == true);
    }

    private void OnUntrust(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TrustedDevice t) _host.Settings.Untrust(t.Id);
    }
}
