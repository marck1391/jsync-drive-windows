using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JsyncDrive.Engine;

namespace JsyncDrive.Ui;

/// <summary>Small pictures of the images in the Drive: fetched through the node, shrunk, and kept on disk.</summary>
public sealed class Thumbs
{
    private readonly SemaphoreSlim _gate = new(3);
    private readonly Dictionary<string, ImageSource> _memory = new();

    private static string Key(string path, long size, long mtime, int px)
    {
        var h = SHA1.HashData(Encoding.UTF8.GetBytes($"{path}|{size}|{mtime}|{px}"));
        return Convert.ToHexString(h).ToLowerInvariant();
    }

    public ImageSource? Peek(EntryVm e, int px) =>
        _memory.GetValueOrDefault(Key(e.FullPath, e.E.Size, e.E.Mtime, px));

    public async Task<ImageSource?> Load(NodeApi api, EntryVm e, int px, CancellationToken ct)
    {
        var key = Key(e.FullPath, e.E.Size, e.E.Mtime, px);
        if (_memory.TryGetValue(key, out var hit)) return hit;
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (_memory.TryGetValue(key, out hit)) return hit;
            var file = Path.Combine(Paths.Thumbs, key + ".jpg");
            byte[]? bytes = null;
            var cached = File.Exists(file);
            try
            {
                bytes = cached ? await File.ReadAllBytesAsync(file, ct) : await api.ReadBytes(e.FullPath, 12_000_000, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
            var img = await Task.Run(() => Decode(bytes, px), ct).ConfigureAwait(true);
            if (img == null) return null;
            _memory[key] = img;
            if (!cached) _ = Task.Run(() => Save(img, file));
            return img;
        }
        finally { _gate.Release(); }
    }

    private static ImageSource? Decode(byte[] bytes, int px)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = px;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static void Save(ImageSource img, string file)
    {
        try
        {
            if (img is not BitmapSource bs) return;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(bs));
            using var fs = File.Create(file);
            enc.Save(fs);
        }
        catch { /* the cache is a convenience */ }
    }

    /// <summary>A picture already on disk (a file just received, say), shrunk.</summary>
    public static ImageSource? FromFile(string path, int px)
    {
        try { return Decode(File.ReadAllBytes(path), px); } catch { return null; }
    }
}
