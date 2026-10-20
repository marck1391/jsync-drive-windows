using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JsyncDrive.Engine;
using JsyncDrive.Ui;
using Microsoft.Win32;

namespace JsyncDrive;

public partial class FolderPage : UserControl, IPage
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _log = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _loading;
    private bool _wired;

    private static readonly (string value, string label)[] FreeChoices =
    {
        ("", "Never"), ("7d", "7 days"), ("30d", "30 days"), ("90d", "90 days"),
    };

    private Window? Owner => Window.GetWindow(this);

    public FolderPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        foreach (var (v, l) in FreeChoices) FreeBox.Items.Add(new ComboBoxItem { Content = Loc.T(l), Tag = v });
        _log.Tick += (_, _) => RefreshLog();
        Loaded += (_, _) => _log.Start();
        Unloaded += (_, _) => _log.Stop();
    }

    public void OnShown()
    {
        if (!_wired)
        {
            _wired = true;
            _host.Mount.Changed += () => Dispatcher.BeginInvoke(Refresh);
            _host.Node.StatusChanged += () => Dispatcher.BeginInvoke(Refresh);
        }
        Refresh();
    }

    private void Refresh()
    {
        _loading = true;
        var s = _host.S;
        var m = _host.Mount;
        DirBox.Text = s.Mount.Directory;
        PathText.Text = s.Mount.Directory;
        EnabledSwitch.IsChecked = s.Mount.Enabled;
        RwSwitch.IsChecked = s.Mount.ReadWrite;
        StartupSwitch.IsChecked = s.StartWithWindows;
        var idx = Array.FindIndex(FreeChoices, c => c.value == s.Mount.FreeAfter);
        FreeBox.SelectedIndex = idx < 0 ? 0 : idx;

        string title, text;
        string dot;
        if (!MountService.CanMount())
        {
            title = Loc.T("Sign in first"); text = Loc.T("The folder needs this PC to be signed in to your Drive. Do that in Settings."); dot = "MutedBrush";
        }
        else switch (m.State)
        {
            case HostState.Running: title = Loc.T("Running"); text = Loc.T("Files show in Explorer and are downloaded when you open them."); dot = "SuccessBrush"; break;
            case HostState.Starting: title = Loc.T("Starting…"); text = Loc.T("Connecting to your Drive."); dot = "WarnBrush"; break;
            case HostState.Failed: title = Loc.T("Stopped, trying again"); text = m.Detail ?? ""; dot = "DangerBrush"; break;
            default: title = Loc.T("Stopped"); text = s.Mount.Enabled ? "" : Loc.T("The folder is turned off."); dot = "MutedBrush"; break;
        }
        StateTitle.Text = title;
        StateText.Text = text;
        StateText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Dot.Fill = Theme.Brush(dot);
        var running = m.State is HostState.Running or HostState.Starting or HostState.Failed;
        ToggleBtn.Content = running ? Loc.T("Stop") : Loc.T("Start");
        ToggleBtn.IsEnabled = MountService.CanMount();
        OpenBtn.IsEnabled = Directory.Exists(s.Mount.Directory);
        RefreshLog();
        _loading = false;
    }

    private void RefreshLog()
    {
        var tail = _host.Mount.LogTail();
        var text = string.Join(Environment.NewLine, tail.TakeLast(60));
        if (LogBox.Text != text)
        {
            LogBox.Text = text;
            LogBox.ScrollToEnd();
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_host.S.Mount.Directory}\"") { UseShellExecute = true }); } catch { /* none */ }
    }

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_host.Mount.State == HostState.Stopped)
        {
            _host.Settings.Update(s => s.Mount.Enabled = true);
            _host.Mount.Start();
        }
        else
        {
            _host.Settings.Update(s => s.Mount.Enabled = false);
            await _host.Mount.StopAsync();
        }
        Refresh();
    }

    private async void OnChangeDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Folder on this PC"), InitialDirectory = Directory.Exists(_host.S.Mount.Directory) ? _host.S.Mount.Directory : null };
        if (dlg.ShowDialog(Owner) != true) return;
        var next = dlg.FolderName;
        if (string.Equals(Path.GetFullPath(next), Path.GetFullPath(_host.S.Mount.Directory), StringComparison.OrdinalIgnoreCase)) return;
        if (Directory.Exists(next) && Directory.EnumerateFileSystemEntries(next).Any())
        {
            Dialogs.Info(Owner, Loc.T("Choose an empty folder"), Loc.T("That folder already has files in it. Pick an empty one, or a new folder."));
            return;
        }
        var old = _host.S.Mount.Directory;
        var wasRunning = _host.Mount.State != HostState.Stopped;
        // The old folder is detached from Windows first, so it can be deleted or kept as it is.
        if (wasRunning || Directory.Exists(old)) await _host.Mount.UnlinkAsync(old);
        _host.Settings.Update(s => s.Mount.Directory = next);
        if (_host.S.Mount.Enabled) _host.Mount.Start();
        Refresh();
    }

    private void OnEnabled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = EnabledSwitch.IsChecked == true;
        _host.Settings.Update(s => s.Mount.Enabled = on);
        if (on) _host.Mount.Start(); else _ = _host.Mount.StopAsync();
    }

    private async void OnStartup(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = StartupSwitch.IsChecked == true;
        _host.Settings.Update(s => s.StartWithWindows = on);
        if (on) await Startup.Enable(Environment.ProcessPath!); else await Startup.Disable();
    }

    private void OnRw(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = RwSwitch.IsChecked == true;
        _host.Settings.Update(s => s.Mount.ReadWrite = on);
    }

    private void OnFree(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FreeBox.SelectedItem is not ComboBoxItem { Tag: string v }) return;
        if (v != _host.S.Mount.FreeAfter) _host.Settings.Update(s => s.Mount.FreeAfter = v);
    }

    private async void OnUnlink(object sender, RoutedEventArgs e)
    {
        var dir = _host.S.Mount.Directory;
        if (!Dialogs.Confirm(Owner, Loc.T("Remove the folder from Explorer"),
                Loc.T("The folder stops syncing and is detached from Windows. Files you already downloaded stay readable; the rest cannot be opened any more. You can turn it on again later."),
                Loc.T("Remove"), true)) return;
        _host.Settings.Update(s => s.Mount.Enabled = false);
        var (ok, text) = await _host.Mount.UnlinkAsync(dir);
        Dialogs.Info(Owner, ok ? Loc.T("Done") : Loc.T("Something went wrong"), ok ? Loc.T("The folder is detached. You can delete it now.") : text);
        Refresh();
    }
}
