using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JsyncDrive.Engine;

public enum HostState { Stopped, Starting, Running, Failed }

/// <summary>
/// Keeps one engine process alive: starts it hidden, logs what it prints, tells when it is up, restarts it when it
/// dies (with a growing pause), and takes it down with the app.
/// </summary>
public sealed class ChildProcessHost : IDisposable
{
    private readonly object _gate = new();
    private readonly string _name;
    private readonly Func<ProcessStartInfo> _start;
    private readonly Func<string, bool>? _upWhen;
    private readonly string? _logFile;
    private readonly Queue<string> _tail = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Process? _proc;

    public HostState State { get; private set; } = HostState.Stopped;
    public string? Detail { get; private set; }
    public bool AutoRestart { get; set; } = true;
    public event Action? Changed;
    /// <summary>Every line the process prints (stdout and stderr).</summary>
    public event Action<string>? Line;

    public ChildProcessHost(string name, Func<ProcessStartInfo> start, Func<string, bool>? upWhen = null, string? logFile = null)
    {
        _name = name;
        _start = start;
        _upWhen = upWhen;
        _logFile = logFile;
    }

    public string[] Tail()
    {
        lock (_tail) return _tail.ToArray();
    }

    private void Set(HostState s, string? detail = null)
    {
        lock (_gate)
        {
            if (State == s && Detail == detail) return;
            State = s;
            Detail = detail;
        }
        Changed?.Invoke();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => Run(ct));
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _cts?.Cancel();
            loop = _loop;
        }
        KillCurrent();
        if (loop != null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); } catch { /* timed out or cancelled */ }
        }
        Set(HostState.Stopped);
    }

    public void Restart()
    {
        // Killing the process makes the loop start it again, with a fresh command line.
        KillCurrent();
    }

    private void KillCurrent()
    {
        Process? p;
        lock (_gate) p = _proc;
        try { if (p is { HasExited: false }) p.Kill(true); } catch { /* already gone */ }
    }

    private async Task Run(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            Set(HostState.Starting);
            var began = DateTime.UtcNow;
            string? failure = null;
            try
            {
                var psi = _start();
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = true;
                psi.StandardOutputEncoding = psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
                using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.Start();
                ProcessJob.Add(p);
                lock (_gate) _proc = p;
                var up = false;
                void OnLine(string? l)
                {
                    if (l == null) return;
                    Log(l);
                    Line?.Invoke(l);
                    if (!up && (_upWhen == null || _upWhen(l)))
                    {
                        up = true;
                        Set(HostState.Running);
                    }
                }
                p.OutputDataReceived += (_, e) => OnLine(e.Data);
                p.ErrorDataReceived += (_, e) => OnLine(e.Data);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (_upWhen == null) Set(HostState.Running);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                failure = $"exited with code {p.ExitCode}";
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { failure = e.Message; }
            finally { lock (_gate) _proc = null; }

            if (ct.IsCancellationRequested) break;
            var lived = DateTime.UtcNow - began;
            if (lived > TimeSpan.FromSeconds(30)) backoff = TimeSpan.FromSeconds(1);
            var last = Tail().LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
            Log($"[{_name}] {failure}; " + (AutoRestart ? $"restarting in {backoff.TotalSeconds:0}s" : "not restarting"));
            Set(HostState.Failed, last is { Length: > 0 } ? last : failure);
            if (!AutoRestart) break;
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            if (backoff < TimeSpan.FromSeconds(30)) backoff += backoff;
        }
        Set(HostState.Stopped);
    }

    private void Log(string line)
    {
        lock (_tail)
        {
            _tail.Enqueue(line);
            while (_tail.Count > 300) _tail.Dequeue();
        }
        if (_logFile == null) return;
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
                if (File.Exists(_logFile) && new FileInfo(_logFile).Length > 2_000_000)
                    File.Move(_logFile, _logFile + ".old", true);
                File.AppendAllText(_logFile, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { /* the log is a convenience */ }
    }

    public void Dispose() => KillCurrent();
}

/// <summary>A Windows job that kills what it holds when the app dies, so no engine process outlives it.</summary>
internal static class ProcessJob
{
    private static readonly IntPtr Job = Create();

    private static IntPtr Create()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(job, 9, ptr, (uint)size); // JobObjectExtendedLimitInformation
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job;
        }
        catch { return IntPtr.Zero; }
    }

    public static void Add(Process p)
    {
        if (Job == IntPtr.Zero) return;
        try { AssignProcessToJobObject(Job, p.Handle); } catch { /* already in a job: fine */ }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
}
