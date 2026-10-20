using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using JsyncDrive.Engine;
using JsyncDrive.Ui;

namespace JsyncDrive;

public partial class SettingsPage : UserControl, IPage
{
    private readonly AppHost _host;
    private bool _loading;
    private bool _wired;

    private Window? Owner => Window.GetWindow(this);

    public SettingsPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        foreach (var (v, l) in new[] { ("auto", "Same as Windows"), ("en", "English"), ("es", "Español") })
            LangBox.Items.Add(new ComboBoxItem { Content = v == "auto" ? Loc.T(l) : l, Tag = v });
        foreach (var (v, l) in new[] { ("system", "Same as Windows"), ("light", "Light"), ("dark", "Dark") })
            ThemeBox.Items.Add(new ComboBoxItem { Content = Loc.T(l), Tag = v });
        SignIn.SignedIn += () => { SignIn.Visibility = Visibility.Collapsed; Refresh(); };
    }

    public void OnShown()
    {
        if (!_wired)
        {
            _wired = true;
            _host.Node.StatusChanged += () => Dispatcher.BeginInvoke(Refresh);
        }
        Load();
        Refresh();
    }

    private void Load()
    {
        _loading = true;
        var s = _host.S;
        AccountCard.Visibility = s.IsClient ? Visibility.Visible : Visibility.Collapsed;
        HubBox.Text = s.HubAddress;
        ServerBox.Text = s.ServerUrl;
        DeviceBox.Text = s.DeviceName;
        ReadOnlySwitch.IsChecked = s.ReadOnly;
        LangBox.SelectedIndex = Math.Max(0, new[] { "auto", "en", "es" }.ToList().IndexOf(s.Language));
        ThemeBox.SelectedIndex = Math.Max(0, new[] { "system", "light", "dark" }.ToList().IndexOf(s.Theme));
        StartupSwitch.IsChecked = s.StartWithWindows;
        AlsoClientSwitch.IsChecked = s.AlsoClient;
        AlsoClientSwitch.Visibility = s.IsHub ? Visibility.Visible : Visibility.Collapsed;
        RoleText.Text = s.IsHub
            ? (s.AlsoClient ? Loc.T("This PC is a hub, and also a client of it.") : Loc.T("This PC is a hub. It serves your Drive to your other devices."))
            : Loc.T("This PC is a client. It connects to a hub you already have.");
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = Loc.T("jsync Drive for Windows {0}", v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "");
        _loading = false;
    }

    private void Refresh()
    {
        var st = _host.Node.Status;
        string text, dot;
        switch (st.State)
        {
            case NodeStates.Online: text = Loc.T("Signed in as {0}", st.Email ?? ""); dot = "SuccessBrush"; break;
            case NodeStates.Connecting: text = Loc.T("Connecting…"); dot = "WarnBrush"; break;
            case NodeStates.Pending: text = Loc.T("Waiting for you to approve this PC"); dot = "WarnBrush"; break;
            case NodeStates.LoggedOut: text = Loc.T("Not signed in"); dot = "MutedBrush"; break;
            case NodeStates.NeedsSetup: text = Loc.T("Signed in as {0}, but the hub or Drive address is missing", st.Email ?? ""); dot = "WarnBrush"; break;
            default: text = Loc.T("Offline") + (string.IsNullOrEmpty(st.Detail) ? "" : ": " + st.Detail); dot = "DangerBrush"; break;
        }
        AccText.Text = text;
        AccDot.Fill = Theme.Brush(dot);
        var signedIn = !string.IsNullOrEmpty(st.Email) && st.State != NodeStates.LoggedOut;
        SignInBtn.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignOutBtn.Visibility = signedIn || st.State == NodeStates.Pending ? Visibility.Visible : Visibility.Collapsed;
        EngineText.Text = string.IsNullOrEmpty(st.Version) ? "" : st.Version;
    }

    // --- connection ---

    private bool Valid(out string error)
    {
        error = "";
        if (HubBox.Text.Trim().Length == 0) { error = Loc.T("Enter the hub address."); return false; }
        if (ServerBox.Text.Trim().Length == 0) { error = Loc.T("Enter the Drive address."); return false; }
        if (DeviceBox.Text.Trim().Length == 0) { error = Loc.T("Give this PC a name."); return false; }
        return true;
    }

    private void Save()
    {
        _host.Settings.Update(s =>
        {
            s.HubAddress = HubBox.Text.Trim();
            s.ServerUrl = ServerBox.Text.Trim();
            s.DeviceName = DeviceBox.Text.Trim();
            s.ReadOnly = ReadOnlySwitch.IsChecked == true;
        });
    }

    private async void OnSaveConnection(object sender, RoutedEventArgs e)
    {
        if (!Valid(out var err)) { SaveNote.Text = err; return; }
        Save();
        SaveNote.Text = Loc.T("Saved.");
        await _host.Node.Apply(_host.S);
    }

    private void OnSignIn(object sender, RoutedEventArgs e)
    {
        if (!Valid(out var err)) { SaveNote.Text = err; return; }
        Save();
        SaveNote.Text = "";
        SignIn.Visibility = Visibility.Visible;
        SignIn.Begin(_host);
    }

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm(Owner, Loc.T("Sign out"), Loc.T("This PC will forget your account. The Drive folder stops until you sign in again. Files already on this PC stay."), Loc.T("Sign out"), true)) return;
        SignIn.Stop();
        SignIn.Visibility = Visibility.Collapsed;
        await _host.Mount.StopAsync();
        try { if (_host.Node.Api != null) await _host.Node.Api.Logout(); } catch (NodeException ex) { SaveNote.Text = ex.Message; }
        await _host.Node.Refresh();
    }

    // --- general ---

    private void OnLang(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LangBox.SelectedItem is not ComboBoxItem { Tag: string v } || v == _host.S.Language) return;
        _host.Settings.Update(s => s.Language = v);
        _host.Restyle();
    }

    private void OnTheme(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string v } || v == _host.S.Theme) return;
        _host.Settings.Update(s => s.Theme = v);
        Theme.Apply(v);
    }

    private async void OnStartup(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = StartupSwitch.IsChecked == true;
        _host.Settings.Update(s => s.StartWithWindows = on);
        if (on) await Startup.Enable(Environment.ProcessPath!); else await Startup.Disable();
    }

    private void OnAlsoClient(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = AlsoClientSwitch.IsChecked == true;
        _host.Settings.Update(s =>
        {
            s.AlsoClient = on;
            if (on && s.HubAddress.Length == 0) s.HubAddress = $"127.0.0.1:{s.Hub.LeafPort}";
            if (on && s.ServerUrl.Length == 0) s.ServerUrl = $"http://127.0.0.1:{s.Hub.DrivePort}";
        });
        if (on) _host.Node.Start();
        _host.Restyle();
    }

    private async void OnChangeRole(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm(Owner, Loc.T("Change how this PC is used"),
                Loc.T("jsync Drive stops, and the setup starts again. Your files, your Drive folder and your sign-in are not deleted."), Loc.T("Continue"))) return;
        await _host.StopEngines();
        _host.Settings.Update(s => s.Mode = "");
        _host.ShowOnboarding();
    }

    private void OnOpenData(object sender, RoutedEventArgs e) => Open(Paths.Root);
    private void OnOpenLogs(object sender, RoutedEventArgs e) => Open(Paths.Logs);

    private static void Open(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
