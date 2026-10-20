using System.Diagnostics;
using System.Security.Cryptography;

namespace JsyncDrive.Engine;

/// <summary>
/// The client node of this PC: runs jsyncnode.exe, keeps its status fresh and relays what it reports (changes in the
/// Drive, device-to-device transfers). Events come on thread-pool threads.
/// </summary>
public sealed class NodeHost : IDisposable
{
    private readonly ChildProcessHost _host;
    private readonly object _gate = new();
    private string _token = "";
    private CancellationTokenSource? _events;

    public NodeApi? Api { get; private set; }
    public NodeStatus Status { get; private set; } = new();
    public HostState ProcessState => _host.State;
    public string? ProcessDetail => _host.Detail;
    public string[] LogTail() => _host.Tail();

    public event Action? StatusChanged;
    public event Action<NodeEvent>? EventReceived;
    public event Action<Transfer>? TransferChanged;
    /// <summary>The node (re)started: listeners should re-read what they show.</summary>
    public event Action? Ready;

    public NodeHost()
    {
        Paths.EnsureAll();
        _host = new ChildProcessHost("node", Command, l => l.StartsWith("READY "), Path.Combine(Paths.Logs, "node.log"));
        _host.Line += OnLine;
        _host.Changed += OnHostChanged;
    }

    private ProcessStartInfo Command()
    {
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var psi = new ProcessStartInfo(Paths.Exe("jsyncnode"), new[]
        {
            "--data", Paths.NodeData, "--cache", Paths.NodeCache, "--exit-on-stdin-eof",
        })
        {
            WorkingDirectory = Paths.Root,
        };
        psi.Environment["JSYNC_NODE_TOKEN"] = _token;
        // The OS credential store is keyed by server alone: keep this app's login out of it so it never replaces the CLI's.
        psi.Environment["JSYNC_CREDENTIALS"] = "file";
        return psi;
    }

    public void Start()
    {
        if (!File.Exists(Paths.Exe("jsyncnode")))
        {
            Failure = $"jsyncnode.exe was not found in {Paths.Engine}";
            StatusChanged?.Invoke();
            return;
        }
        Failure = null;
        _host.Start();
    }

    public string? Failure { get; private set; }

    public Task StopAsync()
    {
        StopEvents();
        return _host.StopAsync();
    }

    private void OnLine(string line)
    {
        if (!line.StartsWith("READY ")) return;
        if (!int.TryParse(line.AsSpan(6).Trim(), out var port)) return;
        lock (_gate)
        {
            Api?.Dispose();
            Api = new NodeApi(port, _token);
        }
    }

    private void OnHostChanged()
    {
        if (_host.State == HostState.Running && Api != null)
        {
            _ = Task.Run(async () =>
            {
                await Refresh().ConfigureAwait(false);
                StartEvents();
                Ready?.Invoke();
            });
        }
        else if (_host.State != HostState.Running)
        {
            StopEvents();
            lock (_gate)
            {
                Status = new NodeStatus { State = NodeStates.Offline, Detail = _host.Detail, Settings = Status.Settings, MachineId = Status.MachineId, Email = Status.Email };
            }
            StatusChanged?.Invoke();
        }
    }

    public async Task Refresh()
    {
        var api = Api;
        if (api == null) return;
        try
        {
            var s = await api.Status().ConfigureAwait(false);
            lock (_gate) Status = s;
            StatusChanged?.Invoke();
        }
        catch { /* the next event or poll tries again */ }
    }

    private void StartEvents()
    {
        StopEvents();
        var api = Api;
        if (api == null) return;
        var cts = new CancellationTokenSource();
        _events = cts;
        _ = Task.Run(async () =>
        {
            long since = 0;
            try { since = Status.Seq; } catch { /* first read */ }
            // The transfers that already exist, so a restart of the app sees them.
            try { foreach (var t in await api.Transfers(cts.Token).ConfigureAwait(false)) TransferChanged?.Invoke(t); } catch { /* none yet */ }
            var gap = TimeSpan.FromSeconds(1);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await foreach (var ev in api.Events(since, cts.Token).ConfigureAwait(false))
                    {
                        since = ev.Seq;
                        gap = TimeSpan.FromSeconds(1);
                        if (ev.Kind == "state" || ev.Kind == "mounted") _ = Refresh();
                        if (ev.Kind == "p2p" && ev.Transfer != null) TransferChanged?.Invoke(ev.Transfer);
                        EventReceived?.Invoke(ev);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* reconnect below */ }
                try { await Task.Delay(gap, cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                if (gap < TimeSpan.FromSeconds(15)) gap += gap;
            }
        });
        // A slow poll keeps the status honest even if an event is missed.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), cts.Token).ConfigureAwait(false);
                    await Refresh().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void StopEvents()
    {
        var c = Interlocked.Exchange(ref _events, null);
        try { c?.Cancel(); } catch { /* disposed */ }
    }

    /// <summary>Makes the node's hub / Drive / device name / read-only match <paramref name="s"/>.</summary>
    public async Task Apply(AppSettings s)
    {
        var api = Api;
        if (api == null) return;
        var cur = Status.Settings;
        string? hub = cur.Hub != s.HubAddress ? s.HubAddress : null;
        string? server = cur.Server != s.ServerUrl ? s.ServerUrl : null;
        string? device = cur.Device != s.DeviceName ? s.DeviceName : null;
        bool? ro = cur.ReadOnly != s.ReadOnly ? s.ReadOnly : null;
        if (hub == null && server == null && device == null && ro == null) return;
        var st = await api.SetSettings(hub, server, device, ro).ConfigureAwait(false);
        lock (_gate) Status = st;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        StopEvents();
        _host.Dispose();
        Api?.Dispose();
    }
}
