using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Overdrive.Core;

namespace Overdrive.Pages;

public class DashboardPage : UserControl, IRefreshable
{
    readonly MainWindow _main;
    readonly ProgressBar _cpuBar = new() { Style = UI.Res<Style>("Meter") };
    readonly ProgressBar _ramBar = new() { Style = UI.Res<Style>("Meter") };
    readonly TextBlock _cpuText = UI.Text("—", 12, "Sub");
    readonly TextBlock _ramText = UI.Text("—", 12, "Sub");
    readonly TextBlock _score = new() { FontSize = 44, FontWeight = FontWeights.Black, Foreground = UI.B("AccentGrad") };
    readonly TextBlock _scoreSub = UI.Text("", 13, "Sub");
    readonly Arc _arc = new();
    readonly StackPanel _alerts = new();
    readonly StackPanel _displayBox = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    List<TweakVM> _recommended;

    public DashboardPage(MainWindow main)
    {
        _main = main;
        var body = new StackPanel();

        // ---------- hero ----------
        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(UI.Label("PC OPTIMIZER"));
        left.Children.Add(new TextBlock { Text = "Let's make your PC fast.", Style = UI.Res<Style>("H1"), FontSize = 34 });
        var intro = UI.Text("Real tweaks only. One click to boost, one click to undo.", 14, "Sub");
        intro.Margin = new Thickness(0, 8, 0, 20); intro.MaxWidth = 560; intro.HorizontalAlignment = HorizontalAlignment.Left;
        left.Children.Add(intro);
        Button boost = null;
        boost = UI.Btn("BEST SETTINGS FOR MY PC", async (s, e) => await PlanWindow.ShowFor(_main, () => { _main.RefreshAll(); UpdateScore(); }), accent: true, icon: "\uE945");
        boost.Padding = new Thickness(26, 13, 26, 13);
        left.Children.Add(UI.Row(boost,
            UI.Btn("Pick a game", (s, e) => _main.Navigate("NavGames"), icon: "")));
        hero.Children.Add(left);

        var ring = new Grid { Width = 170, Height = 170, Margin = new Thickness(20, 0, 10, 0) };
        ring.Children.Add(new Ellipse { Stroke = UI.Hex("#1E2433"), StrokeThickness = 12 });
        ring.Children.Add(_arc);
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _score.HorizontalAlignment = HorizontalAlignment.Center;
        mid.Children.Add(_score);
        var lbl = UI.Label("OPTIMIZED"); lbl.HorizontalAlignment = HorizontalAlignment.Center; lbl.Margin = new Thickness(0, -4, 0, 0);
        mid.Children.Add(lbl);
        ring.Children.Add(mid);
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(ring);
        _scoreSub.HorizontalAlignment = HorizontalAlignment.Center; _scoreSub.Margin = new Thickness(0, 10, 0, 0);
        _scoreSub.TextAlignment = TextAlignment.Center; _scoreSub.MaxWidth = 210;
        right.Children.Add(_scoreSub);
        Grid.SetColumn(right, 1);
        hero.Children.Add(right);

        var heroCard = UI.Card(hero, background: UI.B("HeroGrad"));
        heroCard.Padding = new Thickness(32, 28, 32, 28);
        heroCard.Margin = new Thickness(0, 0, 0, 18);
        body.Children.Add(heroCard);

        if (LowEnd.IsLowEndPc)
        {
            var le = new Grid();
            le.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            le.ColumnDefinitions.Add(new ColumnDefinition());
            le.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            le.Children.Add(new TextBlock { Text = "\uE945", FontFamily = UI.IconFont, FontSize = 20, Foreground = UI.Hex("#2EE59D"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
            var lt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            lt.Children.Add(UI.Text("Low-end PC boost", 15, "Text", FontWeights.SemiBold, false));
            lt.Children.Add(UI.Text($"Detected {LowEnd.Why()}. During games it pauses Windows background work and frees RAM.", 12, "Sub"));
            Grid.SetColumn(lt, 1); le.Children.Add(lt);
            var lr = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button free = null;
            free = UI.Btn("Free RAM now", async (s, e) => await UI.Busy(free, async () =>
            {
                double mb = await Task.Run(LowEnd.FreeStandbyRam);
                free.Content = mb > 1 ? $"Freed {mb:0} MB" : "Already free";
            }), icon: "\uE964");
            var sw = new CheckBox { Style = UI.Res<Style>("Switch"), IsChecked = LowEnd.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            sw.Checked += (_, _) => { Store.Settings.LowEndMode = true; Store.SaveSettings(); };
            sw.Unchecked += (_, _) => { Store.Settings.LowEndMode = false; Store.SaveSettings(); };
            lr.Children.Add(free); lr.Children.Add(sw);
            Grid.SetColumn(lr, 2); le.Children.Add(lr);
            var leCard = UI.Card(le, new Thickness(0, 0, 0, 18));
            leCard.BorderBrush = UI.Hex("#2EE59D"); leCard.BorderThickness = new Thickness(1);
            body.Children.Add(leCard);
        }

        // ---------- presets ----------
        body.Children.Add(UI.Section("One-click presets"));
        var presets = new UniformGrid { Columns = 3 };
        foreach (var pr in Catalog.Presets)
        {
            var sp = new StackPanel();
            sp.Children.Add(UI.Row(new TextBlock { Text = pr.Icon, FontFamily = UI.IconFont, FontSize = 18, Foreground = UI.B("Accent"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center },
                                   UI.Text(pr.Name, 16, "Text", FontWeights.Bold, false)));
            var blurb = UI.Text(pr.Blurb, 12, "Sub"); blurb.Margin = new Thickness(0, 8, 0, 12); blurb.MinHeight = 48;
            sp.Children.Add(blurb);
            Button b = null;
            b = UI.Btn("Apply " + pr.Name, async (s, e) => await ApplyPreset(pr, b), accent: pr.Id == "competitive");
            b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0);
            sp.Children.Add(b);
            presets.Children.Add(UI.Card(sp, new Thickness(0, 0, 12, 12)));
        }
        body.Children.Add(presets);

        // ---------- alerts ----------
        body.Children.Add(_alerts);

        // ---------- system ----------
        body.Children.Add(UI.Section("Your PC"));
        var grid = new UniformGrid { Columns = 4 };
        var gpu = SysInfo.PrimaryGpu;
        var cpuCard = Stat("", "PROCESSOR", SysInfo.CpuName, $"{SysInfo.Threads} threads", _cpuBar, _cpuText);
        Clickable(cpuCard, "See what\'s using your CPU");
        grid.Children.Add(cpuCard);
        var gpuBadge = SysInfo.ShowNvidia ? UI.Badge("NVIDIA", "#76B900", "#15200A") : SysInfo.ShowAmd ? UI.Badge("AMD", "#FF4A50", "#2A0E10") : null;
        grid.Children.Add(Stat("", "GRAPHICS", gpu?.Name ?? "Unknown", SysInfo.Gpus.Count > 1 ? $"+ {SysInfo.Gpus.Count - 1} more GPU" : "Driver " + (SysInfo.ShowNvidia ? gpu?.NvidiaDriverVersion : gpu?.DriverVersion), null, null, gpuBadge));
        var ramCard = Stat("", "MEMORY", $"{Math.Round(SysInfo.RamGb)} GB RAM", "", _ramBar, _ramText);
        Clickable(ramCard, "See what\'s using your RAM");
        grid.Children.Add(ramCard);
        grid.Children.Add(StatCustom("", "DISPLAY", _displayBox));
        body.Children.Add(grid);

        Content = UI.PageScroll(body);

        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => { _timer.Start(); Tick(); };
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else _timer.Stop(); }; // stops when hidden to tray
        TweakVM.AnyChanged += () => Dispatcher.BeginInvoke(UpdateScore);

        Refresh();
    }

    void Clickable(Border card, string tip)
    {
        card.Cursor = System.Windows.Input.Cursors.Hand;
        card.ToolTip = tip;
        card.MouseEnter += (_, _) => card.BorderBrush = UI.B("Accent");
        card.MouseLeave += (_, _) => card.BorderBrush = UI.B("Line");
        card.MouseLeftButtonUp += (_, _) => { if (tip.Contains("RAM")) RamWindow.Open(_main); else _main.Navigate("NavApps"); };
    }

    static Border Stat(string icon, string label, string title, string sub, ProgressBar bar, TextBlock live, UIElement badge = null)
    {
        var sp = new StackPanel();
        var top = UI.Row(new TextBlock { Text = icon, FontFamily = UI.IconFont, FontSize = 16, Foreground = UI.B("Accent"), Margin = new Thickness(0, 0, 10, 0) }, UI.Label(label));
        if (badge != null) { ((FrameworkElement)badge).Margin = new Thickness(8, -4, 0, 0); top.Children.Add(badge); }
        sp.Children.Add(top);
        var t = UI.Text(title, 15, "Text", FontWeights.SemiBold); t.Margin = new Thickness(0, 4, 0, 2);
        sp.Children.Add(t);
        if (!string.IsNullOrEmpty(sub)) sp.Children.Add(UI.Text(sub, 12, "Sub"));
        if (bar != null) { bar.Margin = new Thickness(0, 12, 0, 6); sp.Children.Add(bar); sp.Children.Add(live); }
        return UI.Card(sp, new Thickness(0, 0, 12, 12));
    }

    static Border StatCustom(string icon, string label, StackPanel inner)
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.Row(new TextBlock { Text = icon, FontFamily = UI.IconFont, FontSize = 16, Foreground = UI.B("Accent"), Margin = new Thickness(0, 0, 10, 0) }, UI.Label(label)));
        sp.Children.Add(inner);
        return UI.Card(sp, new Thickness(0, 0, 0, 12));
    }

    void Tick()
    {
        var cpu = SysInfo.CpuUsage();
        _cpuBar.Value = cpu; _cpuText.Text = $"{cpu:0}% in use";
        var (used, pct) = SysInfo.RamUsage();
        _ramBar.Value = pct; _ramText.Text = $"{used:0.0} GB in use ({pct:0}%)";
    }

    public async void Refresh()
    {
        if (_recommended != null) UpdateScore(); // already built: just re-check in the background
        else
        {
            _recommended = await Task.Run(() => Catalog.RecommendedForThisPc().ToList()).ContinueWith(t => t.Result.Select(x => new TweakVM(x)).ToList(), TaskScheduler.FromCurrentSynchronizationContext());
            await Task.WhenAll(_recommended.Select(r => r.Ready));
            ShowScore();
        }

        // gather slow system info in the background, then draw
        var info = await Task.Run(() =>
        {
            Display.Info d = null; bool wifi = false, hvci = false, nvOk = true;
            try { d = Display.GetPrimary(); } catch { }
            try { wifi = NetLab.OnWifi(); } catch { }
            try
            {
                var v = Reg.Get(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
                hvci = v != null && Convert.ToInt32(v) == 1;
            }
            catch { }
            try { nvOk = !SysInfo.ShowNvidia || Nvapi.Available; } catch { }
            return (d, wifi, hvci, nvOk);
        });
        BuildDisplay(info.d);
        BuildAlerts(info.d, info.wifi, info.hvci, info.nvOk);
    }

    bool _scoring;
    async void UpdateScore()
    {
        if (_recommended == null || _scoring) return;
        _scoring = true;
        try { await Task.WhenAll(_recommended.Select(r => r.RefreshAsync())); ShowScore(); }
        finally { _scoring = false; }
    }

    void ShowScore()
    {
        int on = _recommended.Count(r => r.IsOn), total = Math.Max(1, _recommended.Count);
        int pct = (int)Math.Round(100.0 * on / total);
        _score.Text = $"{pct}%";
        _arc.Progress = pct / 100.0;
        _scoreSub.Text = pct >= 100 ? "Every recommended tweak is on. Now pick your game!" : $"{on} of {_recommended.Count} recommended tweaks are on";
    }

    void BuildDisplay(Display.Info d)
    {
        _displayBox.Children.Clear();
        if (d == null) { _displayBox.Children.Add(UI.Text("Unknown", 15, "Text", FontWeights.SemiBold)); return; }
        var t = UI.Text($"{d.Width} x {d.Height}", 15, "Text", FontWeights.SemiBold); t.Margin = new Thickness(0, 4, 0, 2);
        _displayBox.Children.Add(t);
        _displayBox.Children.Add(UI.Text($"{d.CurrentHz} Hz" + (d.MaxHz > d.CurrentHz ? $" (supports {d.MaxHz} Hz!)" : " (max)"), 12, d.MaxHz > d.CurrentHz ? "Warn" : "Sub"));
    }

    void BuildAlerts(Display.Info d, bool wifi, bool hvci, bool nvOk)
    {
        _alerts.Children.Clear();

        if (d != null && d.MaxHz > d.CurrentHz)
            Alert("\uE7F8", $"Your monitor can do {d.MaxHz}Hz, but Windows has it set to {d.CurrentHz}Hz",
                "This is the biggest free upgrade there is. Your screen can show more frames per second than it does now.",
                "Fix it", (s, e) =>
                {
                    if (Display.SetRefresh(d.MaxHz)) { UI.Info($"Done! Your display now runs at {d.MaxHz}Hz."); Refresh(); }
                    else UI.Info("Windows didn't accept that refresh rate. Try Settings > System > Display > Advanced display. A better cable (DisplayPort or HDMI 2.0+) may also be needed.");
                }, "Warn");

        if (SysInfo.HasBattery && SysInfo.OnBattery)
            Alert("\uE83F", "You're running on battery", "Laptops cut CPU and GPU speed by a lot on battery. Plug in your charger before you play.", null, null, "Warn");

        if (wifi)
            Alert("\uE701", "You're on Wi-Fi", "Wi-Fi adds ping spikes and packet loss that no software can fix. An Ethernet cable is the #1 real 'ping reducer'.",
                "Test my connection", (s, e) => _main.Navigate("NavNetwork"), "Accent");

        if (hvci)
            Alert("\uE72E", "Memory Integrity is on", "This Windows security feature can cost around 5-10% FPS in some games. Read about the trade-off in the Windows tab before you turn it off.",
                "Show me", (s, e) => _main.Navigate("NavWindows"), "Purple");

        if (!nvOk)
            Alert("\uE7F4", "NVIDIA driver not found", "Install the latest NVIDIA driver to unlock the NVIDIA tweaks and get the best FPS.",
                "Get driver", (s, e) => Shell.Open("https://www.nvidia.com/en-us/drivers/"), "Warn");
    }

    void Alert(string icon, string title, string text, string action, RoutedEventHandler click, string color)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = icon, FontFamily = UI.IconFont, FontSize = 20, Foreground = UI.B(color), VerticalAlignment = VerticalAlignment.Center });
        var sp = new StackPanel();
        sp.Children.Add(UI.Text(title, 14, "Text", FontWeights.SemiBold));
        sp.Children.Add(UI.Text(text, 12, "Sub"));
        Grid.SetColumn(sp, 1); g.Children.Add(sp);
        if (action != null)
        {
            var b = UI.Btn(action, click); b.Margin = new Thickness(16, 0, 0, 0); b.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(b, 2); g.Children.Add(b);
        }
        var card = UI.Card(g); card.BorderBrush = UI.B(color); card.Padding = new Thickness(16, 14, 16, 14);
        _alerts.Children.Add(card);
    }

    async Task ApplyPreset(Catalog.Preset pr, Button b)
    {
        var on = await Task.Run(() => pr.TurnOn().Select(t => new TweakVM(t)).ToList());
        var off = await Task.Run(() => pr.TurnOff().Select(t => new TweakVM(t)).ToList());
        await Task.WhenAll(on.Concat(off).Select(v => v.Ready));
        var toOn = on.Where(v => v.Available && !v.IsOn).ToList();
        var toOff = off.Where(v => v.Available && v.IsOn).ToList();
        if (toOn.Count + toOff.Count == 0) { UI.Info($"Your PC is already set up for {pr.Name}."); return; }

        var msg = $"Apply the {pr.Name} preset?\n" +
                  (toOn.Count > 0 ? "\nTurn ON:\n" + string.Join("\n", toOn.Select(t => "  • " + t.Title)) : "") +
                  (toOff.Count > 0 ? "\n\nTurn OFF:\n" + string.Join("\n", toOff.Select(t => "  • " + t.Title)) : "");
        if (!UI.Confirm(msg, pr.Name + " preset")) return;
        await UI.Busy(b, async () =>
        {
            foreach (var t in toOn) await t.SetAsync(true, quiet: true);
            foreach (var t in toOff) await t.SetAsync(false, quiet: true);
            _main.RefreshAll();
            UpdateScore();
            var failed = toOn.Where(t => !t.IsOn).Concat(toOff.Where(t => t.IsOn)).ToList();
            var reboot = toOn.Concat(toOff).Any(t => t.NeedsReboot);
            UI.Info($"Done! {pr.Name} preset applied." + (failed.Count > 0 ? "\n\nThese didn't work on this PC:\n" + string.Join("\n", failed.Select(f => "  • " + f.Title)) : "") +
                    (reboot ? "\n\nRestart your PC to finish some of them." : ""));
        });
    }

    async Task BoostAll(Button b)
    {
        if (_recommended == null) { UI.Info("Still checking your PC, try again in a second."); return; }
        var todo = _recommended.Where(r => r.Available && !r.IsOn).ToList();
        if (todo.Count == 0) { UI.Info("Everything recommended is already on. Check the Games tab for per-game boosts!"); return; }

        var msg = "PC Optimizer will turn on these recommended tweaks:\n\n" + string.Join("\n", todo.Select(t => "  - " + t.Title)) +
                  "\n\nCreate a Windows restore point first? (Recommended. It takes about 30 seconds.)";
        var answer = Overdrive.Pages.Dialog.Show(msg, "Boost my PC", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;

        await UI.Busy(b, async () =>
        {
            if (answer == MessageBoxResult.Yes)
            {
                try { await Task.Run(() => RestorePoint.Create("PC Optimizer - before Boost my PC")); }
                catch (Exception ex)
                {
                    if (!UI.Confirm("Couldn't create a restore point:\n" + ex.Message + "\n\nContinue anyway? (PC Optimizer still keeps its own backup of every change.)")) return;
                }
            }
            foreach (var t in todo) await t.SetAsync(true, quiet: true);
            _main.RefreshAll();
            UpdateScore();
            var failed = todo.Where(t => !t.IsOn).ToList();
            var reboot = todo.Any(t => t.IsOn && t.NeedsReboot);
            UI.Info($"Boosted! {todo.Count - failed.Count} tweaks turned on." +
                    (failed.Count > 0 ? "\n\nThese didn't work on this PC:\n" + string.Join("\n", failed.Select(f => "  - " + f.Title)) : "") +
                    (reboot ? "\n\nRestart your PC to finish some of them." : "") +
                    "\n\nNext: open Games and boost the games you play.");
        });
    }
}

/// <summary>Circular progress ring.</summary>
public class Arc : Shape
{
    double _progress;
    public double Progress { get => _progress; set { _progress = Math.Clamp(value, 0, 1); InvalidateVisual(); } }

    public Arc()
    {
        Stroke = UI.B("AccentGrad");
        StrokeThickness = 12;
        StrokeStartLineCap = PenLineCap.Round;
        StrokeEndLineCap = PenLineCap.Round;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    protected override Geometry DefiningGeometry
    {
        get
        {
            double w = ActualWidth, h = ActualHeight;
            if (double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0) return Geometry.Empty; // not laid out yet
            double r = Math.Min(w, h) / 2 - StrokeThickness / 2;
            if (r <= 1 || _progress <= 0) return Geometry.Empty;
            var c = new Point(w / 2, h / 2);
            if (_progress >= 0.9999) return new EllipseGeometry(c, r, r);
            double angle = _progress * 360;
            var start = new Point(c.X, c.Y - r);
            double rad = (angle - 90) * Math.PI / 180;
            var end = new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true));
            var g = new PathGeometry(); g.Figures.Add(fig);
            return g;
        }
    }
}
