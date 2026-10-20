using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JsyncDrive.Engine;
using JsyncDrive.Ui;
using Microsoft.Win32;
using Glyph = System.Windows.Shapes.Path;

namespace JsyncDrive;

public sealed class EntryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Folder { get; set; }
    public DataTemplate? File { get; set; }
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item is EntryVm { IsDir: true } ? Folder : File;
}

/// <summary>An upload or download in progress, as the bar at the bottom shows it.</summary>
public sealed class TransferVm : INotifyPropertyChanged
{
    private double _percent;
    private string _status = "";
    private bool _running = true;
    public string Title { get; set; } = "";
    public Geometry Icon { get; set; } = (Geometry)Application.Current.FindResource("IconUpload");
    public double Percent { get => _percent; set { _percent = value; Raise(); } }
    public string Status { get => _status; set { _status = value; Raise(); } }
    public bool Running { get => _running; set { _running = value; Raise(); } }
    public CancellationTokenSource Cts { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class FilesPage : UserControl, IPage
{
    private readonly AppHost _host;
    private readonly ObservableCollection<EntryVm> _items = new();
    private readonly ObservableCollection<TransferVm> _transfers = new();
    private readonly Thumbs _thumbs = new();
    private readonly DispatcherTimer _refreshSoon = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private Dictionary<string, string> _colors = new();
    private HashSet<string> _pins = new();
    private CancellationTokenSource? _load;
    private string _path = "";
    private string? _query;
    private bool _loading;
    private string? _error;
    private bool _wired;
    private bool _menuFromButton;

    private NodeApi? Api => _host.Node.Api;
    private Window? Owner => Window.GetWindow(this);

    public FilesPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_items);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(EntryVm.Section)));
        TileList.ItemsSource = view;
        RowList.ItemsSource = view;
        TransferList.ItemsSource = _transfers;
        _transfers.CollectionChanged += (_, _) => TransfersBar.Visibility = _transfers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _refreshSoon.Tick += (_, _) => { _refreshSoon.Stop(); _ = Reload(keepSelection: true); };
        SetView(host.S.GridView);
    }

    public void OnShown()
    {
        if (!_wired)
        {
            _wired = true;
            _host.Node.StatusChanged += () => Dispatcher.BeginInvoke(OnNodeStatus);
            _host.Node.EventReceived += OnNodeEvent;
        }
        _ = Reload();
    }

    // --- node ---

    private void OnNodeStatus()
    {
        if (_host.Node.Status.Online && _items.Count == 0 && !_loading) _ = Reload();
        else UpdateState();
    }

    private void OnNodeEvent(NodeEvent ev)
    {
        if (ev.Kind != "change" && ev.Kind != "protected") return;
        var p = (ev.Path ?? "").Trim('/');
        var parent = p.Contains('/') ? p[..p.LastIndexOf('/')] : "";
        if (ev.Kind == "protected" || parent == _path || p == _path || (ev.Old ?? "").StartsWith(_path))
            Dispatcher.BeginInvoke(() => { _refreshSoon.Stop(); _refreshSoon.Start(); });
    }

    // --- loading ---

    private async Task Reload(bool keepSelection = false)
    {
        var api = Api;
        _load?.Cancel();
        if (api == null || !_host.Node.Status.Online) { UpdateState(); return; }
        var cts = _load = new CancellationTokenSource();
        var selected = keepSelection ? GetSelected().Select(s => s.FullPath).ToHashSet() : new HashSet<string>();
        _loading = _items.Count == 0;
        _error = null;
        UpdateState();
        try
        {
            List<Entry> entries;
            var query = _query;
            if (query != null) entries = await api.Search(query, 200, cts.Token);
            else entries = (await api.Ls(_path, cts.Token)).Entries ?? new();
            var colors = _colors;
            var pins = _pins;
            try { colors = await api.FolderColors(cts.Token); } catch (NodeException) { /* colours are decoration */ }
            try { pins = await api.Pins(cts.Token); } catch (NodeException) { }
            if (cts.IsCancellationRequested) return;
            _colors = colors;
            _pins = pins;

            var vms = entries
                .OrderByDescending(e => e.Dir)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => new EntryVm(e, _path))
                .ToList();
            foreach (var v in vms)
            {
                if (v.IsDir)
                {
                    v.ColorHex = _colors.GetValueOrDefault(v.FullPath);
                    v.Pinned = _pins.Contains(v.FullPath);
                }
                if (query != null) v.Context = v.FullPath.Contains('/') ? v.FullPath[..v.FullPath.LastIndexOf('/')] : Loc.T("My Drive");
            }
            _items.Clear();
            foreach (var v in vms) _items.Add(v);
            _loading = false;
            if (keepSelection)
            {
                var list = ActiveList;
                foreach (var v in vms.Where(v => selected.Contains(v.FullPath))) list.SelectedItems.Add(v);
            }
            BuildCrumbs();
            BuildPinned();
            UpdateState();
            foreach (var v in vms.Where(v => v.CanThumb)) _ = LoadThumb(v, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (NodeException e) when (e.Locked)
        {
            _loading = false;
            _items.Clear();
            _error = Loc.T("This folder is locked.");
            BuildCrumbs();
            UpdateState();
        }
        catch (NodeException e)
        {
            _loading = false;
            _error = e.Message;
            UpdateState();
        }
    }

    private async Task LoadThumb(EntryVm v, CancellationToken ct)
    {
        var api = Api;
        if (api == null) return;
        try { v.Thumb = await _thumbs.Load(api, v, 360, ct); } catch (OperationCanceledException) { } catch { /* no thumbnail */ }
    }

    private void UpdateState()
    {
        var st = _host.Node.Status;
        string? title = null, text = null, action = null;
        var icon = "IconCloud";
        if (!_host.S.IsClient) { title = Loc.T("This PC is a hub"); text = Loc.T("Turn on “Also use this PC as a client” in Settings to browse your Drive here."); }
        else if (!st.Online)
        {
            switch (st.State)
            {
                case NodeStates.LoggedOut: title = Loc.T("Sign in to see your Drive"); text = Loc.T("This PC needs to be approved once from your jsync Drive web app."); action = Loc.T("Open Settings"); break;
                case NodeStates.NeedsSetup: title = Loc.T("Finish setting up"); text = Loc.T("Add the hub and Drive address in Settings."); action = Loc.T("Open Settings"); break;
                case NodeStates.Pending: title = Loc.T("Waiting for approval"); text = Loc.T("Approve this PC in your jsync Drive web app."); action = Loc.T("Open Settings"); break;
                case NodeStates.Connecting: title = Loc.T("Connecting to your Drive…"); break;
                default: title = Loc.T("Can't reach your Drive"); text = st.Detail; action = Loc.T("Try again"); icon = "IconWarning"; break;
            }
        }
        else if (_error != null) { title = Loc.T("Could not open this folder"); text = _error; action = Loc.T("Try again"); icon = "IconWarning"; }
        else if (_loading) { title = Loc.T("Loading…"); }
        else if (_items.Count == 0)
        {
            title = _query != null ? Loc.T("Nothing found") : Loc.T("This folder is empty");
            text = _query != null ? Loc.T("No file or folder has that name.") : Loc.T("Drop files here or press Upload.");
            icon = _query != null ? "IconSearch" : "IconFolder";
        }
        var show = title != null;
        StatePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TileList.Visibility = !show && _host.S.GridView ? Visibility.Visible : Visibility.Collapsed;
        RowPane.Visibility = !show && !_host.S.GridView ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            StateTitle.Text = title;
            StateText.Text = text ?? "";
            StateText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            StateIcon.Data = (Geometry)FindResource(icon);
            StateAction.Content = action;
            StateAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
        }
        var canWrite = st.Online && st.ReadWrite && _query == null;
        UploadBtn.IsEnabled = NewFolderBtn.IsEnabled = canWrite;
    }

    private void OnStateAction(object sender, RoutedEventArgs e)
    {
        var st = _host.Node.Status;
        if (_error != null || st.State == NodeStates.Offline) { _ = Reload(); return; }
        (Window.GetWindow(this) as MainWindow)?.Navigate("settings");
    }

    // --- header ---

    private void BuildCrumbs()
    {
        Crumbs.Children.Clear();
        if (_query != null)
        {
            Crumbs.Children.Add(Crumb(Loc.T("My Drive"), () => ClearSearch(), false));
            Crumbs.Children.Add(Sep());
            Crumbs.Children.Add(new TextBlock { Text = Loc.T("Results for “{0}”", _query), Style = (Style)FindResource("H2"), VerticalAlignment = VerticalAlignment.Center });
            return;
        }
        var parts = _path.Length == 0 ? Array.Empty<string>() : _path.Split('/');
        Crumbs.Children.Add(parts.Length == 0
            ? new TextBlock { Text = Loc.T("My Drive"), Style = (Style)FindResource("H1"), VerticalAlignment = VerticalAlignment.Center }
            : Crumb(Loc.T("My Drive"), () => GoTo(""), false));
        var acc = "";
        for (var i = 0; i < parts.Length; i++)
        {
            acc = acc.Length == 0 ? parts[i] : acc + "/" + parts[i];
            var target = acc;
            Crumbs.Children.Add(Sep());
            Crumbs.Children.Add(i == parts.Length - 1
                ? new TextBlock { Text = parts[i], Style = (Style)FindResource("H1"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 }
                : Crumb(parts[i], () => GoTo(target), false));
        }
    }

    private Button Crumb(string text, Action go, bool bold)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("TextBtn"), FontSize = 20, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = (Brush)FindResource("MutedBrush") };
        b.Click += (_, _) => go();
        return b;
    }

    private UIElement Sep() => new Glyph
    {
        Data = (Geometry)FindResource("IconChevron"), Width = 16, Height = 16, Stretch = Stretch.Uniform, Fill = (Brush)FindResource("MutedBrush"),
        Margin = new Thickness(2, 3, 2, 0), VerticalAlignment = VerticalAlignment.Center,
    };

    private void BuildPinned()
    {
        var chips = new List<UIElement>();
        if (_path.Length == 0 && _query == null)
            foreach (var p in _pins.OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase))
            {
                var target = p;
                var b = new Button { Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new Glyph { Data = (Geometry)FindResource("IconPin"), Width = 12, Height = 12, Stretch = Stretch.Uniform, Fill = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = p.Contains('/') ? p[(p.LastIndexOf('/') + 1)..] : p, FontWeight = FontWeights.Normal });
                b.Content = sp;
                b.Click += (_, _) => GoTo(target);
                chips.Add(b);
            }
        PinnedRow.ItemsSource = chips;
        PinnedRow.Visibility = chips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GoTo(string path)
    {
        _query = null;
        SearchBox.Text = "";
        _path = path.Trim('/');
        _items.Clear();
        _ = Reload();
    }

    private void ClearSearch()
    {
        _query = null;
        SearchBox.Text = "";
        _items.Clear();
        _ = Reload();
    }

    private readonly DispatcherTimer _searchSoon = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        // Search as you type: a short pause after the last letter.
        _searchSoon.Tick -= SearchNow;
        _searchSoon.Tick += SearchNow;
        _searchSoon.Stop();
        _searchSoon.Start();
    }

    private void SearchNow(object? sender, EventArgs e)
    {
        _searchSoon.Stop();
        var q = SearchBox.Text.Trim();
        if (q.Length == 0) { if (_query != null) ClearSearch(); return; }
        if (q == _query) return;
        _query = q;
        _items.Clear();
        _ = Reload();
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var q = SearchBox.Text.Trim();
            if (q.Length == 0) { if (_query != null) ClearSearch(); return; }
            _query = q;
            _items.Clear();
            _ = Reload();
        }
        else if (e.Key == Key.Escape && _query != null) ClearSearch();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = Reload();

    private bool _settingView;
    private void OnViewGrid(object sender, RoutedEventArgs e) => SetView(true);
    private void OnViewList(object sender, RoutedEventArgs e) => SetView(false);

    private void SetView(bool grid)
    {
        if (_settingView) return;
        _settingView = true;
        GridToggle.IsChecked = grid;
        ListToggle.IsChecked = !grid;
        _settingView = false;
        if (_host.S.GridView != grid) _host.Settings.Update(s => s.GridView = grid);
        UpdateState();
    }

    private ListBox ActiveList => _host.S.GridView ? TileList : RowList;

    private List<EntryVm> GetSelected() => ActiveList.SelectedItems.OfType<EntryVm>().ToList();

    // --- opening ---

    private async void OnItemOpen(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: EntryVm vm })
        {
            e.Handled = true;
            await Open(vm);
        }
    }

    private async Task Open(EntryVm vm)
    {
        var api = Api;
        if (api == null) return;
        try
        {
            if (vm.IsDir)
            {
                if (vm.IsLocked && !await Unlock(vm)) return;
                GoTo(vm.FullPath);
            }
            else await OpenFile(vm);
        }
        catch (NodeException e) { Fail(e); }
    }

    private async Task<bool> Unlock(EntryVm vm)
    {
        var api = Api!;
        var pw = Dialogs.Password(Owner, Loc.T("Unlock “{0}”", vm.Name), Loc.T("This folder is end-to-end encrypted. Enter your vault password to open it."));
        if (pw == null) return false;
        try
        {
            await api.ProtectedOpen(vm.FullPath, pw, 15 * 60);
            await _host.Node.Refresh();
            return true;
        }
        catch (NodeException e) when (e.WrongPassword) { Dialogs.Info(Owner, Loc.T("Wrong password"), Loc.T("That is not your vault password.")); return false; }
        catch (NodeException e) { Fail(e); return false; }
    }

    private string CachedPath(EntryVm vm)
    {
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{vm.FullPath}|{vm.E.Size}|{vm.E.Mtime}"))).ToLowerInvariant()[..16];
        return Path.Combine(Paths.Opened, key, vm.Name);
    }

    /// <summary>The file on this PC (downloaded into the app's cache the first time).</summary>
    private async Task<string?> Materialize(EntryVm vm, string verb)
    {
        var api = Api!;
        var local = CachedPath(vm);
        if (File.Exists(local) && new FileInfo(local).Length == vm.E.Size) return local;
        var t = AddTransfer(vm.Name, "IconDownload");
        t.Status = verb;
        try
        {
            await api.Download(vm.FullPath, local, new Progress<long>(b => t.Percent = vm.E.Size > 0 ? 100.0 * b / vm.E.Size : 0), t.Cts.Token);
            Done(t, Loc.T("Ready"));
            return local;
        }
        catch (OperationCanceledException) { Done(t, Loc.T("Cancelled")); return null; }
        catch (NodeException e) { Done(t, e.Message); return null; }
    }

    private async Task OpenFile(EntryVm vm)
    {
        var local = await Materialize(vm, Loc.T("Opening…"));
        if (local == null) return;
        try { Process.Start(new ProcessStartInfo(local) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Info(Owner, Loc.T("Could not open the file"), ex.Message); }
    }

    // --- context menu ---

    private void OnItemRight(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            if (!item.IsSelected)
            {
                ActiveList.SelectedItems.Clear();
                item.IsSelected = true;
            }
        }
    }

    private void OnItemMenu(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: EntryVm vm } btn) return;
        var list = ActiveList;
        if (!list.SelectedItems.Contains(vm))
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(vm);
        }
        _menuFromButton = true;
        var menu = list.ContextMenu!;
        menu.PlacementTarget = btn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        if (!_menuFromButton && !(Mouse.DirectlyOver is DependencyObject d && Ancestor<ListBoxItem>(d) != null))
            ActiveList.SelectedItems.Clear();
        _menuFromButton = false;

        var api = Api;
        if (api == null) { menu.IsOpen = false; return; }
        var sel = GetSelected();
        var canWrite = _host.Node.Status.ReadWrite;

        MenuItem Item(string header, string icon, Action act, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, Icon = WinUtil.Glyph(icon, 16), IsEnabled = enabled };
            mi.Click += (_, _) => act();
            return mi;
        }

        if (sel.Count == 0)
        {
            if (_query != null) { menu.IsOpen = false; return; }
            menu.Items.Add(Item(Loc.T("Upload files…"), "IconUpload", () => OnUpload(this, new RoutedEventArgs()), canWrite));
            menu.Items.Add(Item(Loc.T("New folder"), "IconNewFolder", () => OnNewFolder(this, new RoutedEventArgs()), canWrite));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Loc.T("Refresh"), "IconRefresh", () => _ = Reload()));
            return;
        }
        if (sel.Count == 1)
        {
            var v = sel[0];
            menu.Items.Add(Item(Loc.T("Open"), "IconOpen", () => _ = Open(v)));
            if (!v.IsDir)
            {
                menu.Items.Add(Item(Loc.T("Download…"), "IconDownload", () => _ = Download(sel)));
                var send = new MenuItem { Header = Loc.T("Send to a device…"), Icon = WinUtil.Glyph("IconSend", 16) };
                send.Click += (_, _) => _ = SendToDevice(v);
                menu.Items.Add(send);
            }
            else
            {
                menu.Items.Add(Item(Loc.T("Download…"), "IconDownload", () => _ = Download(sel), !v.IsLocked));
            }
            menu.Items.Add(new Separator());
            if (v.IsDir)
            {
                menu.Items.Add(Item(v.Pinned ? Loc.T("Unpin from the top") : Loc.T("Pin to the top"), "IconPin", () => _ = TogglePin(v)));
                menu.Items.Add(Item(Loc.T("Folder colour…"), "IconPalette", () => _ = ChooseColor(v)));
                menu.Items.Add(Item(Loc.T("Share with people…"), "IconShare", () => _ = Share(v), !v.IsProtected));
                if (v.IsProtected)
                    menu.Items.Add(v.IsLocked
                        ? Item(Loc.T("Unlock…"), "IconLock", () => _ = UnlockAndRefresh(v))
                        : Item(Loc.T("Lock"), "IconLock", () => _ = Lock(v)));
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(Item(Loc.T("Rename…"), "IconEdit", () => _ = Rename(v), canWrite));
            menu.Items.Add(Item(Loc.T("Delete"), "IconTrash", () => _ = Delete(sel), canWrite));
        }
        else
        {
            menu.Items.Add(Item(Loc.T("Download…"), "IconDownload", () => _ = Download(sel)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Loc.T("Delete {0} items", sel.Count), "IconTrash", () => _ = Delete(sel), canWrite));
        }
    }

    private static T? Ancestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var c = d; c != null; c = VisualTreeHelper.GetParent(c) ?? (c as FrameworkContentElement)?.Parent)
            if (c is T t) return t;
        return null;
    }

    private void OnListKey(object sender, KeyEventArgs e)
    {
        var sel = GetSelected();
        switch (e.Key)
        {
            case Key.Enter when sel.Count == 1: _ = Open(sel[0]); e.Handled = true; break;
            case Key.Delete when sel.Count > 0 && _host.Node.Status.ReadWrite: _ = Delete(sel); e.Handled = true; break;
            case Key.F2 when sel.Count == 1: _ = Rename(sel[0]); e.Handled = true; break;
            case Key.Back when _path.Length > 0 && _query == null: GoTo(_path.Contains('/') ? _path[.._path.LastIndexOf('/')] : ""); e.Handled = true; break;
            case Key.A when Keyboard.Modifiers == ModifierKeys.Control: ActiveList.SelectAll(); e.Handled = true; break;
        }
    }

    // --- actions on the Drive ---

    private void Fail(NodeException e) => Dialogs.Info(Owner, Loc.T("Something went wrong"), e.Message);

    private async Task Rename(EntryVm v)
    {
        var name = Dialogs.Prompt(Owner, Loc.T("Rename"), "", v.Name, Loc.T("Rename"));
        if (name == null || name == v.Name) return;
        if (name.Contains('/') || name.Contains('\\')) { Dialogs.Info(Owner, Loc.T("Rename"), Loc.T("A name cannot contain / or \\.")); return; }
        var parent = v.FullPath.Contains('/') ? v.FullPath[..v.FullPath.LastIndexOf('/')] : "";
        try { await Api!.Move(v.FullPath, parent.Length == 0 ? name : parent + "/" + name); await Reload(); }
        catch (NodeException e) { Fail(e); }
    }

    private async Task Delete(List<EntryVm> sel)
    {
        var what = sel.Count == 1 ? Loc.T("“{0}”", sel[0].Name) : Loc.T("{0} items", sel.Count);
        if (!Dialogs.Confirm(Owner, Loc.T("Delete"), Loc.T("Delete {0}? It goes to the Drive's trash, where you can restore it from the web app.", what), Loc.T("Delete"), true)) return;
        try
        {
            foreach (var v in sel) await Api!.Remove(v.FullPath);
            await Reload();
        }
        catch (NodeException e) { Fail(e); }
    }

    private async void OnNewFolder(object sender, RoutedEventArgs e)
    {
        var name = Dialogs.Prompt(Owner, Loc.T("New folder"), "", "", Loc.T("Create"), Loc.T("Folder name"));
        if (name == null) return;
        if (name.Contains('/') || name.Contains('\\')) { Dialogs.Info(Owner, Loc.T("New folder"), Loc.T("A name cannot contain / or \\.")); return; }
        try { await Api!.Mkdir(_path.Length == 0 ? name : _path + "/" + name); await Reload(); }
        catch (NodeException ex) { Fail(ex); }
    }

    private async Task TogglePin(EntryVm v)
    {
        try { await Api!.SetPinned(v.FullPath, !v.Pinned); v.Pinned = !v.Pinned; _ = Reload(true); }
        catch (NodeException e) { Fail(e); }
    }

    private async Task ChooseColor(EntryVm v)
    {
        var hex = Dialogs.ChooseColor(Owner, v.ColorHex ?? "");
        if (hex == null) return;
        try { await Api!.SetFolderColor(v.FullPath, hex); v.ColorHex = hex.Length == 0 ? null : hex; }
        catch (NodeException e) { Fail(e); }
    }

    private async Task Share(EntryVm v)
    {
        if (Api != null) await Dialogs.ShareFolder(Owner, Api, v.FullPath);
    }

    private async Task Lock(EntryVm v)
    {
        try { await Api!.ProtectedLock(v.FullPath); await _host.Node.Refresh(); await Reload(); }
        catch (NodeException e) { Fail(e); }
    }

    private async Task UnlockAndRefresh(EntryVm v)
    {
        if (await Unlock(v)) await Reload();
    }

    // --- sending to other devices ---

    private async Task SendToDevice(EntryVm v)
    {
        try
        {
            var devices = (await Api!.Devices()).Where(d => d.Online).ToList();
            var dev = Dialogs.PickDevice(Owner, devices, Loc.T("Send “{0}” to one of your other devices.", v.Name));
            if (dev == null) return;
            var local = await Materialize(v, Loc.T("Getting the file…"));
            if (local == null) return;
            await _host.Transfers.SendFile(dev, local);
            (Window.GetWindow(this) as MainWindow)?.Navigate("devices");
        }
        catch (NodeException e) { Fail(e); }
    }

    // --- downloads ---

    private async Task Download(List<EntryVm> sel)
    {
        var api = Api!;
        try
        {
            if (sel.Count == 1 && !sel[0].IsDir)
            {
                var v = sel[0];
                var dlg = new SaveFileDialog { FileName = v.Name, Title = Loc.T("Download") };
                if (dlg.ShowDialog(Owner) != true) return;
                var t = AddTransfer(v.Name, "IconDownload");
                try
                {
                    await api.Download(v.FullPath, dlg.FileName, new Progress<long>(b => t.Percent = v.E.Size > 0 ? 100.0 * b / v.E.Size : 0), t.Cts.Token);
                    Done(t, Loc.T("Saved"));
                }
                catch (OperationCanceledException) { Done(t, Loc.T("Cancelled")); }
                catch (NodeException e) { Done(t, e.Message); }
                return;
            }
            var pick = new OpenFolderDialog { Title = Loc.T("Choose where to save") };
            if (pick.ShowDialog(Owner) != true) return;
            foreach (var v in sel)
            {
                var t = AddTransfer(v.Name, "IconDownload");
                try
                {
                    if (!v.IsDir)
                    {
                        await api.Download(v.FullPath, Paths.Unique(pick.FolderName, v.Name), new Progress<long>(b => t.Percent = v.E.Size > 0 ? 100.0 * b / v.E.Size : 0), t.Cts.Token);
                    }
                    else
                    {
                        var all = await api.Tree(v.FullPath, t.Cts.Token);
                        var files = all.Where(x => !x.Dir).ToList();
                        var total = Math.Max(1, files.Sum(f => f.Size));
                        long done = 0;
                        var root = Paths.Unique(pick.FolderName, v.Name);
                        Directory.CreateDirectory(root);
                        foreach (var d in all.Where(x => x.Dir)) Directory.CreateDirectory(Path.Combine(root, Rel(d.Path!, v.FullPath)));
                        foreach (var f in files)
                        {
                            var baseDone = done;
                            await api.Download(f.Path!, Path.Combine(root, Rel(f.Path!, v.FullPath)),
                                new Progress<long>(b => t.Percent = 100.0 * (baseDone + b) / total), t.Cts.Token);
                            done += f.Size;
                        }
                    }
                    Done(t, Loc.T("Saved"));
                }
                catch (OperationCanceledException) { Done(t, Loc.T("Cancelled")); }
                catch (NodeException e) { Done(t, e.Message); }
            }
        }
        catch (NodeException e) { Fail(e); }
    }

    private static string Rel(string full, string root) => full.Length > root.Length ? full[(root.Length + 1)..].Replace('/', Path.DirectorySeparatorChar) : "";

    // --- uploads ---

    private void OnUpload(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = Loc.T("Upload") };
        if (dlg.ShowDialog(Owner) == true) _ = UploadPaths(dlg.FileNames);
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop) && Api != null && _host.Node.Status is { Online: true, ReadWrite: true } && _query == null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && _host.Node.Status is { Online: true, ReadWrite: true } && _query == null)
            _ = UploadPaths(files);
        e.Handled = true;
    }

    private async Task UploadPaths(IEnumerable<string> paths)
    {
        var api = Api;
        if (api == null) return;
        var dest = _path;
        foreach (var local in paths.ToList())
        {
            var isDir = Directory.Exists(local);
            var name = Path.GetFileName(local.TrimEnd('\\', '/'));
            var t = AddTransfer(name, "IconUpload");
            try
            {
                var files = isDir ? Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).ToList() : new List<string> { local };
                var total = Math.Max(1, files.Sum(f => new FileInfo(f).Length));
                long done = 0;
                string Remote(string rel) => string.Join("/", new[] { dest, rel }.Where(s => s.Length > 0));
                if (isDir)
                {
                    await api.Mkdir(Remote(name), t.Cts.Token);
                    foreach (var d in Directory.EnumerateDirectories(local, "*", SearchOption.AllDirectories))
                        await api.Mkdir(Remote(name + "/" + Path.GetRelativePath(local, d).Replace('\\', '/')), t.Cts.Token);
                }
                foreach (var f in files)
                {
                    var rel = isDir ? name + "/" + Path.GetRelativePath(local, f).Replace('\\', '/') : name;
                    var baseDone = done;
                    await api.Upload(f, Remote(rel), new Progress<long>(b => t.Percent = 100.0 * (baseDone + b) / total), t.Cts.Token);
                    done += new FileInfo(f).Length;
                }
                Done(t, Loc.T("Uploaded"));
            }
            catch (OperationCanceledException) { Done(t, Loc.T("Cancelled")); }
            catch (NodeException e) { Done(t, e.Message); }
            catch (IOException e) { Done(t, e.Message); }
        }
        await Reload(true);
    }

    // --- the bar at the bottom ---

    private TransferVm AddTransfer(string title, string icon)
    {
        var t = new TransferVm { Title = title, Icon = (Geometry)FindResource(icon), Status = "" };
        _transfers.Add(t);
        return t;
    }

    private void Done(TransferVm t, string status)
    {
        t.Running = false;
        t.Percent = 100;
        t.Status = status;
        // A finished one stays a moment, then goes.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) => { timer.Stop(); _transfers.Remove(t); };
        timer.Start();
    }

    private void OnTransferClose(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferVm t })
        {
            if (t.Running) t.Cts.Cancel();
            else _transfers.Remove(t);
        }
    }
}
