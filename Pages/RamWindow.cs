using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Overdrive.Core;

namespace Overdrive.Pages;

/// <summary>Popup that shows where your RAM is going: a stacked bar and the apps using the most, live.</summary>
public class RamWindow : Window
{
    static RamWindow _open;
    static readonly string[] Colors = { "#00E5FF", "#8B5CF6", "#2EE59D", "#FFB547", "#FF8AD8", "#F43F5E" };

    readonly Grid _bar = new() { Height = 16, Margin = new Thickness(0, 14, 0, 8) };
    readonly WrapPanel _legend = new();
    readonly StackPanel _list = new();
    readonly TextBlock _head = UI.Text("", 26, "Text", FontWeights.Bold, false);
    readonly TextBlock _sub = UI.Text("", 12.5, "Sub");
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public static void Open(Window owner)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new RamWindow();
        if (owner != null && owner.IsVisible) _open.Owner = owner; else _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
    }

    RamWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        Width = 600; SizeToContent = SizeToContent.Height; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true; Title = "RAM"; FontFamily = UI.Res<FontFamily>("Ui");

        var sp = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new StackPanel();
        t.Children.Add(UI.Label("WHERE YOUR RAM IS GOING"));
        t.Children.Add(_head);
        t.Children.Add(_sub);
        top.Children.Add(t);
        var close = new Button { Style = UI.Res<Style>("ChromeButton"), Content = new TextBlock { Text = "", FontFamily = UI.IconFont, FontSize = 11 }, Width = 34, Height = 30, VerticalAlignment = VerticalAlignment.Top };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1); top.Children.Add(close);
        sp.Children.Add(top);
        sp.Children.Add(new Border { Child = _bar, CornerRadius = new CornerRadius(8), ClipToBounds = true });
        sp.Children.Add(_legend);
        var lh = UI.Label("APPS USING THE MOST"); lh.Margin = new Thickness(0, 16, 0, 8);
        sp.Children.Add(lh);
        sp.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        var foot = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        Button free = null;
        free = UI.Btn("Free cached RAM", async (s, e) => await UI.Busy(free, async () =>
        {
            double mb = await Task.Run(LowEnd.FreeStandbyRam);
            free.Content = mb > 1 ? $"Freed {mb:0} MB" : "Already free";
            Refresh();
        }), icon: "");
        free.ToolTip = "Releases memory Windows is holding as file cache, so games get it right away.";
        var apps = UI.Btn("Open Apps", (s, e) => { (Application.Current.MainWindow as MainWindow)?.Navigate("NavApps"); Close(); }, icon: "");
        apps.Margin = new Thickness(8, 0, 0, 0);
        foot.Children.Add(free); foot.Children.Add(apps);
        sp.Children.Add(foot);

        Content = new Grid
        {
            Margin = new Thickness(24),
            Children =
            {
                new Border
                {
                    Background = UI.Hex("#151A25"), BorderBrush = UI.Hex("#2A3142"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
                    Padding = new Thickness(24, 20, 20, 20), Child = sp,
                    Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.55, Color = System.Windows.Media.Colors.Black },
                }
            }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { _timer.Stop(); _open = null; };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
    }

    static string Gb(double bytes) => bytes >= 1L << 30 ? $"{bytes / (1L << 30):0.0} GB" : $"{bytes / (1 << 20):0} MB";

    async void Refresh()
    {
        var data = await Task.Run(() =>
        {
            var m = new Native.MEMORYSTATUSEX(); Native.GlobalMemoryStatusEx(m);
            var procs = ProcSampler.Snapshot().Where(p => p.Pid > 4 && p.PrivateBytes > 0)
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Bytes: g.Sum(x => x.PrivateBytes), Count: g.Count()))
                .OrderByDescending(x => x.Bytes).ToList();
            return (Total: (double)m.ullTotalPhys, Avail: (double)m.ullAvailPhys, Procs: procs);
        });
        double used = data.Total - data.Avail;
        _head.Text = $"{Gb(used)} used of {Gb(data.Total)}";
        _sub.Text = $"{Gb(data.Avail)} free for games  ·  {used / data.Total * 100:0}% in use";

        // stacked bar: top 5 apps, other apps, Windows & drivers, free
        var top = data.Procs.Take(5).ToList();
        double appsAll = data.Procs.Sum(p => p.Bytes);
        double other = Math.Max(0, appsAll - top.Sum(p => p.Bytes));
        double system = Math.Max(0, used - appsAll);
        var segs = new List<(string Label, double Bytes, Brush Color)>();
        for (int i = 0; i < top.Count; i++) segs.Add((Pretty(top[i].Name), top[i].Bytes, UI.Hex(Colors[i])));
        segs.Add(("Other apps", other, UI.Hex("#4B5568")));
        segs.Add(("Windows, drivers & cache", system, UI.Hex("#2F3747")));
        segs.Add(("Free", data.Avail, UI.Hex("#1A1F2B")));

        _bar.ColumnDefinitions.Clear(); _bar.Children.Clear();
        for (int i = 0; i < segs.Count; i++)
        {
            _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, segs[i].Bytes), GridUnitType.Star) });
            var r = new Border { Background = segs[i].Color, ToolTip = $"{segs[i].Label}: {Gb(segs[i].Bytes)}", Margin = new Thickness(0, 0, i < segs.Count - 1 ? 1 : 0, 0) };
            Grid.SetColumn(r, i); _bar.Children.Add(r);
        }
        _legend.Children.Clear();
        foreach (var (label, bytes, color) in segs)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6) };
            row.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(UI.Text($"{label} {Gb(bytes)}", 11.5, "Sub", wrap: false));
            _legend.Children.Add(row);
        }

        // list
        _list.Children.Clear();
        double max = data.Procs.FirstOrDefault().Bytes;
        foreach (var (name, bytes, count) in data.Procs.Take(15))
        {
            var g = new Grid { Margin = new Thickness(0, 0, 8, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var n = UI.Text(Pretty(name) + (count > 1 ? $"  ({count})" : ""), 13, "Text", FontWeights.SemiBold, false);
            n.TextTrimming = TextTrimming.CharacterEllipsis; n.VerticalAlignment = VerticalAlignment.Center; n.ToolTip = name + ".exe";
            g.Children.Add(n);
            var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0) };
            track.Children.Add(new Border { Background = UI.B("Line"), CornerRadius = new CornerRadius(3) });
            double frac = max > 0 ? bytes / max : 0;
            var fill = new Border { Background = UI.B("Accent"), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
            track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * frac;
            track.Children.Add(fill);
            Grid.SetColumn(track, 1); g.Children.Add(track);
            var v = UI.Text(Gb(bytes), 12.5, "Sub", wrap: false); v.HorizontalAlignment = HorizontalAlignment.Right; v.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(v, 2); g.Children.Add(v);
            if (CanEnd(name))
            {
                var end = new Button { Style = UI.Res<Style>("ChromeButton"), Content = new TextBlock { Text = "", FontFamily = UI.IconFont, FontSize = 10 }, Width = 28, Height = 24, Margin = new Thickness(8, 0, 0, 0), ToolTip = $"End {Pretty(name)}" };
                string nm = name;
                end.Click += (_, _) =>
                {
                    if (!UI.Confirm($"End {Pretty(nm)}? Unsaved work in it will be lost.")) return;
                    foreach (var p in Process.GetProcessesByName(nm)) { try { p.Kill(); } catch { } finally { p.Dispose(); } }
                    Refresh();
                };
                Grid.SetColumn(end, 3); g.Children.Add(end);
            }
            _list.Children.Add(g);
        }
    }

    static bool CanEnd(string name) =>
        !AppsPage.SystemProcs.Contains(name) && !AppsPage.AntiCheat.Contains(name) && !SelfInstall.IsOurProcess(name)
        && !Games.All.Any(g => g.ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase));

    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Google Chrome", ["msedge"] = "Microsoft Edge", ["firefox"] = "Firefox", ["opera"] = "Opera", ["opera_gx"] = "Opera GX", ["brave"] = "Brave",
        ["Discord"] = "Discord", ["Spotify"] = "Spotify", ["steamwebhelper"] = "Steam (web helper)", ["steam"] = "Steam", ["javaw"] = "Minecraft / Java", ["java"] = "Minecraft / Java",
        ["MsMpEng"] = "Windows Defender", ["dwm"] = "Desktop Window Manager", ["explorer"] = "File Explorer", ["svchost"] = "Windows services",
        ["Memory Compression"] = "Memory compression", ["msedgewebview2"] = "Edge WebView (apps)", ["SearchHost"] = "Windows Search", ["Teams"] = "Teams", ["ms-teams"] = "Teams",
        ["EpicGamesLauncher"] = "Epic Games", ["OneDrive"] = "OneDrive", ["Code"] = "VS Code", ["obs64"] = "OBS Studio", ["RiotClientServices"] = "Riot Client",
    };
    static string Pretty(string n) => Names.TryGetValue(n, out var p) ? p : n;
}
