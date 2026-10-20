using System.Windows;
using JsyncDrive.Engine;

namespace JsyncDrive;

public partial class App : Application
{
    private const string MutexName = "JsyncDrive.SingleInstance";
    private const string ShowEventName = "JsyncDrive.ShowMain";
    private Mutex? _mutex;
    private EventWaitHandle? _show;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log("ui", ex.Exception.ToString());
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log("fatal", ex.ExceptionObject.ToString() ?? "");
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log("task", ex.Exception.ToString()); ex.SetObserved(); };

        _mutex = new Mutex(true, MutexName, out var first);
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            _show.Set(); // the running app comes to the front
            Shutdown();
            return;
        }
        var background = e.Args.Contains("--background");
        var host = new AppHost(Dispatcher);
        host.Start(background);
        _ = Task.Run(() =>
        {
            while (_show.WaitOne()) Dispatcher.BeginInvoke(() => host.ShowMain());
        });
    }

    public static void Log(string where, string text)
    {
        try
        {
            Directory.CreateDirectory(Paths.Logs);
            File.AppendAllText(Path.Combine(Paths.Logs, "app.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {text}{Environment.NewLine}");
        }
        catch { /* nowhere to report it */ }
    }
}
