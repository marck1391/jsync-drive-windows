using System.Windows;
using System.Windows.Threading;
using JsyncDrive.Engine;
using JsyncDrive.Ui;

namespace JsyncDrive;

/// <summary>Everything the app runs, and how the parts are tied together.</summary>
public sealed class AppHost
{
    public static AppHost Current { get; private set; } = null!;

    public SettingsStore Settings { get; } = SettingsStore.Load();
    public NodeHost Node { get; } = new();
    public HubService Hub { get; } = new();
    public HistoryStore History { get; } = new();
    public MountService Mount { get; }
    public TransferCoordinator Transfers { get; }
    public Dispatcher UI { get; }

    public TrayIcon? Tray => _tray;
    private TrayIcon? _tray;
    private PopupManager? _popups;
    private MainWindow? _main;
    private string _mountKey = "";

    public AppHost(Dispatcher ui)
    {
        Current = this;
        UI = ui;
        Mount = new MountService(() => Settings.Value, () => Node.Status.MachineId);
        Transfers = new TransferCoordinator(Node, Settings, History);
    }

    public AppSettings S => Settings.Value;

    // --- life ---

    public void Start(bool background)
    {
        Paths.EnsureAll();
        Loc.Use(S.Language);
        Theme.Apply(S.Theme);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => UI.BeginInvoke(() => { if (S.Theme == "system") Theme.Apply("system"); });

        _tray = new TrayIcon(this);
        _popups = new PopupManager(this);

        Node.StatusChanged += () => UI.BeginInvoke(OnNodeStatus);
        Node.Ready += () => _ = OnNodeReady();
        Mount.Changed += () => UI.BeginInvoke(() => _tray?.Refresh());
        Hub.Changed += () => UI.BeginInvoke(() => _tray?.Refresh());
        Settings.Changed += () => UI.BeginInvoke(OnSettingsChanged);

        if (!S.Onboarded)
        {
            ShowOnboarding();
            return;
        }
        StartEngines();
        if (!background) ShowMain();
    }

    public void StartEngines()
    {
        if (S.IsClient) Node.Start();
        if (S.IsHub && S.Hub.Configured && S.Hub.RunInApp) Hub.StartInApp(S.Hub);
        _tray?.Refresh();
    }

    public async Task StopEngines()
    {
        await Mount.StopAsync();
        await Node.StopAsync();
        await Hub.StopInApp();
    }

    public async void Quit()
    {
        _tray?.Dispose();
        try { await StopEngines().WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* leaving anyway */ }
        Node.Dispose();
        Mount.Dispose();
        Hub.Dispose();
        Application.Current.Shutdown();
    }

    // --- windows ---

    public void ShowMain(string? page = null)
    {
        if (!S.Onboarded) { ShowOnboarding(); return; }
        if (_main == null || !_main.IsLoaded)
        {
            _main = new MainWindow(this);
            _main.Closed += (_, _) => _main = null;
        }
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        if (page != null) _main.Navigate(page);
        _main.Activate();
    }

    private OnboardingWindow? _onboarding;

    public void ShowOnboarding()
    {
        if (_onboarding is { IsLoaded: true }) { _onboarding.Activate(); return; }
        _onboarding = new OnboardingWindow(this);
        _onboarding.Closed += (_, _) =>
        {
            _onboarding = null;
            if (S.Onboarded)
            {
                StartEngines();
                ShowMain();
            }
            else if (_main == null) Application.Current.Shutdown(); // closed the first-run window without finishing
        };
        _onboarding.Show();
    }

    /// <summary>The language or theme changed: windows are built again so every text follows.</summary>
    public void Restyle()
    {
        Loc.Use(S.Language);
        Theme.Apply(S.Theme);
        var page = _main?.CurrentPage;
        var was = _main != null;
        _main?.CloseForReal();
        _tray?.Rebuild();
        if (was) ShowMain(page);
    }

    // --- what the parts report ---

    private void OnNodeStatus()
    {
        var st = Node.Status;
        _tray?.Refresh();
        // Mount the Drive as a folder once there is a login to mount with.
        var loggedIn = !string.IsNullOrEmpty(st.Email) && st.State != NodeStates.LoggedOut && st.State != NodeStates.Pending;
        if (loggedIn && S.Mount.Enabled && S.IsClient) StartMountIfNeeded();
        if (!loggedIn && Mount.State != HostState.Stopped) _ = Mount.StopAsync();
    }

    private async Task OnNodeReady()
    {
        try { await Node.Apply(S); } catch { /* shown by the status */ }
    }

    private void StartMountIfNeeded()
    {
        var key = MountKey();
        if (Mount.State == HostState.Stopped || Mount.State == HostState.Failed && key != _mountKey)
        {
            _mountKey = key;
            Mount.Start();
        }
    }

    private string MountKey() => string.Join("|", S.HubAddress, S.ServerUrl, S.Mount.Directory, S.Mount.FreeAfter, S.Mount.ReadWrite);

    private void OnSettingsChanged()
    {
        if (S.IsClient)
        {
            _ = Node.Apply(S);
            var key = MountKey();
            if (key != _mountKey && Mount.State != HostState.Stopped)
            {
                _mountKey = key;
                Mount.Restart();
            }
            if (!S.Mount.Enabled && Mount.State != HostState.Stopped) _ = Mount.StopAsync();
            if (S.Mount.Enabled && Mount.State == HostState.Stopped && Node.Status.Email is { Length: > 0 }) StartMountIfNeeded();
        }
        _tray?.Refresh();
    }

    // --- one line for the tray and the sidebar ---

    public string StatusLine()
    {
        if (!S.Onboarded) return Loc.T("Not set up yet");
        var parts = new List<string>();
        if (S.IsClient)
        {
            var st = Node.Status;
            parts.Add(st.State switch
            {
                NodeStates.Online => Loc.T("Connected") + (string.IsNullOrEmpty(st.Email) ? "" : " · " + st.Email),
                NodeStates.Connecting => Loc.T("Connecting…"),
                NodeStates.Pending => Loc.T("Waiting for you to approve this PC"),
                NodeStates.LoggedOut => Loc.T("Signed out"),
                NodeStates.NeedsSetup => Loc.T("Needs the hub and Drive address"),
                _ => Loc.T("Offline"),
            });
        }
        if (S.IsHub)
            parts.Add(Hub.InAppRunning ? Loc.T("Hub running") : Loc.T("Hub stopped"));
        return string.Join(" · ", parts);
    }
}
