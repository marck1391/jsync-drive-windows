namespace JsyncDrive.Engine;

/// <summary>
/// What the app does about device-to-device transfers: follows each one, accepts those from trusted devices without
/// asking, files received files in the receive folder and keeps the history. Everything it cannot decide alone (a
/// request from a device you have not trusted) it passes on through <see cref="NeedsAnswer"/>.
/// </summary>
public sealed class TransferCoordinator
{
    private readonly NodeHost _node;
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly object _gate = new();
    private readonly Dictionary<string, Transfer> _live = new();
    private readonly HashSet<string> _answered = new();
    private readonly HashSet<string> _recorded = new();

    /// <summary>A request from a device that is not trusted: ask the person. (Also raised again for each update.)</summary>
    public event Action<Transfer>? NeedsAnswer;
    /// <summary>Any transfer changed (new, progress, finished). Raised on a thread-pool thread.</summary>
    public event Action<Transfer>? Updated;
    /// <summary>A received file is in the receive folder.</summary>
    public event Action<Transfer, string>? Received;
    /// <summary>The node forgot a transfer (the sender withdrew a request before it was answered, say).</summary>
    public event Action<string>? Forgotten;

    public TransferCoordinator(NodeHost node, SettingsStore settings, HistoryStore history)
    {
        _node = node;
        _settings = settings;
        _history = history;
        node.TransferChanged += t => _ = OnChanged(t);
        node.EventReceived += ev =>
        {
            if (ev.Kind != "p2p" || ev.State != "dismissed" || string.IsNullOrEmpty(ev.Path)) return;
            lock (_gate) { _live.Remove(ev.Path); _answered.Remove(ev.Path); }
            Forgotten?.Invoke(ev.Path);
        };
    }

    public IReadOnlyList<Transfer> Live
    {
        get { lock (_gate) return _live.Values.OrderBy(t => t.Created).ToArray(); }
    }

    private async Task OnChanged(Transfer t)
    {
        lock (_gate) _live[t.Id] = t;
        Updated?.Invoke(t);
        var api = _node.Api;
        if (api == null) return;

        try
        {
            if (!t.Sending && t.Status == TransferStatus.Incoming)
            {
                bool first;
                lock (_gate) first = _answered.Add(t.Id);
                if (!first) return;
                var s = _settings.Value;
                if (s.AcceptFromAllMyDevices || _settings.IsTrusted(t.PeerId))
                {
                    await api.Accept(t.Id).ConfigureAwait(false);
                    return;
                }
                NeedsAnswer?.Invoke(t);
            }
            else if (t.Finished)
            {
                await Finish(t, api).ConfigureAwait(false);
            }
        }
        catch { /* the next event tries again */ }
    }

    private async Task Finish(Transfer t, NodeApi api)
    {
        lock (_gate) { if (!_recorded.Add(t.Id)) return; }

        var item = new HistoryItem
        {
            Id = t.Id, At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Sent = t.Sending, Peer = t.PeerName,
            Name = t.Name, Size = t.Size, Mime = t.Mime, Result = t.Status,
            Note = t.Status == TransferStatus.Error ? t.Error : t.Note,
            Path = t.Sending ? t.Path : null,
        };

        if (!t.Sending && t.Status == TransferStatus.Done && !string.IsNullOrEmpty(t.Path) && File.Exists(t.Path))
        {
            // The node keeps a received file until the app takes it: move it where the person wants it, then let go.
            try
            {
                var dir = _settings.Value.ReceiveDirectory;
                Directory.CreateDirectory(dir);
                var dest = Paths.Unique(dir, SafeName(t.Name));
                File.Move(t.Path, dest);
                item.Path = dest;
                try { File.SetLastWriteTime(dest, DateTime.Now); } catch { /* cosmetic */ }
            }
            catch (Exception e)
            {
                item.Result = TransferStatus.Error;
                item.Note = e.Message;
            }
        }
        _history.Add(item);
        if (!t.Sending && item.Result == TransferStatus.Done && item.Path != null) Received?.Invoke(t, item.Path);

        try { await api.Dismiss(t.Id).ConfigureAwait(false); } catch { /* already gone */ }
        lock (_gate) { _live.Remove(t.Id); _answered.Remove(t.Id); }
        Updated?.Invoke(t);
    }

    private static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "file" : clean;
    }

    // --- what the person decides ---

    public async Task Accept(Transfer t, bool always)
    {
        if (always && !string.IsNullOrEmpty(t.PeerId)) _settings.Trust(t.PeerId, t.PeerName);
        if (_node.Api != null) await _node.Api.Accept(t.Id).ConfigureAwait(false);
    }

    public async Task Decline(Transfer t)
    {
        if (_node.Api != null) await _node.Api.Decline(t.Id).ConfigureAwait(false);
        lock (_gate) { _live.Remove(t.Id); }
        Updated?.Invoke(t);
    }

    public async Task Cancel(Transfer t)
    {
        if (_node.Api != null) await _node.Api.CancelTransfer(t.Id).ConfigureAwait(false);
    }

    public async Task SendFile(Device to, string path, CancellationToken ct = default)
    {
        var api = _node.Api ?? throw new InvalidOperationException("The node is not running");
        var info = new FileInfo(path);
        await api.Send(to.Id, to.Name, info.Name, MimeOf(info.Name), info.FullName, ct).ConfigureAwait(false);
    }

    public static string MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp",
        ".mp4" or ".m4v" => "video/mp4", ".mkv" => "video/x-matroska", ".mov" => "video/quicktime", ".webm" => "video/webm",
        ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".flac" => "audio/flac", ".m4a" => "audio/mp4", ".ogg" => "audio/ogg",
        ".pdf" => "application/pdf", ".txt" => "text/plain", ".zip" => "application/zip",
        _ => "application/octet-stream",
    };
}
