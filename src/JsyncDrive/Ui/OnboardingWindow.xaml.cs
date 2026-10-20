using System.Windows;
using System.Windows.Controls;
using JsyncDrive.Engine;
using JsyncDrive.Ui;
using Microsoft.Win32;

namespace JsyncDrive;

/// <summary>First run: hub or client, the folder, and signing in.</summary>
public partial class OnboardingWindow : Window
{
    private enum Step { Choose, Client, Hub, SignIn, Done }

    private readonly AppHost _host;
    private Step _step = Step.Choose;
    private bool _hub;       // the role chosen
    private bool _alsoClient;

    public OnboardingWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        WinUtil.Chrome(this);
        var s = host.S;
        HubBox.Text = s.HubAddress;
        ServerBox.Text = s.ServerUrl;
        DeviceBox.Text = s.DeviceName;
        MountBox.Text = s.Mount.Directory;
        StorageBox.Text = s.Hub.StorageDir;
        LeafPortBox.Text = s.Hub.LeafPort.ToString();
        DrivePortBox.Text = s.Hub.DrivePort.ToString();
        SignIn.SignedIn += () => Go(Step.Done);
        Closed += (_, _) => SignIn.Stop();
        Show(Step.Choose);
    }

    private void Show(Step step)
    {
        _step = step;
        StepChoose.Visibility = step == Step.Choose ? Visibility.Visible : Visibility.Collapsed;
        StepClient.Visibility = step == Step.Client ? Visibility.Visible : Visibility.Collapsed;
        StepHub.Visibility = step == Step.Hub ? Visibility.Visible : Visibility.Collapsed;
        StepSignIn.Visibility = step == Step.SignIn ? Visibility.Visible : Visibility.Collapsed;
        StepDone.Visibility = step == Step.Done ? Visibility.Visible : Visibility.Collapsed;
        BackBtn.Visibility = step is Step.Choose or Step.Done ? Visibility.Collapsed : Visibility.Visible;
        NextBtn.Content = step switch { Step.Done => Loc.T("Finish"), Step.SignIn => Loc.T("Skip for now"), _ => Loc.T("Continue") };
        NextBtn.IsEnabled = true;
        Note.Text = "";
    }

    private void Go(Step step)
    {
        Show(step);
        if (step == Step.SignIn)
        {
            _host.Node.Start();
            SignIn.Begin(_host);
        }
        if (step == Step.Done)
        {
            DoneText.Text = _hub
                ? (_alsoClient ? Loc.T("The hub is running, and this PC is signed in to it. Point your other devices at the addresses on the Server page.")
                               : Loc.T("The hub is running. Point your other devices at the addresses on the Server page."))
                : Loc.T("Your Drive folder is being set up. You will find it in Explorer, and jsync Drive stays in the notification area.");
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Client: Go(_hub ? Step.Hub : Step.Choose); break;
            case Step.Hub: Go(Step.Choose); break;
            case Step.SignIn: SignIn.Stop(); Go(Step.Client); break;
        }
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        Note.Text = "";
        switch (_step)
        {
            case Step.Choose:
                _hub = HubCard.IsChecked == true;
                Go(_hub ? Step.Hub : Step.Client);
                break;

            case Step.Hub:
                await ApplyHub();
                break;

            case Step.Client:
                if (!ReadClient()) return;
                Go(Step.SignIn);
                break;

            case Step.SignIn:
                // Skipped: the person signs in later from Settings.
                SignIn.Stop();
                Go(Step.Done);
                break;

            case Step.Done:
                Finish();
                break;
        }
    }

    private bool ReadClient()
    {
        var hub = HubBox.Text.Trim();
        var server = ServerBox.Text.Trim();
        if (hub.Length == 0) { Note.Text = Loc.T("Enter the hub address."); return false; }
        if (server.Length == 0) { Note.Text = Loc.T("Enter the Drive address."); return false; }
        if (DeviceBox.Text.Trim().Length == 0) { Note.Text = Loc.T("Give this PC a name."); return false; }
        if (MountBox.Text.Trim().Length == 0) { Note.Text = Loc.T("Choose a folder for your Drive."); return false; }
        _host.Settings.Update(s =>
        {
            s.HubAddress = hub;
            s.ServerUrl = server;
            s.DeviceName = DeviceBox.Text.Trim();
            s.Mount.Directory = MountBox.Text.Trim();
            s.Mount.Enabled = true;
        });
        return true;
    }

    private async Task ApplyHub()
    {
        if (!int.TryParse(LeafPortBox.Text, out var leaf) || leaf is < 1 or > 65535) { Note.Text = Loc.T("The hub port must be a number from 1 to 65535."); return; }
        if (!int.TryParse(DrivePortBox.Text, out var drive) || drive is < 1 or > 65535) { Note.Text = Loc.T("The Drive port must be a number from 1 to 65535."); return; }
        if (StorageBox.Text.Trim().Length == 0) { Note.Text = Loc.T("Choose where the Drive files are kept."); return; }
        if (AlsoClientSwitch.IsChecked == true && (SecretBox.Password.Length == 0 || AccountsBox.Text.Trim().Length == 0))
        {
            Note.Text = Loc.T("To sign this PC in to its own hub now, enter the account service address and secret. Or turn off “Also use this PC as a client”.");
            return;
        }
        foreach (var p in new[] { leaf, drive })
            if (HubService.PortInUse(p)) { Note.Text = Loc.T("Port {0} is already in use on this PC. Choose another.", p); return; }

        var h = new HubSettings
        {
            LeafPort = leaf, DrivePort = drive, StorageDir = StorageBox.Text.Trim(), AccountsUrl = AccountsBox.Text.Trim(),
            Admins = AdminBox.Text.Split(',', ';').Select(a => a.Trim()).Where(a => a.Length > 0).ToList(),
            Configured = true, RunInApp = true,
        };
        NextBtn.IsEnabled = false;
        Note.Foreground = Theme.Brush("MutedBrush");
        Note.Text = Loc.T("Setting up the hub…");
        var (ok, text, _, _) = await _host.Hub.Configure(h, SecretBox.Password.Length > 0 ? SecretBox.Password : null);
        Note.Foreground = Theme.Brush("DangerBrush");
        if (!ok) { Note.Text = text; NextBtn.IsEnabled = true; return; }
        _host.Settings.Update(s => s.Hub = h);
        _hub = true;
        _alsoClient = AlsoClientSwitch.IsChecked == true;

        // The hub runs now, so signing in can reach it.
        _host.Hub.StartInApp(h);
        if (_alsoClient)
        {
            HubBox.Text = $"127.0.0.1:{leaf}";
            ServerBox.Text = $"http://127.0.0.1:{drive}";
            Go(Step.Client);
        }
        else Go(Step.Done);
    }

    private void Finish()
    {
        _host.Settings.Update(s =>
        {
            s.Mode = _hub ? "hub" : "client";
            s.AlsoClient = _hub && _alsoClient;
        });
        if (_host.S.StartWithWindows) _ = Startup.Enable(Environment.ProcessPath!);
        Close();
    }

    private void OnChooseMount(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Where should your Drive folder be?") };
        if (dlg.ShowDialog(this) == true) MountBox.Text = dlg.FolderName;
    }

    private void OnChooseStorage(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Where the Drive files are kept") };
        if (dlg.ShowDialog(this) == true) StorageBox.Text = dlg.FolderName;
    }
}
