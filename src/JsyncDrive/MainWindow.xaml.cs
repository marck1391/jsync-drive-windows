using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JsyncDrive.Engine;
using JsyncDrive.Ui;

namespace JsyncDrive;

/// <summary>Implemented by the pages: told when they come into view.</summary>
public interface IPage
{
    void OnShown();
}

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly Dictionary<string, UserControl> _pages = new();
    private static bool _toldAboutTray;

    public string CurrentPage { get; private set; } = "files";

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        WinUtil.Chrome(this);
        var s = host.S;
        NavFiles.Visibility = NavDevices.Visibility = NavFolder.Visibility = s.IsClient ? Visibility.Visible : Visibility.Collapsed;
        NavServer.Visibility = s.IsHub ? Visibility.Visible : Visibility.Collapsed;

        host.Node.StatusChanged += OnStatus;
        host.Mount.Changed += OnStatus;
        host.Hub.Changed += OnStatus;
        Closed += (_, _) =>
        {
            host.Node.StatusChanged -= OnStatus;
            host.Mount.Changed -= OnStatus;
            host.Hub.Changed -= OnStatus;
        };
        Navigate(s.IsClient ? "files" : s.IsHub ? "server" : "settings");
        UpdateStatus();
    }

    private void OnStatus() => Dispatcher.BeginInvoke(UpdateStatus);

    private void UpdateStatus()
    {
        var s = _host.S;
        Brush dot = Theme.Brush("MutedBrush");
        string title = Loc.T("Offline");
        var text = "";
        if (s.IsClient)
        {
            var st = _host.Node.Status;
            switch (st.State)
            {
                case NodeStates.Online: dot = Theme.Brush("SuccessBrush"); title = Loc.T("Connected"); text = st.Email ?? ""; break;
                case NodeStates.Connecting: dot = Theme.Brush("WarnBrush"); title = Loc.T("Connecting…"); break;
                case NodeStates.Pending: dot = Theme.Brush("WarnBrush"); title = Loc.T("Waiting for approval"); break;
                case NodeStates.LoggedOut: title = Loc.T("Signed out"); text = Loc.T("Open Settings to sign in."); break;
                case NodeStates.NeedsSetup: dot = Theme.Brush("WarnBrush"); title = Loc.T("Needs setup"); text = Loc.T("Add the hub and Drive address in Settings."); break;
                default: dot = Theme.Brush("DangerBrush"); title = Loc.T("Offline"); text = st.Detail ?? ""; break;
            }
        }
        else if (s.IsHub)
        {
            var up = _host.Hub.InAppRunning;
            dot = Theme.Brush(up ? "SuccessBrush" : "MutedBrush");
            title = up ? Loc.T("Hub running") : Loc.T("Hub stopped");
        }
        StatusDot.Fill = dot;
        StatusTitle.Text = title;
        StatusText.Text = text;
        StatusText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnStatusClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Navigate("settings");

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string page } && page != CurrentPage) Navigate(page);
    }

    public void Navigate(string page)
    {
        if (!_pages.TryGetValue(page, out var view))
        {
            view = page switch
            {
                "files" => new FilesPage(_host),
                "devices" => new DevicesPage(_host),
                "folder" => new FolderPage(_host),
                "server" => new ServerPage(_host),
                _ => new SettingsPage(_host),
            };
            _pages[page] = view;
        }
        CurrentPage = page;
        PageHost.Content = view;
        foreach (var (rb, tag) in new[] { (NavFiles, "files"), (NavDevices, "devices"), (NavFolder, "folder"), (NavServer, "server"), (NavSettings, "settings") })
            if (rb.IsChecked != (tag == page)) rb.IsChecked = tag == page;
        (view as IPage)?.OnShown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The app lives in the tray: closing the window only hides it.
        if (Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown && !_closeForReal)
        {
            e.Cancel = true;
            Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                _host.Tray?.Balloon("jsync Drive", Loc.T("jsync Drive keeps running here, in the notification area."));
            }
        }
        base.OnClosing(e);
    }

    private bool _closeForReal;

    /// <summary>Closes the window for good (the language or theme changed and it is built again).</summary>
    public void CloseForReal()
    {
        _closeForReal = true;
        Close();
    }
}
