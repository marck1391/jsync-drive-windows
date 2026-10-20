using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JsyncDrive.Engine;
using JsyncDrive.Ui;

namespace JsyncDrive;

/// <summary>
/// The window of one transfer that comes to this PC: it asks (Accept, Always, Decline) while a device waits for an
/// answer, shows progress while the file arrives, and once it is here shows the picture itself (or the icon of its
/// kind) with the things to do with it.
/// </summary>
public partial class TransferPopup : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _close = new();
    private string? _savedPath;

    public string TransferId { get; }

    /// <summary>The file is here and the window shows it: later reports about the transfer must not undo that.</summary>
    public bool Final { get; private set; }

    public TransferPopup(AppHost host, Transfer t)
    {
        _host = host;
        TransferId = t.Id;
        InitializeComponent();
        WinUtil.Chrome(this);
        _close.Tick += (_, _) => { _close.Stop(); if (!IsMouseOver) Close(); else _close.Start(); };
        MouseEnter += (_, _) => _close.Stop();
        MouseLeave += (_, _) => { if (_closeAfter > TimeSpan.Zero) { _close.Interval = _closeAfter; _close.Start(); } };
        Update(t);
    }

    private TimeSpan _closeAfter = TimeSpan.Zero;

    private Button Btn(string text, Action click, string? style = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), MinWidth = 78 };
        if (style != null) b.Style = (Style)FindResource(style);
        b.Click += (_, _) => click();
        return b;
    }

    private void SetKind(string name, string? mime)
    {
        var kind = FileKinds.Of(name, false);
        KindIcon.Data = (Geometry)FindResource(FileKinds.Icon(kind));
        KindIcon.Fill = FileKinds.BrushOf(FileKinds.Tint(kind));
    }

    private static string Who(Transfer t) => string.IsNullOrEmpty(t.PeerName) ? Loc.T("Another device") : t.PeerName;

    /// <summary>Follows the transfer as the node reports it.</summary>
    public void Update(Transfer t)
    {
        if (Final) return;
        FileName.Text = t.Name;
        SetKind(t.Name, t.Mime);
        Buttons.Children.Clear();
        Bar.Visibility = Message.Visibility = Visibility.Collapsed;
        _close.Stop();
        _closeAfter = TimeSpan.Zero;

        switch (t.Status)
        {
            case TransferStatus.Incoming:
                Heading.Text = Loc.T("{0} wants to send you a file", Who(t)).ToUpperInvariant();
                Detail.Text = Fmt.Size(t.Size);
                Buttons.Children.Add(Btn(Loc.T("Accept"), () => _ = Answer(() => _host.Transfers.Accept(t, false)), "PrimaryBtn"));
                Buttons.Children.Add(Btn(Loc.T("Always accept from {0}", Who(t)), () => _ = Answer(() => _host.Transfers.Accept(t, true))));
                Buttons.Children.Add(Btn(Loc.T("Decline"), () => _ = Answer(async () => { await _host.Transfers.Decline(t); Dispatcher.Invoke(Close); }), "DangerBtn"));
                break;

            case TransferStatus.Connecting:
            case TransferStatus.Waiting:
                Heading.Text = Loc.T("Receiving from {0}", Who(t)).ToUpperInvariant();
                Detail.Text = string.IsNullOrEmpty(t.Note) ? Loc.T("Connecting…") : t.Note;
                Bar.IsIndeterminate = true;
                Bar.Visibility = Visibility.Visible;
                Buttons.Children.Add(Btn(Loc.T("Cancel"), () => _ = _host.Transfers.Cancel(t)));
                break;

            case TransferStatus.Active:
                Heading.Text = (t.Sending ? Loc.T("Sending to {0}", Who(t)) : Loc.T("Receiving from {0}", Who(t))).ToUpperInvariant();
                Detail.Text = $"{Fmt.Size(t.Bytes)} / {Fmt.Size(t.Size)}" + (t.Speed > 0 ? $" · {Fmt.Size((long)t.Speed)}/s" : "");
                Bar.IsIndeterminate = false;
                Bar.Value = t.Fraction * 100;
                Bar.Visibility = Visibility.Visible;
                Buttons.Children.Add(Btn(Loc.T("Cancel"), () => _ = _host.Transfers.Cancel(t)));
                break;

            case TransferStatus.Done:
                Heading.Text = (t.Sending ? Loc.T("Sent to {0}", Who(t)) : Loc.T("Received from {0}", Who(t))).ToUpperInvariant();
                Detail.Text = Fmt.Size(t.Size) + (t.Sending ? "" : " · " + Loc.T("Saving…"));
                _closeAfter = TimeSpan.FromSeconds(t.Sending ? 4 : 30);
                break;

            default:
                Heading.Text = Who(t).ToUpperInvariant();
                Detail.Text = Fmt.Size(t.Size);
                Message.Text = t.Status switch
                {
                    TransferStatus.Declined => Loc.T("The other device declined."),
                    TransferStatus.Cancelled => string.IsNullOrEmpty(t.Note) ? Loc.T("Cancelled.") : t.Note,
                    _ => string.IsNullOrEmpty(t.Error) ? Loc.T("The transfer failed.") : t.Error,
                };
                Message.Foreground = Theme.Brush(t.Status == TransferStatus.Error ? "DangerBrush" : "MutedBrush");
                Message.Visibility = Visibility.Visible;
                Buttons.Children.Add(Btn(Loc.T("Close"), Close));
                _closeAfter = TimeSpan.FromSeconds(8);
                break;
        }
        if (_closeAfter > TimeSpan.Zero) { _close.Interval = _closeAfter; _close.Start(); }
    }

    /// <summary>The file is in the receive folder: show it, with the buttons to use it.</summary>
    public void ShowReceived(Transfer t, string path)
    {
        _savedPath = path;
        Final = true;
        FileName.Text = Path.GetFileName(path);
        SetKind(path, t.Mime);
        Heading.Text = Loc.T("Received from {0}", Who(t)).ToUpperInvariant();
        Detail.Text = Fmt.Size(new FileInfo(path).Length) + " · " + Loc.T("Saved in {0}", Path.GetDirectoryName(path)!);
        Bar.Visibility = Message.Visibility = Visibility.Collapsed;
        if (FileKinds.CanThumb(path, new FileInfo(path).Length) && Thumbs.FromFile(path, 900) is { } img)
        {
            Picture.Source = img;
            Picture.Visibility = Visibility.Visible;
            KindIcon.Visibility = Visibility.Collapsed;
        }
        Buttons.Children.Clear();
        Buttons.Children.Add(Btn(Loc.T("Open"), OpenFile, "PrimaryBtn"));
        Buttons.Children.Add(Btn(Loc.T("Show in folder"), ShowFolder));
        Buttons.Children.Add(Btn(Loc.T("Delete"), DeleteFile, "DangerBtn"));
        _closeAfter = TimeSpan.FromSeconds(30);
        _close.Stop();
        _close.Interval = _closeAfter;
        _close.Start();
    }

    private async Task Answer(Func<Task> act)
    {
        try { await act(); }
        catch (NodeException e) { Message.Text = e.Message; Message.Foreground = Theme.Brush("DangerBrush"); Message.Visibility = Visibility.Visible; }
    }

    private void OpenFile()
    {
        try { Process.Start(new ProcessStartInfo(_savedPath!) { UseShellExecute = true }); } catch { /* no handler */ }
        Close();
    }

    private void ShowFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_savedPath}\"") { UseShellExecute = true }); } catch { /* explorer gone */ }
        Close();
    }

    private void DeleteFile()
    {
        try { if (_savedPath != null) File.Delete(_savedPath); } catch (Exception e) { Message.Text = e.Message; Message.Visibility = Visibility.Visible; return; }
        _host.History.Remove(TransferId);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
