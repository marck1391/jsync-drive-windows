using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JsyncDrive.Engine;

public enum ServiceState { NotInstalled, Stopped, Starting, Running, Stopping, Unknown }

/// <summary>
/// This PC as the hub: `jsyncd`, configured with `jsyncd init`, either as a Windows service (needs administrator,
/// asked once) or running only while the app is open.
/// </summary>
public sealed class HubService : IDisposable
{
    public const string ServiceName = "jsyncd";

    private ChildProcessHost? _inApp;
    public event Action? Changed;

    public bool InAppRunning => _inApp is { State: HostState.Running or HostState.Starting };
    public HostState InAppState => _inApp?.State ?? HostState.Stopped;
    public string? InAppDetail => _inApp?.Detail;
    public string[] LogTail() => _inApp?.Tail() ?? Array.Empty<string>();

    // --- configuration ---

    /// <summary>Writes the hub's config and identity (idempotent: an existing identity is kept).</summary>
    public async Task<(bool ok, string text, string machineId, string publicKey)> Configure(HubSettings h, string? jwtSecret)
    {
        Directory.CreateDirectory(Paths.HubData);
        if (!string.IsNullOrWhiteSpace(h.StorageDir)) Directory.CreateDirectory(h.StorageDir);
        var args = new List<string>
        {
            "init", "--config", Paths.HubConfig, "--role", "hub",
            "--host", h.Host, "--port", h.ClientPort.ToString(), "--leaf-port", h.LeafPort.ToString(),
            "--drive-base-dir", h.StorageDir,
        };
        if (!string.IsNullOrWhiteSpace(h.AccountsUrl)) args.AddRange(new[] { "--gotrue-url", h.AccountsUrl });
        if (!string.IsNullOrWhiteSpace(h.DriveWebUrl)) args.AddRange(new[] { "--drive-web-url", h.DriveWebUrl });
        if (h.Admins.Count > 0) args.AddRange(new[] { "--admin-emails", string.Join(",", h.Admins) });
        var env = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(jwtSecret)) env["JSYNC_INIT_JWT_SECRET"] = jwtSecret; // not on the command line
        var (ok, text) = await Run.Capture(Paths.Exe("jsyncd"), args, env).ConfigureAwait(false);
        if (!ok) return (false, text, "", "");
        try
        {
            var line = text.Split('\n').Last(l => l.TrimStart().StartsWith("{"));
            var o = JsonNode.Parse(line)!;
            return (true, text, o["machine_id"]?.GetValue<string>() ?? "", o["public_key"]?.GetValue<string>() ?? "");
        }
        catch { return (true, text, "", ""); }
    }

    public static string[] ServeArgs(HubSettings h) => new[] { "--config", Paths.HubConfig, "--drive-host", h.DriveHost, "--drive-port", h.DrivePort.ToString() };

    // --- the Windows service ---

    public static async Task<ServiceState> QueryService()
    {
        var (_, text) = await Run.Capture("sc.exe", new[] { "query", ServiceName }, null, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (text.Contains("1060") || text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)) return ServiceState.NotInstalled;
        var m = Regex.Match(text, @"STATE\s*:\s*\d+\s+(\w+)");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() switch
        {
            "RUNNING" => ServiceState.Running,
            "STOPPED" => ServiceState.Stopped,
            "START_PENDING" => ServiceState.Starting,
            "STOP_PENDING" => ServiceState.Stopping,
            _ => ServiceState.Unknown,
        } : ServiceState.Unknown;
    }

    /// <summary>Runs jsyncd.exe elevated (the user answers the Windows prompt). Returns the exit code, or null if declined.</summary>
    private static async Task<int?> Elevated(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Paths.Exe("jsyncd"))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Paths.Root,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync().ConfigureAwait(false);
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { return null; } // UAC declined
    }

    public async Task<(bool ok, string text)> InstallService(HubSettings h)
    {
        var args = new List<string> { "install", "--no-wizard" };
        args.AddRange(ServeArgs(h));
        var code = await Elevated(args).ConfigureAwait(false);
        Changed?.Invoke();
        return code switch { null => (false, "declined"), 0 => (true, ""), _ => (false, $"jsyncd install exited with code {code}") };
    }

    public async Task<(bool ok, string text)> Control(string verb)
    {
        var code = await Elevated(new[] { verb }).ConfigureAwait(false);
        Changed?.Invoke();
        return code switch { null => (false, "declined"), 0 => (true, ""), _ => (false, $"jsyncd {verb} exited with code {code}") };
    }

    // --- running inside the app (no administrator needed) ---

    public void StartInApp(HubSettings h)
    {
        _inApp ??= CreateInApp(h);
        _inApp.Start();
    }

    private ChildProcessHost CreateInApp(HubSettings h)
    {
        var host = new ChildProcessHost("hub", () => new ProcessStartInfo(Paths.Exe("jsyncd"), ServeArgs(h)) { WorkingDirectory = Paths.HubData },
            line => line.StartsWith("jsyncd: ready"), Path.Combine(Paths.Logs, "hub.log"));
        host.Changed += () => Changed?.Invoke();
        return host;
    }

    public async Task StopInApp()
    {
        if (_inApp != null) await _inApp.StopAsync().ConfigureAwait(false);
    }

    // --- what to tell the devices ---

    /// <summary>The addresses of this PC other devices on the network can use to reach the hub.</summary>
    public static IEnumerable<string> LocalAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var a in ni.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                    yield return a.Address.ToString();
        }
    }

    /// <summary>True when something already listens on this local TCP port.</summary>
    public static bool PortInUse(int port)
    {
        try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port); }
        catch { return false; }
    }

    public void Dispose() => _inApp?.Dispose();
}
