using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using JsyncDrive.Engine;

namespace JsyncDrive.Ui;

public static class WinUtil
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Makes the title bar follow the app's light or dark theme.</summary>
    public static void Chrome(Window w)
    {
        void Apply()
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            var dark = Theme.IsDark ? 1 : 0;
            DwmSetWindowAttribute(h, 20, ref dark, sizeof(int));
        }
        w.SourceInitialized += (_, _) => Apply();
        Action changed = () => w.Dispatcher.BeginInvoke(Apply);
        Theme.Changed += changed;
        w.Closed += (_, _) => Theme.Changed -= changed;
    }

    public static Style S(string key) => (Style)Application.Current.FindResource(key);

    public static Path Glyph(string key, double size = 18, Brush? fill = null)
    {
        var p = new Path { Data = (Geometry)Application.Current.FindResource(key), Width = size, Height = size, Stretch = Stretch.Uniform };
        if (fill != null) p.Fill = fill; else p.SetResourceReference(Shape.FillProperty, "MutedBrush");
        return p;
    }
}

/// <summary>Small modal windows built in code: ask a line of text, confirm, pick a colour, share a folder…</summary>
public static class Dialogs
{
    private static Window Make(Window? owner, string title, double width = 440)
    {
        var w = new Window
        {
            Title = title, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner == null, Style = WinUtil.S("AppWindow"),
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (owner is { IsVisible: true }) w.Owner = owner;
        WinUtil.Chrome(w);
        return w;
    }

    private static StackPanel Body(Window w)
    {
        var sp = new StackPanel { Margin = new Thickness(24) };
        w.Content = sp;
        return sp;
    }

    private static TextBlock Text(string s, string style = "Muted") => new()
    {
        Text = s, Style = WinUtil.S(style), Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap,
    };

    private static StackPanel Buttons(Window w, string ok, Func<bool> onOk, bool danger = false, string? cancel = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var c = new Button { Content = cancel ?? Loc.T("Cancel"), Margin = new Thickness(0, 0, 8, 0), IsCancel = true, MinWidth = 84 };
        var b = new Button { Content = ok, Style = WinUtil.S(danger ? "DangerBtn" : "PrimaryBtn"), IsDefault = true, MinWidth = 84 };
        b.Click += (_, _) => { if (onOk()) { w.DialogResult = true; } };
        row.Children.Add(c);
        row.Children.Add(b);
        return row;
    }

    public static string? Prompt(Window? owner, string title, string message, string initial = "", string ok = "OK", string? hint = null)
    {
        var w = Make(owner, title);
        var sp = Body(w);
        if (message.Length > 0) sp.Children.Add(Text(message));
        var box = new TextBox { Text = initial };
        if (hint != null) Hint.SetText(box, hint);
        sp.Children.Add(box);
        sp.Children.Add(Buttons(w, ok, () => box.Text.Trim().Length > 0));
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text.Trim() : null;
    }

    public static string? Password(Window? owner, string title, string message, string ok = "Unlock")
    {
        var w = Make(owner, title);
        var sp = Body(w);
        sp.Children.Add(Text(message));
        var box = new PasswordBox();
        sp.Children.Add(box);
        sp.Children.Add(Buttons(w, Loc.T(ok), () => box.Password.Length > 0));
        w.Loaded += (_, _) => box.Focus();
        return w.ShowDialog() == true ? box.Password : null;
    }

    public static bool Confirm(Window? owner, string title, string message, string ok, bool danger = false)
    {
        var w = Make(owner, title);
        var sp = Body(w);
        sp.Children.Add(Text(message));
        sp.Children.Add(Buttons(w, ok, () => true, danger));
        return w.ShowDialog() == true;
    }

    public static void Info(Window? owner, string title, string message)
    {
        var w = Make(owner, title);
        var sp = Body(w);
        sp.Children.Add(Text(message));
        var row = Buttons(w, "OK", () => true);
        row.Children.RemoveAt(0);
        sp.Children.Add(row);
        w.ShowDialog();
    }

    public static readonly (string hex, string name)[] Palette =
    {
        ("#4285F4", "Blue"), ("#34A853", "Green"), ("#FBBC04", "Yellow"), ("#EA4335", "Red"), ("#A142F4", "Purple"),
        ("#FF6D01", "Orange"), ("#F439A0", "Pink"), ("#24C1E0", "Cyan"), ("#9AA0A6", "Grey"),
    };

    /// <summary>The colour for a folder: a hex from the palette, "" for the default, null when cancelled.</summary>
    public static string? ChooseColor(Window? owner, string current)
    {
        var w = Make(owner, Loc.T("Folder colour"), 400);
        var sp = Body(w);
        sp.Children.Add(Text(Loc.T("Pick a colour for this folder. It shows on every device.")));
        string? result = null;
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        void Add(string hex, string name)
        {
            var dot = new Border
            {
                Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Margin = new Thickness(0, 0, 10, 10), Cursor = Cursors.Hand, ToolTip = Loc.T(name),
                Background = hex.Length > 0 ? (Brush)new BrushConverter().ConvertFromString(hex)! : (Brush)Application.Current.FindResource("SurfaceAltBrush"),
                BorderThickness = new Thickness(hex.Equals(current, StringComparison.OrdinalIgnoreCase) ? 3 : 1),
            };
            dot.SetResourceReference(Border.BorderBrushProperty, hex.Equals(current, StringComparison.OrdinalIgnoreCase) ? "TextBrush" : "BorderBrush");
            if (hex.Length == 0) dot.Child = WinUtil.Glyph("IconClose", 18);
            dot.MouseLeftButtonUp += (_, _) => { result = hex; w.DialogResult = true; };
            wrap.Children.Add(dot);
        }
        Add("", "Default");
        foreach (var (hex, name) in Palette) Add(hex, name);
        sp.Children.Add(wrap);
        var close = new Button { Content = Loc.T("Cancel"), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true, MinWidth = 84 };
        sp.Children.Add(close);
        return w.ShowDialog() == true ? result : null;
    }

    /// <summary>Which of the account's other devices to send to; null when cancelled.</summary>
    public static Device? PickDevice(Window? owner, IReadOnlyList<Device> devices, string what)
    {
        var w = Make(owner, Loc.T("Send to…"), 420);
        var sp = Body(w);
        sp.Children.Add(Text(what, "Muted"));
        Device? chosen = null;
        if (devices.Count == 0) sp.Children.Add(Text(Loc.T("No other device of your account is online.")));
        foreach (var d in devices)
        {
            var row = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(14, 10, 14, 10), Style = WinUtil.S("Btn") };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 12, 0), Fill = (Brush)Application.Current.FindResource(d.Online ? "SuccessBrush" : "MutedBrush") };
            g.Children.Add(dot);
            var name = new TextBlock { Text = d.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            g.Children.Add(name);
            row.Content = g;
            var dev = d;
            row.Click += (_, _) => { chosen = dev; w.DialogResult = true; };
            sp.Children.Add(row);
        }
        sp.Children.Add(new Button { Content = Loc.T("Cancel"), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true, MinWidth = 84, Margin = new Thickness(0, 8, 0, 0) });
        return w.ShowDialog() == true ? chosen : null;
    }

    /// <summary>Share a folder with another person (read or write), or stop sharing it.</summary>
    public static Task ShareFolder(Window? owner, NodeApi api, string path)
    {
        var w = Make(owner, Loc.T("Share folder"), 480);
        var sp = Body(w);
        sp.Children.Add(Text(Loc.T("Share “{0}” with someone who has a jsync Drive account.", System.IO.Path.GetFileName(path))));
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        sp.Children.Add(list);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var email = new TextBox { Margin = new Thickness(0, 0, 8, 0) };
        Hint.SetText(email, Loc.T("Email"));
        var perm = new ComboBox { Margin = new Thickness(0, 0, 8, 0), MinWidth = 96, SelectedIndex = 0 };
        perm.Items.Add(new ComboBoxItem { Content = Loc.T("Can view"), Tag = "read" });
        perm.Items.Add(new ComboBoxItem { Content = Loc.T("Can edit"), Tag = "write" });
        var add = new Button { Content = Loc.T("Share"), Style = WinUtil.S("PrimaryBtn") };
        var err = new TextBlock { Foreground = (Brush)Application.Current.FindResource("DangerBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetColumn(perm, 1); Grid.SetColumn(add, 2);
        row.Children.Add(email); row.Children.Add(perm); row.Children.Add(add);
        sp.Children.Add(row);
        sp.Children.Add(err);
        var close = new Button { Content = Loc.T("Done"), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true, Margin = new Thickness(0, 12, 0, 0), MinWidth = 84 };
        sp.Children.Add(close);

        async Task Reload()
        {
            list.Children.Clear();
            List<DriveShare> shares;
            try { shares = (await api.Shares()).Where(s => s.Path == path).ToList(); }
            catch (Exception e) { err.Text = e.Message; return; }
            if (shares.Count == 0) list.Children.Add(Text(Loc.T("Not shared with anyone yet."), "Caption"));
            foreach (var s in shares)
            {
                var r = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                r.ColumnDefinitions.Add(new ColumnDefinition());
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                r.Children.Add(new TextBlock { Text = $"{s.SharedWith}  ·  {(s.Permission == "write" ? Loc.T("Can edit") : Loc.T("Can view"))}", VerticalAlignment = VerticalAlignment.Center });
                var stop = new Button { Content = Loc.T("Stop sharing"), Style = WinUtil.S("TextBtn") };
                Grid.SetColumn(stop, 1);
                var id = s.Id;
                stop.Click += async (_, _) =>
                {
                    try { await api.RemoveShare(id); await Reload(); } catch (Exception e) { err.Text = e.Message; }
                };
                r.Children.Add(stop);
                list.Children.Add(r);
            }
        }
        add.Click += async (_, _) =>
        {
            err.Text = "";
            var to = email.Text.Trim();
            if (!to.Contains('@')) { err.Text = Loc.T("Enter the person's email."); return; }
            try
            {
                await api.CreateShare(path, to, (string)((ComboBoxItem)perm.SelectedItem).Tag);
                email.Text = "";
                await Reload();
            }
            catch (Exception e) { err.Text = e.Message; }
        };
        w.Loaded += async (_, _) => await Reload();
        w.ShowDialog();
        return Task.CompletedTask;
    }
}
