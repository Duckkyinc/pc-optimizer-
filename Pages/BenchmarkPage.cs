using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Overdrive.Core;

namespace Overdrive.Pages;

public class BenchmarkPage : UserControl
{
    static readonly string[] StageIcons = { "", "", "", "" };
    static readonly string[] StageColors = { "#00E5FF", "#8B5CF6", "#F59E0B", "#2EE59D" };

    readonly StackPanel _hero = new();
    readonly UniformGrid _stages = new() { Columns = 2 };
    readonly StackPanel _history = new();
    readonly StackPanel _improve = new();
    Button _full, _quick;

    public BenchmarkPage()
    {
        var body = new StackPanel();
        body.Children.Add(UI.Header("Benchmark",
            "Run it before and after tweaks to see the real difference."));

        body.Children.Add(UI.Card(_hero));
        body.Children.Add(_improve);
        body.Children.Add(UI.Section("The four tests"));
        body.Children.Add(_stages);
        body.Children.Add(UI.SectionInfo("History", "Runs are only compared with the same test length and resolution. Close other apps and plug in a laptop for a fair test. Under 3% difference is normal noise."));
        body.Children.Add(UI.Card(_history));

        Content = UI.PageScroll(body);
        Render();
    }

    static List<SimRun> Runs => Store.Settings.SimRuns ??= new();

    static SimRun PreviousOf(SimRun r)
    {
        var list = Runs; int i = list.IndexOf(r);
        for (int j = i - 1; j >= 0; j--)
            if (list[j].Mode == r.Mode && list[j].Resolution == r.Resolution && list[j].TestVersion == r.TestVersion) return list[j];
        return null;
    }

    void Render()
    {
        var last = Runs.LastOrDefault();
        var prev = last == null ? null : PreviousOf(last);
        RenderHero(last, prev);
        _ = RenderImprove(last);
        RenderStages(last, prev);
        RenderHistory();
    }

    async Task ApplyBest(SimRun last)
    {
        var byId = new Dictionary<string, Tweak>();
        foreach (var t in Catalog.Visible) byId.TryAdd(t.Id, t);
        var vms = BenchAdvisor.For(last).Where(r => byId.ContainsKey(r.TweakId)).Select(r => (Rec: r, Vm: new TweakVM(byId[r.TweakId]))).ToList();
        foreach (var v in vms) { try { await v.Vm.Ready; } catch { } }
        var todo = vms.Where(v => v.Vm.Available && v.Vm.IsOn != v.Rec.WantOn).ToList();
        if (todo.Count == 0) { UI.Info("All the settings that help this test are already set. Try Auto-tune to check each one on your PC.", "Apply best settings"); return; }
        if (!UI.Confirm("Apply these settings?\n\n" + string.Join("\n", todo.Select(t => "  - " + t.Vm.Title)))) return;
        foreach (var t in todo) { try { await t.Vm.SetAsync(t.Rec.WantOn, quiet: true); } catch { } }
        (Application.Current.MainWindow as MainWindow)?.RefreshAll();
        bool reboot = todo.Any(t => t.Vm.NeedsReboot && t.Vm.IsOn == t.Rec.WantOn);
        UI.Info(reboot ? "Done! Some changes need a restart. Restart your PC, then run the test again." : "Done! Run the same test again to see the difference.", "Apply best settings");
        Render();
    }

    async Task AutoTuneUi(Button b)
    {
        if (Benchmark.Running || AutoTune.Running) return;
        if (!UI.Confirm("Auto-tune runs a quick benchmark about 6 times (about 3 minutes). It tries each big tweak on and off and keeps whichever is faster on your PC.\n\nDon't use the PC while it runs. Start?")) return;
        var main = Window.GetWindow(this);
        main?.Hide();
        List<AutoTune.Result> res = null; string err = null;
        try { res = await AutoTune.RunAsync(); }
        catch (Exception ex) { err = ex.Message; Store.Log("Auto-tune: " + ex); }
        finally { if (main != null) Tray.ShowMain(main); }
        (Application.Current.MainWindow as MainWindow)?.RefreshAll();
        if (err != null) { UI.Info("Auto-tune stopped: " + err, "Auto-tune"); return; }
        if (res == null || res.Count == 0) { UI.Info("Nothing to test: none of the tweaks Auto-tune checks are available on this PC.", "Auto-tune"); return; }
        var lines = res.Select(r => $"{(r.Kept ? "✓" : "✗")}  {r.Title}: {r.Delta:+0.0;-0.0}%  ({(r.FinalOn ? "on" : "off")})");
        UI.Info("Results on your PC:\n\n" + string.Join("\n", lines) + "\n\nEach setting was left in whichever state was faster. Changes under 1.5% count as noise, so those were left as they were.", "Auto-tune");
        Render();
    }

    // ---------------- improve ----------------
    int _improveGen;
    async Task RenderImprove(SimRun last)
    {
        int gen = ++_improveGen;
        _improve.Children.Clear();
        if (last == null) return;
        List<(BenchAdvisor.Rec Rec, TweakVM Vm)> vms;
        List<(string Text, string Button, Action Click)> checks;
        try
        {
            var recs = BenchAdvisor.For(last);
            var byId = new Dictionary<string, Tweak>();
            foreach (var t in Catalog.Visible) byId.TryAdd(t.Id, t);
            vms = recs.Where(r => byId.ContainsKey(r.TweakId)).Select(r => (r, new TweakVM(byId[r.TweakId]))).ToList();
            foreach (var v in vms) { try { await v.Vm.Ready; } catch { } }
            checks = await Task.Run(BenchAdvisor.Checks);
        }
        catch (Exception ex)
        {
            Store.Log("Improve card failed: " + ex);
            _improve.Children.Add(UI.Card(UI.Text("Couldn't check your settings: " + ex.Message, 13, "Sub"), new Thickness(0, 16, 0, 0)));
            return;
        }
        if (gen != _improveGen) return;
        var todo = vms.Where(v => v.Vm.Available && v.Vm.IsOn != v.Rec.WantOn).Take(6).ToList();

        var sp = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(UI.Text("Improve your score", 17, "Text", FontWeights.SemiBold, false));
        titles.Children.Add(UI.Text(todo.Count + checks.Count == 0 ? "Your PC is already set up for max performance." : "Picked from your weakest tests.", 12.5, "Sub"));
        head.Children.Add(titles);
        if (todo.Count > 0)
        {
            Button all = null;
            all = UI.Btn($"Apply all ({todo.Count})", async (s, e) => await UI.Busy(all, async () =>
            {
                foreach (var t in todo) await t.Vm.SetAsync(t.Rec.WantOn, quiet: true);
                (Application.Current.MainWindow as MainWindow)?.RefreshAll();
                bool reboot = todo.Any(t => t.Vm.NeedsReboot && t.Vm.IsOn == t.Rec.WantOn);
                UI.Info(reboot ? "Done! Some changes need a restart. Restart your PC, then run the test again." : "Done! Run the same test again to see the difference.", "Improve your score");
                await RenderImprove(last);
            }), accent: true, icon: "\uE945");
            all.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(all, 1);
            head.Children.Add(all);
        }
        sp.Children.Add(head);

        foreach (var (text, btn, click) in checks)
            sp.Children.Add(ImproveRow("\uE7BA", "#F59E0B", text, null, btn == null ? null : UI.Btn(btn, (s, e) => click())));
        foreach (var (rec, vm) in todo)
        {
            Button b = null;
            b = UI.Btn(rec.WantOn ? "Turn on" : "Turn off", async (s, e) => await UI.Busy(b, async () =>
            {
                await vm.SetAsync(rec.WantOn, quiet: true);
                (Application.Current.MainWindow as MainWindow)?.RefreshAll();
                b.Content = vm.IsOn == rec.WantOn ? "Done" : "Failed"; b.IsEnabled = false;
            }));
            string tag = vm.Tweak.ImpactLabel + (vm.NeedsReboot ? "  ·  RESTART" : "");
            sp.Children.Add(ImproveRow("\uE945", "#00E5FF", vm.Title, rec.Why, b, tag));
        }
        if (todo.Count + checks.Count == 0)
            sp.Children.Add(ImproveRow("\uE73E", "#2EE59D", "All the settings that help this test are already on.", "For more FPS: lower in-game graphics, use DLSS/FSR, or check Pro Lab for stutter causes.", UI.Btn("Open Pro Lab", (s, e) => (Application.Current.MainWindow as MainWindow)?.Navigate("NavLab"))));
        _improve.Children.Add(UI.Card(sp, new Thickness(0, 16, 0, 0)));
    }

    static UIElement ImproveRow(string icon, string color, string title, string why, Button action, string tag = null)
    {
        var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = icon, FontFamily = UI.IconFont, FontSize = 15, Foreground = UI.Hex(color), VerticalAlignment = VerticalAlignment.Center });
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var tr = new StackPanel { Orientation = Orientation.Horizontal };
        tr.Children.Add(UI.Text(title, 13.5, "Text", FontWeights.SemiBold));
        if (tag != null) { var tg = UI.Text(tag.ToUpperInvariant(), 9.5, "Dim", FontWeights.Bold, false); tg.Margin = new Thickness(10, 2, 0, 0); tg.VerticalAlignment = VerticalAlignment.Center; tr.Children.Add(tg); }
        t.Children.Add(tr);
        if (why != null) t.Children.Add(UI.Text(why, 12, "Sub"));
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        if (action != null) { action.VerticalAlignment = VerticalAlignment.Center; action.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(action, 2); g.Children.Add(action); }
        var b = new Border { Child = g, BorderBrush = UI.B("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 0, 0, 0) };
        return g;
    }

    // ---------------- hero ----------------
    void RenderHero(SimRun last, SimRun prev)
    {
        _hero.Children.Clear();
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(UI.Label(last == null ? "YOUR SCORE" : $"LAST SCORE  ·  {last.Mode.ToUpperInvariant()} TEST  ·  {last.Resolution}"));
        var scoreRow = new StackPanel { Orientation = Orientation.Horizontal };
        scoreRow.Children.Add(new TextBlock
        {
            Text = last == null ? "--" : last.Score.ToString("N0"), FontSize = 64, FontWeight = FontWeights.Black,
            Foreground = UI.B("AccentGrad"), FontFamily = UI.Res<FontFamily>("UiDisplay")
        });
        if (last != null && prev != null)
        {
            var d = Delta(last.Score, prev.Score);
            d.VerticalAlignment = VerticalAlignment.Center; d.Margin = new Thickness(16, 8, 0, 0); d.FontSize = 18;
            scoreRow.Children.Add(d);
        }
        left.Children.Add(scoreRow);
        if (last == null)
            left.Children.Add(UI.Text("No runs yet. Your first run sets the baseline, then apply tweaks and run again.", 13, "Sub"));
        else
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            chips.Children.Add(ScoreChip("GPU SCORE", last.GpuScore, prev?.GpuScore, ""));
            chips.Children.Add(ScoreChip("CPU SCORE", last.CpuScore, prev?.CpuScore, ""));
            chips.Children.Add(ScoreChip("SMOOTHNESS", Smoothness(last), prev == null ? null : Smoothness(prev), "", "%"));
            left.Children.Add(chips);
            left.Children.Add(UI.Text($"{last.Gpu}  ·  {last.At:MMM d, h:mm tt}", 12, "Sub"));
        }
        g.Children.Add(left);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 220 };
        _full = UI.Btn("Run full test", async (s, e) => await Run(false), accent: true, icon: "");
        _full.HorizontalAlignment = HorizontalAlignment.Stretch; _full.Padding = new Thickness(22, 12, 22, 12);
        right.Children.Add(_full);
        right.Children.Add(Center(UI.Text("4 tests · about 1 minute", 11.5, "Sub", wrap: false)));
        _quick = UI.Btn("Quick test", async (s, e) => await Run(true), icon: "");
        _quick.HorizontalAlignment = HorizontalAlignment.Stretch; _quick.Margin = new Thickness(0, 12, 0, 0);
        right.Children.Add(_quick);
        right.Children.Add(Center(UI.Text("about 30 seconds", 11.5, "Sub", wrap: false)));
        right.Children.Add(Center(UI.Text("Press ESC any time to stop", 11.5, "Sub", wrap: false)));
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        _hero.Children.Add(g);

        if (last != null && last.CappedAt > 0) _hero.Children.Add(CapWarning(last.CappedAt));
        if (last != null) _hero.Children.Add(Verdict(last, prev));
        if (last != null)
        {
            Button apply = null, tune = null;
            apply = UI.Btn("Apply best settings", async (s, e) => await UI.Busy(apply, () => ApplyBest(last)), accent: true, icon: "\uE945");
            tune = UI.Btn("Auto-tune for my PC", async (s, e) => await AutoTuneUi(tune), icon: "\uE9F5");
            tune.ToolTip = "Tests each big tweak with a quick benchmark and keeps only the ones that make YOUR PC faster. About 3 minutes.";
            var row = UI.Row(apply, tune); row.Margin = new Thickness(0, 12, 0, 0);
            _hero.Children.Add(row);
        }
    }

    static TextBlock Center(TextBlock t) { t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 5, 0, 0); return t; }

    /// <summary>How close 1% lows are to the average, across all tests (100% = perfectly even frames).</summary>
    static double Smoothness(SimRun r) => Math.Round(r.Stages.Where(s => s.Avg > 0).Select(s => s.Low1 / s.Avg).DefaultIfEmpty(0).Average() * 100);

    static UIElement CapWarning(double fps)
    {
        var b = new Border { Background = UI.Hex("#2A1F0A"), BorderBrush = UI.Hex("#F59E0B"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 18, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "\uE7BA", FontFamily = UI.IconFont, Foreground = UI.Hex("#F59E0B"), FontSize = 15, Margin = new Thickness(0, 1, 10, 0) });
        var t = UI.Text($"Your FPS was stuck at about {fps:0} in every test, so something is capping it and the score can't show your PC's real speed. "
            + "Turn off V-Sync and any FPS limit for this test: NVIDIA Control Panel › Vertical sync = Off and Max Frame Rate = Off (or the GPU tab's FPS cap here), "
            + "AMD Adrenalin › Wait for Vertical Refresh = Off and Frame Rate Target Control = Off, and close RivaTuner/Afterburner limiters. Then run it again.", 13);
        t.MaxWidth = 760;
        row.Children.Add(t);
        b.Child = row;
        return b;
    }

    UIElement Verdict(SimRun last, SimRun prev)
    {
        string text;
        if (prev == null) text = "Baseline saved. Apply the settings below, then run the test again to see the difference.";
        else
        {
            double d = (last.Score - prev.Score) / Math.Max(1, prev.Score) * 100;
            text = Math.Abs(d) < 3 ? $"Within {Math.Abs(d):0.0}% of your previous run, which is normal noise. No real change."
                : d > 0 ? $"{d:0.0}% faster than your previous run. Biggest gain: {Biggest(last, prev, true)}."
                : $"{-d:0.0}% slower than your previous run. Biggest drop: {Biggest(last, prev, false)}. Check for background apps, thermal throttling or a laptop on battery.";
        }
        var b = new Border { Background = UI.B("CardHover"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 18, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "", FontFamily = UI.IconFont, Foreground = UI.B("Accent"), FontSize = 15, Margin = new Thickness(0, 1, 10, 0) });
        var t = UI.Text(text, 13); t.MaxWidth = 760;
        row.Children.Add(t);
        b.Child = row;
        return b;
    }

    static string Biggest(SimRun a, SimRun b, bool gain)
    {
        var pairs = a.Stages.Zip(b.Stages, (x, y) => (x.Name, d: (x.Avg - y.Avg) / Math.Max(1, y.Avg) * 100));
        var pick = gain ? pairs.MaxBy(p => p.d) : pairs.MinBy(p => p.d);
        return $"{pick.Name} ({pick.d:+0.0;-0.0}%)";
    }

    static Border ScoreChip(string label, double value, double? prev, string icon, string suffix = "")
    {
        var sp = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new TextBlock { Text = icon, FontFamily = UI.IconFont, FontSize = 11, Foreground = UI.B("Sub"), Margin = new Thickness(0, 1, 6, 0) });
        top.Children.Add(UI.Text(label, 10.5, "Sub", FontWeights.SemiBold, false));
        sp.Children.Add(top);
        var v = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        v.Children.Add(UI.Text(value.ToString("N0") + suffix, 20, "Text", FontWeights.Bold, false));
        if (prev is double p && p > 0) { var d = Delta(value, p); d.Margin = new Thickness(8, 6, 0, 0); v.Children.Add(d); }
        sp.Children.Add(v);
        return new Border { Child = sp, Background = UI.B("CardHover"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 9, 16, 9), Margin = new Thickness(0, 0, 10, 10) };
    }

    static TextBlock Delta(double now, double before)
    {
        double d = (now - before) / Math.Max(0.001, before) * 100;
        string color = Math.Abs(d) < 3 ? "Sub" : d > 0 ? "#2EE59D" : "#F43F5E";
        return new TextBlock
        {
            Text = (d >= 0 ? "▲ " : "▼ ") + Math.Abs(d).ToString("0.0") + "%", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = color.StartsWith('#') ? UI.Hex(color) : UI.B(color)
        };
    }

    // ---------------- stages ----------------
    void RenderStages(SimRun last, SimRun prev)
    {
        _stages.Children.Clear();
        for (int i = 0; i < Benchmark.Stages.Length; i++)
        {
            var st = Benchmark.Stages[i];
            var res = last?.Stages.ElementAtOrDefault(i);
            var pr = prev?.Stages.ElementAtOrDefault(i);
            var accent = UI.Hex(StageColors[i]);

            var sp = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            var ic = new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0),
                Background = new SolidColorBrush(Color.FromArgb(40, ((SolidColorBrush)accent).Color.R, ((SolidColorBrush)accent).Color.G, ((SolidColorBrush)accent).Color.B)),
                Child = new TextBlock { Text = StageIcons[i], FontFamily = UI.IconFont, FontSize = 17, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(UI.Text($"{i + 1}. {st.Name}", 15, "Text", FontWeights.SemiBold, false));
            names.Children.Add(new TextBlock { Text = st.Tests.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = accent });
            Grid.SetColumn(names, 1);
            head.Children.Add(ic); head.Children.Add(names);
            sp.Children.Add(head);
            var desc = UI.Text(st.Description, 12, "Sub"); desc.Margin = new Thickness(0, 10, 0, 12); desc.MinHeight = 34;
            sp.Children.Add(desc);

            var stats = new UniformGrid { Columns = 3 };
            stats.Children.Add(Stat("AVG FPS", res?.Avg, pr?.Avg, big: true));
            stats.Children.Add(Stat("1% LOW", res?.Low1, pr?.Low1));
            stats.Children.Add(Stat("0.1% LOW", res?.Low01, pr?.Low01));
            sp.Children.Add(stats);

            // bar: 1% low as a share of average (how even the frames were)
            if (res != null && res.Avg > 0)
            {
                double ratio = Math.Clamp(res.Low1 / res.Avg, 0, 1);
                var track = new Grid { Height = 6, Margin = new Thickness(0, 12, 0, 4) };
                track.Children.Add(new Border { Background = UI.B("Line"), CornerRadius = new CornerRadius(3) });
                var fill = new Border { Background = accent, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
                track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * ratio;
                track.Children.Add(fill);
                sp.Children.Add(track);
                sp.Children.Add(UI.Text($"Frame pacing {ratio * 100:0}%  ·  {res.FrameMs:0.00} ms per frame  ·  {res.Frames:N0} frames", 11, "Sub", wrap: false));
            }
            _stages.Children.Add(UI.Card(sp, new Thickness(0, 0, i % 2 == 0 ? 12 : 0, 12)));
        }
    }

    static StackPanel Stat(string label, double? v, double? prev, bool big = false)
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.Text(label, 10.5, "Sub", FontWeights.SemiBold, false));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(UI.Text(v is double x ? x.ToString(x >= 100 ? "N0" : "0.0") : "--", big ? 24 : 18, "Text", FontWeights.Bold, false));
        if (v is double a && prev is double p && p > 0) { var d = Delta(a, p); d.FontSize = 10.5; d.Margin = new Thickness(6, 0, 0, 3); d.VerticalAlignment = VerticalAlignment.Bottom; row.Children.Add(d); }
        sp.Children.Add(row);
        return sp;
    }

    // ---------------- history ----------------
    void RenderHistory()
    {
        _history.Children.Clear();
        var runs = Runs;
        if (runs.Count == 0) { _history.Children.Add(UI.Text("Your runs will show up here so you can compare before and after tweaks.", 13, "Sub")); return; }
        double max = runs.Max(r => r.Score);
        foreach (var r in Enumerable.Reverse(runs).Take(12))
        {
            var prev = PreviousOf(r);
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            var when = UI.Text(r.At.ToString("MMM d, h:mm tt"), 12.5, "Text", wrap: false);
            var mode = UI.Text($"{r.Mode} · {r.Resolution}", 12, "Sub", wrap: false);
            var track = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            track.Children.Add(new Border { Background = UI.B("Line"), CornerRadius = new CornerRadius(4) });
            var fill = new Border { Background = UI.B("AccentGrad"), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
            double frac = max > 0 ? r.Score / max : 0;
            track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * frac;
            track.Children.Add(fill);
            var score = UI.Text(r.Score.ToString("N0"), 14, "Text", FontWeights.Bold, false);
            score.HorizontalAlignment = HorizontalAlignment.Right;
            UIElement delta = prev == null ? UI.Text(r.TestVersion < Benchmark.TestVersion ? "old test" : "baseline", 11, "Sub", wrap: false) : Delta(r.Score, prev.Score);
            ((FrameworkElement)delta).HorizontalAlignment = HorizontalAlignment.Right;
            ((FrameworkElement)delta).VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(mode, 1); Grid.SetColumn(track, 2); Grid.SetColumn(score, 3); Grid.SetColumn(delta, 4);
            foreach (FrameworkElement e in new FrameworkElement[] { when, mode, score }) e.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(when); g.Children.Add(mode); g.Children.Add(track); g.Children.Add(score); g.Children.Add(delta);
            _history.Children.Add(g);
        }
        var clear = UI.Btn("Clear history", (s, e) =>
        {
            if (!UI.Confirm("Delete all saved benchmark runs?")) return;
            Runs.Clear(); Store.SaveSettings(); Render();
        });
        clear.HorizontalAlignment = HorizontalAlignment.Left; clear.Margin = new Thickness(0, 12, 0, 0);
        _history.Children.Add(clear);
    }

    // ---------------- run ----------------
    async Task Run(bool quick)
    {
        if (Benchmark.Running) return;
        _full.IsEnabled = _quick.IsEnabled = false;
        var main = Window.GetWindow(this);
        bool overlay = Store.Settings.FpsOverlay;
        if (overlay) LiveMonitor.SetOverlay(false);
        main?.Hide();
        try
        {
            var (run, error) = await Benchmark.RunAsync(quick);
            if (main != null) Tray.ShowMain(main);
            if (run != null)
            {
                Runs.Add(run);
                while (Runs.Count > 30) Runs.RemoveAt(0);
                Store.SaveSettings();
                Render();
            }
            else if (error != null)
                UI.Info("The benchmark couldn't start on this PC:\n\n" + error + "\n\nMake sure your graphics driver is installed and up to date.", "Benchmark");
        }
        finally
        {
            if (overlay) LiveMonitor.SetOverlay(true);
            if (main != null && !main.IsVisible) Tray.ShowMain(main);
            _full.IsEnabled = _quick.IsEnabled = true;
        }
    }
}

/// <summary>Turns a benchmark result into concrete settings to change, based on which tests were weak.</summary>
static class BenchAdvisor
{
    public record Rec(string TweakId, bool WantOn, string Why);

    static bool Ryzen => SysInfo.CpuName?.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) == true;

    public static List<Rec> For(SimRun r)
    {
        var list = new List<Rec>();
        void Add(string id, string why, bool on = true) { if (!list.Any(x => x.TweakId == id)) list.Add(new Rec(id, on, why)); }

        double gpuAvg = r.Stages.Take(3).Select(s => s.Avg).DefaultIfEmpty(0).Average();
        var cpu = r.Stages.ElementAtOrDefault(3);
        double smooth = r.Stages.Where(s => s.Avg > 0).Select(s => s.Low1 / s.Avg).DefaultIfEmpty(1).Average();
        var weakest = r.Stages.OrderBy(s => s.Avg > 0 ? s.Low1 / s.Avg : 1).FirstOrDefault();

        if (r.CappedAt > 0)
        {
            Add("nv.fpscap", "An FPS cap was limiting every test.", on: false);
            Add("nv.vsync", "V-Sync was holding your FPS at your refresh rate.");
        }
        // Power first: it affects every test
        Add("win.powerplan", "Keeps your CPU at full speed. Helps every test.");
        Add("nv.power", "Stops your GPU from downclocking mid-game. Helps Neon City, Fractal Core and Galaxy Storm.");
        Add("amd.ulps", "Stops your Radeon from dropping into deep sleep between frames.");
        Add("amd.deepsleep", "Keeps your Radeon's clocks steady. Helps Neon City, Fractal Core and Galaxy Storm.");
        Add("amd.tessellation", "Caps wasted geometry detail. Big gain in games that overdo it.");
        Add("amd.shadercache", "Fewer stutters when loading new effects.");

        // CPU / driver bound (Draw Call Rush)
        if (cpu != null && cpu.Avg > 0 && cpu.Avg < gpuAvg)
        {
            if (!Ryzen) Add("win.cstates", "Stops CPU cores from sleeping. Helps Draw Call Rush and 1% lows.");
            Add("win.vbs", "Removes a security layer that slows CPU-heavy games by up to 10%.");
        }
        // GPU bound
        Add("win.hags", "Lets the GPU manage its own memory. Small FPS gain on newer cards.");
        Add("nv.shadercache", "Bigger shader cache = fewer stutters when loading new effects.");

        // Smoothness
        if (smooth < 0.7)
        {
            if (!Ryzen) Add("win.cstates", $"Your 1% lows were uneven{(weakest != null ? $" (worst in {weakest.Name})" : "")}. Keeping cores awake smooths frame times.");
            Add("win.timerres", "Faster Windows timer for more even frame pacing.");
            Add("win.usbsuspend", "Stops USB devices from sleeping, which can cause hitches.");
        }
        Add("win.gamedvr", "Background recording costs a few FPS in every game.");
        return list;
    }

    /// <summary>Non-tweak issues worth fixing right now (battery, busy background apps).</summary>
    public static List<(string Text, string Button, Action Click)> Checks()
    {
        var res = new List<(string, string, Action)>();
        try
        {
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            if (ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline)
                res.Add(("Your laptop is on battery. Plug in your charger: on battery, CPU and GPU speed drop a lot.", null, null));
        }
        catch { }
        try
        {
            var a = ProcSampler.Snapshot().ToDictionary(x => x.Pid);
            Thread.Sleep(600);
            var b = ProcSampler.Snapshot();
            var busy = b.Where(x => x.Pid > 4 && a.TryGetValue(x.Pid, out var o) && o.Name == x.Name)
                        .Select(x => (x.Name, Cpu: (x.CpuTicks - a[x.Pid].CpuTicks) / 10000.0 / 600 * 100))
                        .Where(x => x.Cpu > 8 && !AppsPage.SystemProcs.Contains(x.Name) && !SelfInstall.IsOurProcess(x.Name))
                        .GroupBy(x => x.Name).Select(g => (Name: g.Key, Cpu: g.Sum(v => v.Cpu)))
                        .OrderByDescending(x => x.Cpu).Take(3).ToList();
            foreach (var (name, c) in busy)
                res.Add(($"{name} is using {c:0}% of a CPU core in the background.", "Open Apps", () => (Application.Current.MainWindow as MainWindow)?.Navigate("NavApps")));
        }
        catch { }
        return res;
    }
}
