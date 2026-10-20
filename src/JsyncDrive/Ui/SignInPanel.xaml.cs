using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using JsyncDrive.Engine;
using JsyncDrive.Ui;

namespace JsyncDrive;

/// <summary>
/// Signing this PC in to the Drive: the node asks for a code, the person approves it in the Drive web app, and the node
/// comes online. Nothing secret passes through the app.
/// </summary>
public partial class SignInPanel : UserControl
{
    private AppHost? _host;
    private CancellationTokenSource? _cts;
    private string _uri = "";

    /// <summary>Raised on the UI thread when the node is signed in and online (or connecting with a login).</summary>
    public event Action? SignedIn;

    public SignInPanel()
    {
        InitializeComponent();
        Intro.Text = Loc.T("Approve this PC once from your jsync Drive web app. Open the page below, check that the code matches, and press Approve.");
        WaitText.Text = Loc.T("Waiting for approval…");
    }

    public void Begin(AppHost host)
    {
        _host = host;
        _ = Run();
    }

    public void Stop() => _cts?.Cancel();

    private static string Friendly(string text) =>
        text.Contains("not configured on this node", StringComparison.OrdinalIgnoreCase)
            ? Loc.T("That hub is not ready for sign-in yet: it needs its account service address and secret (Server page on the hub PC).")
            : text;

    private void Fail(string text)
    {
        text = Friendly(text);
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
        RetryBtn.Visibility = Visibility.Visible;
        WaitRow.Visibility = Visibility.Collapsed;
    }

    private async Task Run()
    {
        var host = _host!;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        ErrorText.Visibility = RetryBtn.Visibility = Visibility.Collapsed;
        WaitRow.Visibility = Visibility.Visible;
        CodeText.Text = "— — — —";
        OpenBtn.Visibility = ManualText.Visibility = Visibility.Collapsed;
        try
        {
            // The node must be up, and know where the Drive is.
            for (var i = 0; i < 60 && host.Node.Api == null; i++) await Task.Delay(250, cts.Token);
            if (host.Node.Api == null) { Fail(host.Node.Failure ?? host.Node.ProcessDetail ?? Loc.T("The jsync engine did not start.")); return; }
            await host.Node.Apply(host.S);
            var info = await host.Node.Api.LoginStart(host.S.ServerUrl, host.S.DeviceName, cts.Token);
            CodeText.Text = info.UserCode;
            _uri = info.Uri;
            if (!string.IsNullOrEmpty(_uri)) OpenBtn.Visibility = Visibility.Visible;
            else
            {
                ManualText.Text = Loc.T("Open your Drive web app, go to Devices, and enter this code.");
                ManualText.Visibility = Visibility.Visible;
            }
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(1000, cts.Token);
                await host.Node.Refresh();
                var st = host.Node.Status;
                if (st.Login is { Error: { Length: > 0 } err }) { Fail(err); return; }
                if (st.State is NodeStates.Online or NodeStates.Connecting or NodeStates.Offline && !string.IsNullOrEmpty(st.Email))
                {
                    SignedIn?.Invoke();
                    return;
                }
                if (st.State == NodeStates.LoggedOut && st.Login == null && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > info.ExpiresAt)
                {
                    Fail(Loc.T("The code expired."));
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (NodeException e) { Fail(e.Message); }
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_uri) { UseShellExecute = true }); } catch { /* no browser */ }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(CodeText.Text); } catch { /* clipboard busy */ }
    }

    private void OnRetry(object sender, RoutedEventArgs e) => _ = Run();
}
