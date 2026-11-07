using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Overdrive.Core;

namespace Overdrive.Pages;

/// <summary>Task-Manager-style process list: Apps / Background processes, expandable groups, sortable columns, heat-map cells.</summary>
public class AppsPage : UserControl, IRefreshable
{
    // ------------------------------------------------------------------ data
    class Proc { public int Pid; public long Ram; public double? Cpu; }
    class Group
    {
        public string Key;            // process name
        public string Display;        // friendly name ("Google Chrome")
        public string ExePath;
        public bool IsApp, IsProtected, IsGame;
        public List<Proc> Procs = new();
        public long Ram => Procs.Sum(p => p.Ram);
        public double? Cpu => Procs.Any(p => p.Cpu.HasValue) ? Procs.Sum(p => p.Cpu ?? 0) : null;
    }

    internal static readonly HashSet<string> SystemProcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "fontdrvhost", "dwm", "spoolsv", "sihost", "ctfmon", "conhost", "dllhost", "taskhostw", "RuntimeBroker",
        "SecurityHealthService", "MsMpEng", "NisSrv", "SgrmBroker", "audiodg", "WUDFHost", "dasHost", "WmiPrvSE", "LsaIso",
        "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "TextInputHost", "explorer", "Secure System", "vmmem",
        "vgc", "vgtray", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEService_x64", "FACEITService", "faceit",
    };
    internal static readonly HashSet<string> AntiCheat = new(StringComparer.OrdinalIgnoreCase) { "vgc", "vgtray", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEService_x64", "FACEITService", "faceit" };

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _lastCpu = new();
    readonly Dictionary<string, (string Name, ImageSource Icon)> _meta = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Row> _rows = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ ui
    readonly StackPanel _list = new();
    readonly TextBlock _cpuHead = new(), _memHead = new(), _nameHead = new();
    readonly TextBox _search = new();
    readonly TextBlock _placeholder = new() { Text = "Type a name to search", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly Button _endBtn;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    string _sort = "mem"; bool _desc = true;
    string _selected; int? _selectedPid;
    bool _busy;
    List<Group> _groups = new();

    const double ColCpu = 96, ColMem = 120;

    public AppsPage()
    {
        _placeholder.Foreground = UI.B("Dim");
        var body = new StackPanel();
        body.Children.Add(UI.Header("Apps", "See what's using your PC and close it before a match."));

        // toolbar
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var searchBox = new Grid { Width = 300, Height = 38, HorizontalAlignment = HorizontalAlignment.Left };
        _search.Style = UI.Res<Style>("SearchInput");
        _search.TextChanged += (_, _) => { _placeholder.Visibility = string.IsNullOrEmpty(_search.Text) ? Visibility.Visible : Visibility.Collapsed; Render(); };
        searchBox.Children.Add(_search);
        searchBox.Children.Add(new TextBlock { Text = "", FontFamily = UI.IconFont, FontSize = 13, Foreground = UI.B("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), IsHitTestVisible = false });
        _placeholder.Margin = new Thickness(38, 0, 0, 0);
        searchBox.Children.Add(_placeholder);
        bar.Children.Add(searchBox);
        _endBtn = UI.Btn("End task", async (s, e) => await EndSelected(), style: "DangerButton", icon: "");
        _endBtn.Margin = new Thickness(0); _endBtn.IsEnabled = false;
        Grid.SetColumn(_endBtn, 1);
        bar.Children.Add(_endBtn);
        body.Children.Add(bar);

        // table
        var table = new StackPanel();
        var head = ColumnsGrid();
        head.Height = 44;
        head.Background = UI.Hex("#121620");
        head.Children.Add(HeaderCell(_nameHead, "name", 0, HorizontalAlignment.Left, 3));
        head.Children.Add(HeaderCell(_cpuHead, "cpu", 3, HorizontalAlignment.Right));
        head.Children.Add(HeaderCell(_memHead, "mem", 4, HorizontalAlignment.Right));
        table.Children.Add(new Border { Child = head, BorderBrush = UI.B("Line"), BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(14, 14, 0, 0), ClipToBounds = true });
        table.Children.Add(_list);
        var card = new Border { Style = UI.Res<Style>("CardBorder"), Padding = new Thickness(0), Child = table };
        body.Children.Add(card);

        Content = UI.PageScroll(body);
        _timer.Tick += async (_, _) => await Update();
        IsVisibleChanged += async (_, _) => { if (IsVisible) { _timer.Start(); await Update(); } else _timer.Stop(); };
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.Delete && _endBtn.IsEnabled) await EndSelected(); };
    }

    static Grid ColumnsGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });   // chevron
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });   // icon
        g.ColumnDefinitions.Add(new ColumnDefinition());                               // name
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColCpu) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColMem) });
        return g;
    }

    Border HeaderCell(TextBlock tb, string key, int col, HorizontalAlignment align, int span = 1)
    {
        tb.FontSize = 12; tb.FontWeight = FontWeights.SemiBold; tb.Foreground = UI.B("Sub");
        tb.HorizontalAlignment = align; tb.VerticalAlignment = VerticalAlignment.Center; tb.TextAlignment = align == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left;
        var b = new Border { Child = tb, Padding = new Thickness(col == 0 ? 16 : 8, 0, 16, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, BorderBrush = UI.B("Line"), BorderThickness = new Thickness(col == 0 ? 0 : 1, 0, 0, 0) };
        b.MouseEnter += (_, _) => b.Background = UI.Hex("#1A2030");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => { if (_sort == key) _desc = !_desc; else { _sort = key; _desc = key != "name"; } Render(); };
        Grid.SetColumn(b, col); Grid.SetColumnSpan(b, span);
        return b;
    }

    // ------------------------------------------------------------------ sampling
    async Task Update()
    {
        if (_busy) return;
        _busy = true;
        try { _groups = await Task.Run(Snapshot); Render(); }
        catch (Exception ex) { Store.Log("Apps page: " + ex.Message); }
        finally { _busy = false; }
    }

    List<Group> Snapshot()
    {
        var gameNames = new HashSet<string>(Games.All.SelectMany(g => g.ProcessNames), StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var all = Process.GetProcesses();
        var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();
        foreach (var p in all)
        {
            try
            {
                if (p.Id == 0 || p.Id == 4) continue;
                var n = p.ProcessName;
                if (!groups.TryGetValue(n, out var g))
                {
                    g = new Group { Key = n, IsGame = gameNames.Contains(n) || AntiCheat.Contains(n), IsProtected = SystemProcs.Contains(n) || SelfInstall.IsOurProcess(n) };
                    groups[n] = g;
                }
                var pr = new Proc { Pid = p.Id, Ram = p.WorkingSet64 }; // from the system snapshot, no handle needed
                // has a visible window -> it's an "app" (window lookup doesn't open the process)
                try { if (p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle)) g.IsApp = true; } catch { }
                if (!g.IsGame && !g.IsProtected)
                {
                    seen.Add(p.Id);
                    try
                    {
                        var t = p.TotalProcessorTime;
                        if (_lastCpu.TryGetValue(p.Id, out var last))
                        {
                            var secs = (now - last.At).TotalSeconds;
                            if (secs > 0) pr.Cpu = Math.Clamp((t - last.Cpu).TotalSeconds / secs / Environment.ProcessorCount * 100, 0, 100);
                        }
                        else pr.Cpu = 0;
                        _lastCpu[p.Id] = (t, now);
                    }
                    catch { }
                    g.ExePath ??= Native.ProcessPath(p.Id);
                }
                g.Procs.Add(pr);
            }
            catch { }
            finally { p.Dispose(); }
        }
        foreach (var k in _lastCpu.Keys.Where(k => !seen.Contains(k)).ToList()) _lastCpu.Remove(k);

        foreach (var g in groups.Values)
        {
            if (g.ExePath != null && !_meta.ContainsKey(g.ExePath)) _meta[g.ExePath] = LoadMeta(g.ExePath, g.Key);
            g.Display = g.ExePath != null && _meta.TryGetValue(g.ExePath, out var m) ? m.Name : g.Key;
            if (g.IsGame) g.Display = Games.All.FirstOrDefault(x => x.ProcessNames.Contains(g.Key, StringComparer.OrdinalIgnoreCase))?.Name ?? g.Key;
        }
        return groups.Values.ToList();
    }

    static (string, ImageSource) LoadMeta(string path, string fallback)
    {
        string name = fallback;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(vi.FileDescription)) name = vi.FileDescription.Trim();
            else if (!string.IsNullOrWhiteSpace(vi.ProductName)) name = vi.ProductName.Trim();
        }
        catch { }
        ImageSource icon = null;
        try
        {
            var icons = new IntPtr[1]; var ids = new uint[1];
            if (PrivateExtractIcons(path, 0, 32, 32, icons, ids, 1, 0) > 0 && icons[0] != IntPtr.Zero)
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                DestroyIcon(icons[0]); src.Freeze(); icon = src;
            }
        }
        catch { }
        return (name, icon);
    }

    // ------------------------------------------------------------------ rendering (rows are reused, values updated in place)
    class Row
    {
        public Border Root; public TextBlock Chevron, Name, Cpu, Mem; public Border CpuCell, MemCell; public Image Icon; public TextBlock IconGlyph;
        public StackPanel Children = new();
        public Group Group;
    }

    Row MakeRow(string key)
    {
        var r = new Row();
        var g = ColumnsGrid();
        g.Height = 40;
        r.Chevron = new TextBlock { FontFamily = UI.IconFont, FontSize = 10, Foreground = UI.B("Sub"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var chevBox = new Border { Child = r.Chevron, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        chevBox.MouseLeftButtonUp += (_, e) => { if (!_expanded.Remove(key)) _expanded.Add(key); Render(); e.Handled = true; };
        g.Children.Add(chevBox);
        r.Icon = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(r.Icon, BitmapScalingMode.HighQuality);
        r.IconGlyph = new TextBlock { FontFamily = UI.IconFont, FontSize = 15, Foreground = UI.B("Dim"), VerticalAlignment = VerticalAlignment.Center };
        var iconHost = new Grid(); iconHost.Children.Add(r.Icon); iconHost.Children.Add(r.IconGlyph);
        Grid.SetColumn(iconHost, 1); g.Children.Add(iconHost);
        r.Name = UI.Text("", 13, "Text", FontWeights.Normal, false); r.Name.VerticalAlignment = VerticalAlignment.Center; r.Name.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(r.Name, 2); g.Children.Add(r.Name);
        (r.CpuCell, r.Cpu) = Cell(3, g);
        (r.MemCell, r.Mem) = Cell(4, g);

        r.Root = new Border { Child = g, Background = Brushes.Transparent, BorderBrush = UI.Hex("#1B2130"), BorderThickness = new Thickness(0, 0, 0, 1), Cursor = Cursors.Hand };
        r.Root.MouseEnter += (_, _) => { if (_selected != key || _selectedPid != null) r.Root.Background = UI.Hex("#161C28"); };
        r.Root.MouseLeave += (_, _) => Highlight(r, key);
        r.Root.MouseLeftButtonUp += (_, _) => Select(key, null);
        r.Root.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { if (!_expanded.Remove(key)) _expanded.Add(key); Render(); } };
        r.Root.MouseRightButtonUp += (_, e) => { Select(key, null); ShowMenu(r.Root, r.Group, null); e.Handled = true; };
        return r;
    }

    static (Border, TextBlock) Cell(int col, Grid g)
    {
        var t = new TextBlock { FontSize = 13, Foreground = UI.B("Text"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border { Child = t, Padding = new Thickness(8, 0, 16, 0), BorderBrush = UI.Hex("#1B2130"), BorderThickness = new Thickness(1, 0, 0, 0) };
        Grid.SetColumn(b, col); g.Children.Add(b);
        return (b, t);
    }

    static Brush Heat(double frac)
    {
        frac = Math.Clamp(frac, 0, 1);
        if (frac < 0.02) return Brushes.Transparent;
        byte a = (byte)(25 + 150 * frac);
        return new SolidColorBrush(Color.FromArgb(a, 0xFF, 0xB5, 0x47)); // Task Manager-style amber heat
    }

    static string Mem(long bytes) => $"{bytes / 1024.0 / 1024.0:N1} MB";

    void Render()
    {
        if (_groups.Count == 0) return;
        var q = _search.Text?.Trim() ?? "";
        IEnumerable<Group> gs = _groups;
        if (q.Length > 0) gs = gs.Where(g => g.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || g.Key.Contains(q, StringComparison.OrdinalIgnoreCase));
        Func<Group, object> key = _sort switch { "cpu" => g => g.Cpu ?? -1, "name" => g => g.Display.ToLowerInvariant(), _ => g => g.Ram };
        var sorted = (_desc ? gs.OrderByDescending(key) : gs.OrderBy(key)).ToList();
        var apps = sorted.Where(g => g.IsApp).ToList();
        var bg = sorted.Where(g => !g.IsApp).ToList();

        // headers with totals, like Task Manager
        var cpuTotal = SysInfo.CpuUsage();
        var (_, memPct) = SysInfo.RamUsage();
        string Arrow(string k) => _sort == k ? (_desc ? "  ▾" : "  ▴") : "";
        _nameHead.Text = "Name" + Arrow("name");
        _cpuHead.Inlines.Clear();
        _cpuHead.Inlines.Add(new System.Windows.Documents.Run($"{cpuTotal:0}%\n") { FontSize = 15, FontWeight = FontWeights.Bold, Foreground = UI.B("Text") });
        _cpuHead.Inlines.Add(new System.Windows.Documents.Run("CPU" + Arrow("cpu")));
        _memHead.Inlines.Clear();
        _memHead.Inlines.Add(new System.Windows.Documents.Run($"{memPct:0}%\n") { FontSize = 15, FontWeight = FontWeights.Bold, Foreground = UI.B("Text") });
        _memHead.Inlines.Add(new System.Windows.Documents.Run("Memory" + Arrow("mem")));

        double totalRam = Math.Max(1, SysInfo.RamGb * 1024 * 1024 * 1024);
        _list.Children.Clear();
        void Section(string title, List<Group> list)
        {
            if (list.Count == 0) return;
            var h = new TextBlock { Text = $"{title} ({list.Count})", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = UI.B("Accent"), Margin = new Thickness(16, 14, 0, 6) };
            _list.Children.Add(h);
            foreach (var g in list)
            {
                if (!_rows.TryGetValue(g.Key, out var r)) _rows[g.Key] = r = MakeRow(g.Key);
                r.Group = g;
                bool multi = g.Procs.Count > 1;
                bool open = multi && _expanded.Contains(g.Key);
                r.Chevron.Text = multi ? (open ? "" : "") : "";
                r.Name.Text = multi ? $"{g.Display} ({g.Procs.Count})" : g.Display;
                r.Name.FontWeight = g.IsApp ? FontWeights.SemiBold : FontWeights.Normal;
                var meta = g.ExePath != null && _meta.TryGetValue(g.ExePath, out var m) ? m.Icon : null;
                r.Icon.Source = meta; r.Icon.Visibility = meta != null ? Visibility.Visible : Visibility.Collapsed;
                r.IconGlyph.Text = g.IsGame ? "" : g.IsProtected ? "" : "";
                r.IconGlyph.Visibility = meta == null ? Visibility.Visible : Visibility.Collapsed;
                r.Cpu.Text = g.Cpu.HasValue ? $"{g.Cpu:0.0}%" : "-";
                r.Cpu.ToolTip = g.Cpu.HasValue ? null : g.IsGame ? "Not measured: PC Optimizer never opens game or anti-cheat processes." : "Protected Windows process";
                r.CpuCell.Background = Heat((g.Cpu ?? 0) / 25.0);
                r.Mem.Text = Mem(g.Ram);
                r.MemCell.Background = Heat(g.Ram / (totalRam * 0.08));
                Highlight(r, g.Key);
                _list.Children.Add(r.Root);

                if (open)
                    foreach (var p in g.Procs.OrderByDescending(p => p.Ram))
                        _list.Children.Add(ChildRow(g, p, totalRam));
            }
        }
        Section("Apps", apps);
        Section("Background processes", bg);
        if (apps.Count + bg.Count == 0) _list.Children.Add(new TextBlock { Text = "Nothing matches your search.", Foreground = UI.B("Sub"), Margin = new Thickness(16) });

        UpdateEndButton();
    }

    UIElement ChildRow(Group g, Proc p, double totalRam)
    {
        var grid = ColumnsGrid();
        grid.Height = 34;
        var name = UI.Text($"{g.Key}.exe   ·   PID {p.Pid}", 12, "Sub", wrap: false);
        name.VerticalAlignment = VerticalAlignment.Center; name.Margin = new Thickness(26, 0, 0, 0);
        Grid.SetColumn(name, 2); grid.Children.Add(name);
        var (cc, ct) = Cell(3, grid); ct.Text = p.Cpu.HasValue ? $"{p.Cpu:0.0}%" : "-"; ct.FontSize = 12; cc.Background = Heat((p.Cpu ?? 0) / 25.0);
        var (mc, mt) = Cell(4, grid); mt.Text = Mem(p.Ram); mt.FontSize = 12; mc.Background = Heat(p.Ram / (totalRam * 0.08));
        var b = new Border { Child = grid, Background = _selectedPid == p.Pid ? UI.Hex("#1C2B3A") : UI.Hex("#10141C"), BorderBrush = UI.Hex("#1B2130"), BorderThickness = new Thickness(0, 0, 0, 1), Cursor = Cursors.Hand };
        b.MouseLeftButtonUp += (_, _) => Select(g.Key, p.Pid);
        b.MouseRightButtonUp += (_, e) => { Select(g.Key, p.Pid); ShowMenu(b, g, p.Pid); e.Handled = true; };
        return b;
    }

    void Highlight(Row r, string key)
    {
        r.Root.Background = _selected == key && _selectedPid == null ? UI.Hex("#1C2B3A") : Brushes.Transparent;
        r.Root.BorderBrush = _selected == key && _selectedPid == null ? UI.Hex("#2A5A70") : UI.Hex("#1B2130");
    }

    void Select(string key, int? pid)
    {
        _selected = key; _selectedPid = pid;
        Render();
    }

    void UpdateEndButton()
    {
        var g = _groups.FirstOrDefault(x => string.Equals(x.Key, _selected, StringComparison.OrdinalIgnoreCase));
        _endBtn.IsEnabled = g != null && !g.IsProtected && !AntiCheat.Contains(g.Key);
        _endBtn.ToolTip = g == null ? "Select an app first" : g.IsProtected ? "Protected: Windows needs this to run" : null;
    }

    // ------------------------------------------------------------------ actions
    Popup _menu;
    void ShowMenu(UIElement target, Group g, int? pid)
    {
        if (g == null) return;
        var sp = new StackPanel { Margin = new Thickness(6) };
        Button Item(string icon, string text, Action act, bool enabled = true)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = icon, FontFamily = UI.IconFont, FontSize = 13, Foreground = UI.B("Sub"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(UI.Text(text, 13, "Text", wrap: false));
            var b = new Button { Content = row, Style = UI.Res<Style>("MenuRow"), IsEnabled = enabled, Opacity = enabled ? 1 : 0.4 };
            b.Click += (_, _) => { _menu.IsOpen = false; act(); };
            return b;
        }
        bool canEnd = !g.IsProtected && !AntiCheat.Contains(g.Key);
        sp.Children.Add(Item("", pid.HasValue ? "End this process" : "End task", async () => await EndSelected(), canEnd));
        sp.Children.Add(Item("", "Open file location", () =>
        {
            if (g.ExePath != null) try { Process.Start("explorer.exe", $"/select,\"{g.ExePath}\""); } catch { }
        }, g.ExePath != null));
        _menu = new Popup
        {
            PlacementTarget = target, Placement = PlacementMode.MousePoint, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Child = new Border { Background = UI.Hex("#161B26"), BorderBrush = UI.B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), MinWidth = 200, Child = sp },
        };
        _menu.IsOpen = true;
    }

    async Task EndSelected()
    {
        var g = _groups.FirstOrDefault(x => string.Equals(x.Key, _selected, StringComparison.OrdinalIgnoreCase));
        if (g == null || g.IsProtected || AntiCheat.Contains(g.Key)) return;
        var pids = _selectedPid.HasValue ? new[] { _selectedPid.Value } : g.Procs.Select(p => p.Pid).ToArray();
        var what = _selectedPid.HasValue ? $"{g.Display} (PID {_selectedPid})" : g.Procs.Count > 1 ? $"{g.Display} and its {g.Procs.Count} processes" : g.Display;
        if (!UI.Confirm($"End {what}?\n\nUnsaved work in it will be lost." + (g.IsGame ? "\n\nThis is a game. Ending it closes it immediately." : ""), "End task")) return;
        int ended = 0, failed = 0;
        await Task.Run(() =>
        {
            foreach (var pid in pids)
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(true); p.WaitForExit(2000); ended++; }
                catch { failed++; }
            }
        });
        Store.Log($"Ended {g.Key}: {ended} ok, {failed} failed");
        if (ended == 0 && failed > 0) UI.Info($"Couldn't end {g.Display}. It may be protected by Windows or already closed.");
        _selected = null; _selectedPid = null;
        await Update();
    }

    public void Refresh() { }
}
