using System.Diagnostics;

namespace JsyncDrive.Engine;

/// <summary>
/// `jsync mount drive`: the Drive as a real folder through the Windows Cloud Filter API (files appear in Explorer and
/// are downloaded when opened). It runs as a hidden process of its own, with the node's identity and login.
/// </summary>
public sealed class MountService : IDisposable
{
    private readonly ChildProcessHost _host;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string> _machineId;

    public HostState State => _host.State;
    public string? Detail => _host.Detail;
    public string[] LogTail() => _host.Tail();
    public event Action? Changed;

    public MountService(Func<AppSettings> settings, Func<string> machineId)
    {
        _settings = settings;
        _machineId = machineId;
        _host = new ChildProcessHost("mount", Command, line => line.StartsWith("mounting "), Path.Combine(Paths.Logs, "mount.log"));
        _host.Changed += () => Changed?.Invoke();
    }

    private ProcessStartInfo Command()
    {
        var s = _settings();
        var args = new List<string>
        {
            "mount",
            "--config", Path.Combine(Paths.NodeData, "jsync.db"),
            "--hub", s.HubAddress,
            "--identity", Path.Combine(Paths.NodeData, "identity.json"),
            "--prekeys", Path.Combine(Paths.NodeData, "prekeys.json"),
            "--machine-id", _machineId(),
            "--cache-dir", Paths.MountCache,
        };
        if (!string.IsNullOrWhiteSpace(s.ServerUrl)) args.AddRange(new[] { "--server", s.ServerUrl });
        if (!string.IsNullOrWhiteSpace(s.Mount.FreeAfter)) args.AddRange(new[] { "--free-after", s.Mount.FreeAfter });
        if (s.Mount.ReadWrite) args.Add("--rw");
        args.Add("drive");
        args.Add(s.Mount.Directory);

        Directory.CreateDirectory(s.Mount.Directory);
        var psi = new ProcessStartInfo(Paths.Exe("jsync"), args) { WorkingDirectory = Paths.Root };
        psi.Environment["JSYNC_CREDENTIALS"] = "file";
        return psi;
    }

    /// <summary>True when the identity and login the mount needs exist (the node has been through sign-in).</summary>
    public static bool CanMount() =>
        File.Exists(Path.Combine(Paths.NodeData, "identity.json")) && File.Exists(Path.Combine(Paths.NodeData, "drive_auth.json"));

    public void Start()
    {
        var s = _settings();
        if (!s.Mount.Enabled || string.IsNullOrWhiteSpace(s.HubAddress) || !CanMount()) return;
        _host.Start();
    }

    public Task StopAsync() => _host.StopAsync();

    public void Restart()
    {
        if (_host.State == HostState.Stopped) Start();
        else _host.Restart();
    }

    /// <summary>Detach the folder from the Cloud Filter API so it can be deleted (a mount leaves it registered).</summary>
    public async Task<(bool ok, string text)> UnlinkAsync(string directory)
    {
        await StopAsync().ConfigureAwait(false);
        return await Run.Capture(Paths.Exe("jsync"), new[] { "mount", "--unlink", directory }).ConfigureAwait(false);
    }

    public void Dispose() => _host.Dispose();
}

/// <summary>Runs a short engine command and returns its output.</summary>
public static class Run
{
    public static async Task<(bool ok, string text)> Capture(string exe, IEnumerable<string> args, IDictionary<string, string>? env = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        try
        {
            using var p = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var text = (await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false)).Trim();
            return (p.ExitCode == 0, text);
        }
        catch (Exception e) { return (false, e.Message); }
    }
}
