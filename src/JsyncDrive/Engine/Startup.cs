using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace JsyncDrive.Engine;

/// <summary>
/// Starts the app when the person signs in, and starts it again if it dies: a Task Scheduler task for the current
/// user (no administrator needed). The Run registry key is the fallback where tasks are not allowed.
/// </summary>
public static class Startup
{
    private const string TaskName = "jsync Drive";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static async Task<bool> IsEnabled()
    {
        var (ok, _) = await Run.Capture("schtasks.exe", new[] { "/Query", "/TN", TaskName }, null, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (ok) return true;
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(TaskName) != null;
    }

    public static async Task<bool> Enable(string exePath)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Keeps jsync Drive running: your Drive folder and transfers between your devices.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Esc(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>6</Priority>
                <RestartOnFailure><Interval>PT1M</Interval><Count>99</Count></RestartOnFailure>
              </Settings>
              <Actions Context="Author"><Exec><Command>{Esc(exePath)}</Command><Arguments>--background</Arguments></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Paths.Root, "startup-task.xml");
        Directory.CreateDirectory(Paths.Root);
        await File.WriteAllTextAsync(file, xml, Encoding.Unicode).ConfigureAwait(false);
        var (ok, _) = await Run.Capture("schtasks.exe", new[] { "/Create", "/TN", TaskName, "/XML", file, "/F" }, null, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        try { File.Delete(file); } catch { /* leftover is harmless */ }
        if (ok) { RemoveRunKey(); return true; }
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.SetValue(TaskName, $"\"{exePath}\" --background");
        return true;
    }

    public static async Task Disable()
    {
        await Run.Capture("schtasks.exe", new[] { "/Delete", "/TN", TaskName, "/F" }, null, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        RemoveRunKey();
    }

    private static void RemoveRunKey()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey, true); k?.DeleteValue(TaskName, false); } catch { /* none */ }
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;
}
