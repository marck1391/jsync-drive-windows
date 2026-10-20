using System.Diagnostics;
using JsyncDrive.Engine;

// Drives the real engine through the layer the app uses, everything under JSYNC_APP_HOME:
//   dotnet run --project tests\EngineSmoke -- <hub> <server> [approve.py]
// With a script as third argument (called with the login code) it signs in by itself against a local test hub.
Paths.EnsureAll();
Console.WriteLine("home " + Paths.Root + " args " + string.Join(" | ", args));
var failures = 0;
void Check(string what, bool ok, string extra = "") { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what} {extra}"); if (!ok) failures++; }

var settings = SettingsStore.Load();
settings.Update(s => { s.HubAddress = args[0]; s.ServerUrl = args[1]; s.DeviceName = "smoke-" + Environment.MachineName; s.Mode = "client"; });
using var node = new NodeHost();
var ready = new TaskCompletionSource();
node.Ready += () => ready.TrySetResult();
node.Start();
await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
Check("node up", node.Api != null, node.Status.MachineId);
await node.Apply(settings.Value);
Check("settings applied", node.Status.Settings.Hub == args[0] && node.Status.Settings.Server == args[1]);

if (node.Status.State == NodeStates.LoggedOut || node.Status.State == NodeStates.NeedsSetup)
{
    var li = await node.Api!.LoginStart(args[1], settings.Value.DeviceName);
    Console.WriteLine($"login code {li.UserCode} {li.Uri}");
    if (args.Length > 2)
    {
        var p = Process.Start(new ProcessStartInfo("python", new[] { "-I", args[2], li.UserCode }) { UseShellExecute = false, RedirectStandardOutput = true })!;
        Console.WriteLine("approve -> " + (await p.StandardOutput.ReadToEndAsync()).Trim());
    }
}
for (var i = 0; i < 60 && !node.Status.Online; i++) { await Task.Delay(500); await node.Refresh(); }
Check("online", node.Status.Online, $"{node.Status.State} {node.Status.Detail} as {node.Status.Email}");
if (node.Status.Online)
{
    var api = node.Api!;
    var dir = "smoke-" + DateTime.Now.ToString("HHmmss");
    await api.Mkdir(dir);
    var src = Path.Combine(Paths.Root, "payload.bin");
    var bytes = new byte[3_000_000]; new Random(7).NextBytes(bytes);
    File.WriteAllBytes(src, bytes);
    long last = 0;
    await api.Upload(src, dir + "/payload.bin", new Progress<long>(b => last = b));
    Check("upload", last == bytes.Length, $"{last} bytes");
    var ls = await api.Ls(dir);
    Check("ls", ls.Entries?.Count == 1 && ls.Entries[0].Size == bytes.Length, ls.Entries?.FirstOrDefault()?.Name);
    var dst = Path.Combine(Paths.Root, "back.bin");
    await api.Download(dir + "/payload.bin", dst);
    Check("download identical", File.ReadAllBytes(dst).AsSpan().SequenceEqual(bytes));
    await api.Move(dir + "/payload.bin", dir + "/renamed.bin");
    Check("rename", (await api.Ls(dir)).Entries?.Any(e => e.Name == "renamed.bin") == true);
    // the hub applies what the node wrote a moment later
    for (var i = 0; i < 20; i++) { try { await api.SetFolderColor(dir, "#34A853"); break; } catch (NodeException) when (i < 19) { await Task.Delay(500); } }
    Check("folder colour", (await api.FolderColors()).GetValueOrDefault(dir) == "#34A853");
    await api.SetPinned(dir, true);
    Check("pin", (await api.Pins()).Contains(dir));
    var found = await api.Search("renamed");
    Check("search", found.Any(e => e.Path?.EndsWith("renamed.bin") == true), found.FirstOrDefault()?.Path);
    var devices = await api.Devices();
    Console.WriteLine($"devices: {string.Join(", ", devices.Select(d => d.Name + (d.Online ? "*" : "")))}");
    await api.SetPinned(dir, false);
    await api.SetFolderColor(dir, "");
    await api.Remove(dir);
    Check("remove", !((await api.Ls("")).Entries?.Any(e => e.Name == dir) ?? false));
}
await node.StopAsync();
Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures;
