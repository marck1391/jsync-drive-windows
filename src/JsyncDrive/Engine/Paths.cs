namespace JsyncDrive.Engine;

/// <summary>Where the app keeps things, and where the jsync engine binaries are.</summary>
public static class Paths
{
    /// <summary>Overridable for tests: everything the app writes lives under this folder.</summary>
    public static string Root { get; set; } = Environment.GetEnvironmentVariable("JSYNC_APP_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "jsync Drive");

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string History => Path.Combine(Root, "history.json");
    public static string NodeData => Path.Combine(Root, "node");
    public static string NodeCache => Path.Combine(Root, "cache", "node");
    public static string MountCache => Path.Combine(Root, "cache", "mount");
    public static string Thumbs => Path.Combine(Root, "cache", "thumbs");
    public static string Opened => Path.Combine(Root, "cache", "open");
    /// <summary>The hub's config and data. A Windows service runs as SYSTEM, so it lives in ProgramData, not in the user's profile.</summary>
    public static string HubData => Environment.GetEnvironmentVariable("JSYNC_APP_HOME") != null
        ? Path.Combine(Root, "hub")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "jsync Drive", "hub");
    public static string HubConfig => Path.Combine(HubData, "jsync.db");
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>Folder holding jsyncnode.exe, jsync.exe and jsyncd.exe: next to the app, or $JSYNC_ENGINE.</summary>
    public static string Engine
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("JSYNC_ENGINE");
            if (!string.IsNullOrEmpty(env)) return env;
            var beside = Path.Combine(AppContext.BaseDirectory, "engine");
            if (Directory.Exists(beside)) return beside;
            // Running from the build output: walk up to the repository's engine\ folder.
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var c = Path.Combine(d.FullName, "engine");
                if (File.Exists(Path.Combine(c, "jsyncnode.exe"))) return c;
            }
            return beside;
        }
    }

    public static string Exe(string name) => Path.Combine(Engine, name + ".exe");

    public static void EnsureAll()
    {
        foreach (var d in new[] { Root, NodeData, NodeCache, MountCache, Thumbs, Opened, HubData, Logs })
            Directory.CreateDirectory(d);
    }

    public static string DefaultMountDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "jsync Drive");

    public static string DefaultReceiveDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "jsync");

    public static string DefaultHubStorage =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "jsync Drive", "storage");

    /// <summary>A name that does not exist yet in <paramref name="dir"/>: "a.txt", "a (1).txt", "a (2).txt"…</summary>
    public static string Unique(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
    }
}
