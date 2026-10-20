using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JsyncDrive.Engine;

/// <summary>The local HTTP API of a running jsyncnode (loopback, bearer token).</summary>
public sealed class NodeApi : IDisposable
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly Uri _base;

    public NodeApi(int port, string token)
    {
        _base = new Uri($"http://127.0.0.1:{port}/");
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = _base,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void Dispose() => _http.Dispose();

    private static string Q(string s) => Uri.EscapeDataString(s);

    private async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new NodeException(0, "The jsync node did not answer in time"); }
        catch (HttpRequestException e) { throw new NodeException(0, "Could not reach the jsync node: " + e.Message); }
        if (res.IsSuccessStatusCode) return res;
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var code = (int)res.StatusCode;
        res.Dispose();
        try { text = JsonNode.Parse(text)?["error"]?.GetValue<string>() ?? text; } catch { /* not JSON */ }
        throw new NodeException(code, text);
    }

    private async Task<T> Get<T>(string url, CancellationToken ct)
    {
        using var res = await Send(new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false))!;
    }

    private async Task<JsonNode?> Post(string url, object? body, CancellationToken ct, TimeSpan? timeout = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await Send(req, ct, timeout).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private async Task<T> Post<T>(string url, object? body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await Send(req, ct).ConfigureAwait(false);
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false))!;
    }

    // --- node ---

    public Task<NodeStatus> Status(CancellationToken ct = default) => Get<NodeStatus>("v1/status", ct);

    public Task<LoginInfo> LoginStart(string server, string device, CancellationToken ct = default) =>
        Post<LoginInfo>("v1/login/start", new { server, device }, ct);

    public Task LoginCancel(CancellationToken ct = default) => Post("v1/login/cancel", null, ct);
    public Task Logout(CancellationToken ct = default) => Post("v1/logout", null, ct);

    /// <summary>Changes only the settings given.</summary>
    public Task<NodeStatus> SetSettings(string? hub = null, string? server = null, string? device = null, bool? readOnly = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>();
        if (hub != null) body["hub"] = hub;
        if (server != null) body["server"] = server;
        if (device != null) body["device"] = device;
        if (readOnly != null) body["read_only"] = readOnly.Value;
        return Post<NodeStatus>("v1/settings", body, ct);
    }

    // --- the Drive ---

    public async Task<ListResult> Ls(string path, CancellationToken ct = default) =>
        await Get<ListResult>("v1/ls?path=" + Q(path), ct).ConfigureAwait(false);

    public async Task<List<Entry>> Search(string query, int limit = 100, CancellationToken ct = default)
    {
        var r = await Get<ListResult>($"v1/search?q={Q(query)}&limit={limit}", ct).ConfigureAwait(false);
        return r.Entries ?? new();
    }

    /// <summary>Every file and folder under a folder, flat, with full Drive paths.</summary>
    public async Task<List<Entry>> Tree(string path, CancellationToken ct = default)
    {
        var r = await Get<ListResult>("v1/tree?path=" + Q(path), ct).ConfigureAwait(false);
        return r.Entries ?? new();
    }

    public Task Mkdir(string path, CancellationToken ct = default) => Post("v1/mkdir?path=" + Q(path), null, ct);
    public Task Remove(string path, CancellationToken ct = default) => Post("v1/rm?path=" + Q(path), null, ct);
    public Task Move(string from, string to, CancellationToken ct = default) => Post($"v1/mv?from={Q(from)}&to={Q(to)}", null, ct);

    /// <summary>The contents of a file (or the first <paramref name="max"/> bytes).</summary>
    public async Task<byte[]> ReadBytes(string path, long max = long.MaxValue, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "v1/file?path=" + Q(path));
        if (max != long.MaxValue) req.Headers.Range = new RangeHeaderValue(0, max - 1);
        using var res = await Send(req, ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        return await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task Download(string path, string destination, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "v1/file?path=" + Q(path));
        using var res = await Send(req, ct, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tmp = destination + ".part";
        try
        {
            await using (var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                var buf = new byte[1 << 17];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    total += n;
                    progress?.Report(total);
                }
            }
            File.Move(tmp, destination, true);
        }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    public async Task Upload(string local, string path, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var info = new FileInfo(local);
        await using var file = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true);
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        var req = new HttpRequestMessage(HttpMethod.Put, $"v1/file?path={Q(path)}&mtime={mtime}")
        {
            Content = new ProgressContent(file, progress),
        };
        using var res = await Send(req, ct, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
    }

    public Task<NodeStatus> ProtectedOpen(string folder, string password, int forSeconds = 0, CancellationToken ct = default) =>
        Post<NodeStatus>("v1/protected/open", new { folder, password, for_seconds = forSeconds }, ct);

    public Task<NodeStatus> ProtectedLock(string folder, CancellationToken ct = default) =>
        Post<NodeStatus>("v1/protected/lock", new { folder }, ct);

    /// <summary>Folder colours, pins and shares of the account (an allowlist on the node).</summary>
    public async Task<JsonNode?> DriveCall(string method, string path, object? body = null, CancellationToken ct = default)
    {
        var node = await Post("v1/drive", new { method, path, body }, ct).ConfigureAwait(false);
        return node;
    }

    public async Task<Dictionary<string, string>> FolderColors(CancellationToken ct = default)
    {
        var o = await DriveCall("GET", "/api/v1/folder-colors", null, ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (o is JsonObject jo) foreach (var kv in jo) if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s)) map[kv.Key] = s;
        return map;
    }

    /// <summary>An empty colour puts the folder back to the default.</summary>
    public Task SetFolderColor(string path, string color, CancellationToken ct = default) =>
        DriveCall("POST", "/api/v1/folder-colors", new { path, color }, ct);

    public async Task<HashSet<string>> Pins(CancellationToken ct = default)
    {
        var a = await DriveCall("GET", "/api/v1/pins", null, ct).ConfigureAwait(false);
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (a is JsonArray arr) foreach (var n in arr) if (n is JsonValue v && v.TryGetValue<string>(out var s)) set.Add(s);
        return set;
    }

    public Task SetPinned(string path, bool pinned, CancellationToken ct = default) =>
        DriveCall(pinned ? "POST" : "DELETE", "/api/v1/pins", new { path }, ct);

    public async Task<List<DriveShare>> Shares(CancellationToken ct = default)
    {
        var o = await DriveCall("GET", "/api/v1/shares", null, ct).ConfigureAwait(false);
        return o?["owned"]?.Deserialize<List<DriveShare>>(Json) ?? new();
    }

    public Task CreateShare(string path, string email, string permission, CancellationToken ct = default) =>
        DriveCall("POST", "/api/v1/shares", new { path, email, permission }, ct);

    public Task RemoveShare(string shareId, CancellationToken ct = default) =>
        DriveCall("DELETE", "/api/v1/shares", new { share_id = shareId }, ct);

    // --- other devices ---

    public async Task<List<Device>> Devices(CancellationToken ct = default) =>
        (await Get<DevicesResult>("v1/p2p/devices", ct).ConfigureAwait(false)).Devices ?? new();

    public async Task<List<Transfer>> Transfers(CancellationToken ct = default) =>
        (await Get<TransfersResult>("v1/p2p/transfers", ct).ConfigureAwait(false)).Transfers ?? new();

    public async Task<string> Send(string toDevice, string peerName, string name, string? mime, string path, CancellationToken ct = default)
    {
        var r = await Post("v1/p2p/send", new { to = toDevice, peer_name = peerName, name, mime = mime ?? "", path }, ct).ConfigureAwait(false);
        return r?["id"]?.GetValue<string>() ?? "";
    }

    public Task Accept(string id, CancellationToken ct = default) => Post("v1/p2p/accept", new { id }, ct);
    public Task Decline(string id, CancellationToken ct = default) => Post("v1/p2p/decline", new { id }, ct);
    public Task CancelTransfer(string id, CancellationToken ct = default) => Post("v1/p2p/cancel", new { id }, ct);
    public Task Dismiss(string id, CancellationToken ct = default) => Post("v1/p2p/dismiss", new { id }, ct);

    // --- events ---

    /// <summary>Server-sent events from sequence <paramref name="since"/> on; ends when the stream does.</summary>
    public async IAsyncEnumerable<NodeEvent> Events(long since, [EnumeratorCancellation] CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "v1/events?since=" + since);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) yield break;
            if (line.StartsWith("data:"))
            {
                data.Append(line.AsSpan(5).TrimStart());
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                NodeEvent? ev = null;
                try { ev = JsonSerializer.Deserialize<NodeEvent>(data.ToString(), Json); } catch { /* skip a bad frame */ }
                data.Clear();
                if (ev != null) yield return ev;
            }
        }
    }

    /// <summary>Request body that reports how much of the file went out.</summary>
    private sealed class ProgressContent : HttpContent
    {
        private readonly Stream _src;
        private readonly IProgress<long>? _progress;

        public ProgressContent(Stream src, IProgress<long>? progress)
        {
            _src = src;
            _progress = progress;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buf = new byte[1 << 17];
            long total = 0;
            int n;
            while ((n = await _src.ReadAsync(buf).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                total += n;
                _progress?.Report(total);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _src.Length;
            return true;
        }
    }
}
