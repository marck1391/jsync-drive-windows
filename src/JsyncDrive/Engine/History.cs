using System.Text.Json;
using System.Text.Json.Serialization;

namespace JsyncDrive.Engine;

/// <summary>One file that went to, or came from, another device.</summary>
public sealed class HistoryItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("at")] public long At { get; set; }              // unix ms, when it ended
    [JsonPropertyName("sent")] public bool Sent { get; set; }
    [JsonPropertyName("peer")] public string Peer { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("mime")] public string? Mime { get; set; }
    [JsonPropertyName("result")] public string Result { get; set; } = ""; // done | error | declined | cancelled
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }       // where a received file was saved / the file that was sent

    [JsonIgnore] public DateTime When => DateTimeOffset.FromUnixTimeMilliseconds(At).LocalDateTime;
    [JsonIgnore] public bool Ok => Result == "done";
    [JsonIgnore] public bool Exists => !string.IsNullOrEmpty(Path) && File.Exists(Path);
}

/// <summary>The record of shared and received files, kept across runs (newest first, last 500).</summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly object _gate = new();
    private List<HistoryItem> _items = new();

    public event Action? Changed;

    public HistoryStore()
    {
        try
        {
            if (File.Exists(Paths.History))
                _items = JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(Paths.History)) ?? new();
        }
        catch { /* start empty */ }
    }

    public IReadOnlyList<HistoryItem> Items
    {
        get { lock (_gate) return _items.ToArray(); }
    }

    public void Add(HistoryItem item)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.Id == item.Id);
            _items.Insert(0, item);
            if (_items.Count > 500) _items.RemoveRange(500, _items.Count - 500);
            Save();
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.Id == id);
            Save();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            var tmp = Paths.History + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Opts));
            File.Move(tmp, Paths.History, true);
        }
        catch { /* the history is a convenience */ }
    }
}
