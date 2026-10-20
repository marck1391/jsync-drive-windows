using System.Text.Json.Serialization;

namespace JsyncDrive.Engine;

// The JSON the jsync node speaks (internal/androidnode/server.go).

public sealed class NodeSettings
{
    [JsonPropertyName("hub")] public string Hub { get; set; } = "";
    [JsonPropertyName("server")] public string Server { get; set; } = "";
    [JsonPropertyName("device")] public string Device { get; set; } = "";
    [JsonPropertyName("read_only")] public bool ReadOnly { get; set; }
}

public sealed class LoginInfo
{
    [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
    [JsonPropertyName("expires_at")] public long ExpiresAt { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class ProtectedFolder
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("open")] public bool Open { get; set; }
}

public static class NodeStates
{
    public const string LoggedOut = "logged_out", Pending = "pending", Connecting = "connecting",
        Online = "online", Offline = "offline", NeedsSetup = "needs_setup";
}

public sealed class NodeStatus
{
    [JsonPropertyName("state")] public string State { get; set; } = NodeStates.LoggedOut;
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("settings")] public NodeSettings Settings { get; set; } = new();
    [JsonPropertyName("machine_id")] public string MachineId { get; set; } = "";
    [JsonPropertyName("read_write")] public bool ReadWrite { get; set; }
    [JsonPropertyName("login")] public LoginInfo? Login { get; set; }
    [JsonPropertyName("protected")] public List<ProtectedFolder>? Protected { get; set; }
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }

    public bool Online => State == NodeStates.Online;
}

public sealed class Entry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("dir")] public bool Dir { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("mtime")] public long Mtime { get; set; } // unix ms
    [JsonPropertyName("protected")] public bool Protected { get; set; }
    [JsonPropertyName("locked")] public bool Locked { get; set; }
    /// <summary>Set on search results: the full Drive path of the entry.</summary>
    [JsonPropertyName("path")] public string? Path { get; set; }

    public DateTime? Modified => Mtime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(Mtime).LocalDateTime : null;
}

public sealed class ListResult
{
    [JsonPropertyName("entries")] public List<Entry>? Entries { get; set; }
    [JsonPropertyName("read_write")] public bool ReadWrite { get; set; }
}

public sealed class Device
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("online")] public bool Online { get; set; }
    [JsonPropertyName("push")] public bool Push { get; set; }
    [JsonPropertyName("auto")] public List<string>? Auto { get; set; }
}

public sealed class DevicesResult
{
    [JsonPropertyName("devices")] public List<Device>? Devices { get; set; }
}

public static class TransferStatus
{
    public const string Waiting = "waiting", Incoming = "incoming", Connecting = "connecting", Active = "active",
        Done = "done", Error = "error", Declined = "declined", Cancelled = "cancelled";
}

public sealed class Transfer
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("direction")] public string Direction { get; set; } = ""; // send | receive
    [JsonPropertyName("peer_id")] public string PeerId { get; set; } = "";
    [JsonPropertyName("peer_name")] public string PeerName { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("mime")] public string? Mime { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("speed")] public double Speed { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("route")] public string? Route { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("created")] public long Created { get; set; }

    public bool Sending => Direction == "send";
    public bool Finished => Status is TransferStatus.Done or TransferStatus.Error or TransferStatus.Declined or TransferStatus.Cancelled;
    public double Fraction => Size > 0 ? Math.Clamp((double)Bytes / Size, 0, 1) : 0;
}

public sealed class TransfersResult
{
    [JsonPropertyName("transfers")] public List<Transfer>? Transfers { get; set; }
}

public sealed class NodeEvent
{
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = ""; // change | protected | state | mounted | warning | p2p
    [JsonPropertyName("op")] public string? Op { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("old")] public string? Old { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("transfer")] public Transfer? Transfer { get; set; }
}

public sealed class DriveShare
{
    [JsonPropertyName("share_id")] public string Id { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("shared_with_email")] public string SharedWith { get; set; } = "";
    [JsonPropertyName("permission")] public string Permission { get; set; } = "read";
}

public sealed class NodeException : Exception
{
    public int Status { get; }
    public NodeException(int status, string message) : base(message) => Status = status;
    public bool Locked => Status == 423;
    public bool WrongPassword => Status == 401;
    public bool NotFound => Status == 404;
}
