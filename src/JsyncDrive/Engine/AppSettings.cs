using System.Text.Json;
using System.Text.Json.Serialization;

namespace JsyncDrive.Engine;

public sealed class TrustedDevice
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class HubSettings
{
    public string Host { get; set; } = "0.0.0.0";   // address the hub listens on for devices
    public int LeafPort { get; set; } = 7830;       // where every other device's node connects
    public int ClientPort { get; set; } = 4222;     // the hub's own client port (loopback)
    public string DriveHost { get; set; } = "0.0.0.0";
    public int DrivePort { get; set; } = 8780;      // the Drive API
    public string StorageDir { get; set; } = Paths.DefaultHubStorage; // where accounts' files live
    public string AccountsUrl { get; set; } = "";   // the account service (GoTrue) the Drive API trusts
    public string DriveWebUrl { get; set; } = "";   // the Drive web app
    public List<string> Admins { get; set; } = new();
    public bool Configured { get; set; }
    /// <summary>Run the hub while the app is open (no administrator needed). Off when it runs as a Windows service.</summary>
    public bool RunInApp { get; set; } = true;
}

public sealed class MountSettings
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = Paths.DefaultMountDirectory;
    /// <summary>Give a downloaded file's disk space back after this long unopened ("" = never), e.g. "30d".</summary>
    public string FreeAfter { get; set; } = "";
    public bool ReadWrite { get; set; } = true;
}

/// <summary>What the app remembers between runs (%LOCALAPPDATA%\jsync Drive\settings.json).</summary>
public sealed class AppSettings
{
    /// <summary>"" until onboarding is done, then "client" or "hub".</summary>
    public string Mode { get; set; } = "";
    /// <summary>A hub PC can also be a client of its own hub.</summary>
    public bool AlsoClient { get; set; }

    public string Language { get; set; } = "auto"; // auto | en | es
    public string Theme { get; set; } = "system";  // system | light | dark
    public bool StartWithWindows { get; set; } = true;

    public string HubAddress { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public string DeviceName { get; set; } = Environment.MachineName;
    public bool ReadOnly { get; set; }

    public MountSettings Mount { get; set; } = new();
    public HubSettings Hub { get; set; } = new();

    public string ReceiveDirectory { get; set; } = Paths.DefaultReceiveDirectory;
    public bool TransferPopups { get; set; } = true;
    public bool AcceptFromAllMyDevices { get; set; }
    public List<TrustedDevice> Trusted { get; set; } = new();

    public bool GridView { get; set; } = true;

    [JsonIgnore] public bool IsClient => Mode == "client" || (Mode == "hub" && AlsoClient);
    [JsonIgnore] public bool IsHub => Mode == "hub";
    [JsonIgnore] public bool Onboarded => Mode != "";
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();

    public AppSettings Value { get; private set; } = new();
    public event Action? Changed;

    public static SettingsStore Load()
    {
        var s = new SettingsStore();
        try
        {
            if (File.Exists(Paths.Settings))
                s.Value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.Settings), Opts) ?? new();
        }
        catch { /* a corrupt file starts over: nothing in it is irreplaceable */ }
        return s;
    }

    /// <summary>Changes the settings under the lock, saves them and tells the listeners.</summary>
    public void Update(Action<AppSettings> change)
    {
        lock (_gate)
        {
            change(Value);
            Directory.CreateDirectory(Paths.Root);
            var tmp = Paths.Settings + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Value, Opts));
            File.Move(tmp, Paths.Settings, true);
        }
        Changed?.Invoke();
    }

    public bool IsTrusted(string deviceId) => Value.Trusted.Any(t => t.Id == deviceId);

    public void Trust(string id, string name) => Update(s =>
    {
        var t = s.Trusted.FirstOrDefault(x => x.Id == id);
        if (t == null) s.Trusted.Add(new TrustedDevice { Id = id, Name = name });
        else t.Name = name;
    });

    public void Untrust(string id) => Update(s => s.Trusted.RemoveAll(t => t.Id == id));
}
