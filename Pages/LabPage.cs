using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Overdrive.Core;

namespace Overdrive.Pages;

/// <summary>Advanced diagnostics: Stutter Finder, Driver Latency Check and Mouse Polling Rate test.</summary>
public class LabPage : UserControl
{
    public LabPage()
    {
        var body = new StackPanel();
        body.Children.Add(UI.Header("Pro Lab",
            "Find what's really causing stutter and input lag."));
        body.Children.Add(new StutterCard());
        body.Children.Add(new LatencyCard());
        body.Children.Add(new PollingCard());
        Content = UI.PageScroll(body);
    }

    internal static UIElement CardHead(string icon, string color, string title, string tag, string desc)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var c = ((SolidColorBrush)UI.Hex(color)).Color;
        g.Children.Add(new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(38, c.R, c.G, c.B)),
            Child = new TextBlock { Text = icon, FontFamily = UI.IconFont, FontSize = 20, Foreground = UI.Hex(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        var sp = new StackPanel();
        var tr = new StackPanel { Orientation = Orientation.Horizontal };
        tr.Children.Add(UI.Text(title, 17, "Text", FontWeights.SemiBold, false));
        var b = UI.Badge(tag, color, "#1A1F2B"); b.Margin = new Thickness(10, 0, 0, 0); b.VerticalAlignment = VerticalAlignment.Center;
        tr.Children.Add(b);
        sp.Children.Add(tr);
        var d = UI.Text(desc, 12.5, "Sub"); d.Margin = new Thickness(0, 4, 0, 0); d.MaxWidth = 820; d.HorizontalAlignment = HorizontalAlignment.Left;
        sp.Children.Add(d);
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        return g;
    }

    internal static Border StatBox(string label, string value, string color = "Text")
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.Text(label, 10.5, "Sub", FontWeights.SemiBold, false));
        var v = UI.Text(value, 20, "Text", FontWeights.Bold, false);
        if (color.StartsWith('#')) v.Foreground = UI.Hex(color);
        sp.Children.Add(v);
        return new Border { Child = sp, Background = UI.B("CardHover"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 9, 16, 9), Margin = new Thickness(0, 0, 10, 10) };
    }

    internal static StackPanel Chips(string group, (string Label, int Value)[] items, int selected, Action<int> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (label, value) in items)
        {
            var rb = new RadioButton { Style = UI.Res<Style>("Chip"), Content = label, GroupName = group, IsChecked = value == selected };
            rb.Checked += (_, _) => pick(value);
            row.Children.Add(rb);
        }
        return row;
    }
}

// =====================================================================================================
class StutterCard : Border
{
    int _seconds = 120;
    readonly Button _start;
    readonly TextBlock _status = UI.Text("", 13, "Sub");
    readonly StackPanel _results = new();

    public StutterCard()
    {
        Style = UI.Res<Style>("CardBorder");
        Margin = new Thickness(0, 0, 0, 16);
        var sp = new StackPanel();
        sp.Children.Add(LabPage.CardHead("", "#F43F5E", "Stutter Finder", "EXCLUSIVE",
            "Play normally. It finds which apps cause your stutters."));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(LabPage.Chips("stutterlen", new[] { ("1 min", 60), ("2 min", 120), ("5 min", 300) }, _seconds, v => _seconds = v));
        _start = UI.Btn("Start recording", (s, e) => Toggle(), accent: true, icon: "");
        _start.Margin = new Thickness(14, 0, 0, 0);
        row.Children.Add(_start);
        sp.Children.Add(row);
        _status.Margin = new Thickness(0, 10, 0, 0);
        sp.Children.Add(_status);
        sp.Children.Add(_results);
        Child = sp;

        StutterFinder.StatusChanged += OnStatus;
        StutterFinder.Finished += OnFinished;
        Unloaded += (_, _) => { };
        OnStatus();
    }

    void Toggle()
    {
        if (StutterFinder.Active) { StutterFinder.Cancel(); return; }
        _results.Children.Clear();
        StutterFinder.Start(_seconds);
        OnStatus();
    }

    void OnStatus() => Dispatcher.BeginInvoke(() =>
    {
        _status.Text = StutterFinder.Active ? StutterFinder.Status : StutterFinder.Status == "Done." ? "" : StutterFinder.Status;
        if (!StutterFinder.Active && string.IsNullOrEmpty(_status.Text))
            _status.Text = "Press Start, then open your game.";
        _start.Content = StutterFinder.Active ? "Stop" : "Start recording";
    });

    void OnFinished(StutterFinder.Report r)
    {
        Tray.Notify("Stutter report ready", $"{r.Spikes.Count} stutters found in {r.Game}. Open PC Optimizer › Pro Lab to see what caused them.");
        Dispatcher.BeginInvoke(() => Show(r));
    }

    void Show(StutterFinder.Report r)
    {
        _results.Children.Clear();
        _results.Margin = new Thickness(0, 16, 0, 0);
        _results.Children.Add(UI.Label($"REPORT  ·  {r.Game.ToUpperInvariant()}  ·  {r.Seconds:0} SECONDS"));
        var stats = new WrapPanel();
        double perMin = r.Spikes.Count / Math.Max(0.1, r.Seconds / 60);
        stats.Children.Add(LabPage.StatBox("AVG FPS", r.AvgFps.ToString("0")));
        stats.Children.Add(LabPage.StatBox("TYPICAL FRAME", $"{r.MedianMs:0.0} ms"));
        stats.Children.Add(LabPage.StatBox("WORST FRAME", $"{r.WorstMs:0} ms", r.WorstMs > 50 ? "#F43F5E" : "Text"));
        stats.Children.Add(LabPage.StatBox("STUTTERS", $"{r.Spikes.Count}  ({perMin:0.#}/min)", perMin > 6 ? "#F43F5E" : perMin > 2 ? "#F59E0B" : "#2EE59D"));
        _results.Children.Add(stats);

        // timeline
        var canvas = new Canvas { Height = 90, Background = UI.Hex("#0E1118"), ClipToBounds = true, Margin = new Thickness(0, 4, 0, 4) };
        canvas.SizeChanged += (_, _) => DrawTimeline(canvas, r);
        _results.Children.Add(new Border { Child = canvas, CornerRadius = new CornerRadius(8), ClipToBounds = true });
        _results.Children.Add(UI.Text("Frame times over the recording. Red bars are stutters.", 11, "Sub"));

        _results.Children.Add(UI.Section("Most likely causes"));
        if (r.Spikes.Count == 0)
            _results.Children.Add(UI.Tip("No stutters found. Your frame times were smooth the whole time.", "\uE73E", "Good"));
        foreach (var s in r.Suspects.Take(8)) _results.Children.Add(SuspectRow(s, r.Spikes.Count));
        if (r.Unexplained > 0)
        {
            var t = UI.Tip($"{r.Unexplained} stutter(s) had no program using extra CPU or disk at that moment. Those usually come from drivers (Wi-Fi, GPU, audio, USB), the GPU itself, or network hiccups. Run the Driver Latency Check below while the game is open to find a bad driver.", "\uE7BA", "Warn");
            _results.Children.Add(t);
        }
    }

    UIElement SuspectRow(StutterFinder.Suspect s, int total)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(UI.Text(s.IsGame ? $"{s.Name}  (the game itself)" : s.Name, 14, "Text", FontWeights.SemiBold, false));
        string what = s.AvgIoMb > 8 && s.AvgIoMb * 2 > s.AvgCpu ? $"used ~{s.AvgIoMb:0} MB of disk" : $"jumped to ~{s.AvgCpu:0}% of a CPU core";
        string tip = s.IsGame ? "Usually shader compiling or loading. Let shaders finish compiling, keep the game on an SSD, and avoid clearing the shader cache often."
                   : "Close it while gaming, or let Game Boost close it automatically.";
        left.Children.Add(UI.Text($"Linked to {s.Spikes} of {total} stutters  ·  {what} at those moments. {tip}", 12, "Sub"));
        // share bar
        var track = new Grid { Height = 5, Margin = new Thickness(0, 6, 20, 0) };
        track.Children.Add(new Border { Background = UI.B("Line"), CornerRadius = new CornerRadius(3) });
        var fill = new Border { Background = UI.Hex("#F43F5E"), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
        double frac = (double)s.Spikes / Math.Max(1, total);
        track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * frac;
        track.Children.Add(fill);
        left.Children.Add(track);
        g.Children.Add(left);

        if (s.CanEnd)
        {
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button kill = null;
            kill = UI.Btn("End task", (_, _) =>
            {
                int n = 0;
                foreach (var p in Process.GetProcessesByName(s.Name)) { try { p.Kill(); n++; } catch { } finally { p.Dispose(); } }
                kill.Content = n > 0 ? "Ended" : "Not running"; kill.IsEnabled = false;
            }, style: "DangerButton");
            bool listed = Store.Settings.BoostKillList.Contains(s.Name, StringComparer.OrdinalIgnoreCase);
            Button add = null;
            add = UI.Btn(listed ? "Closed by Game Boost" : "Close during games", (_, _) =>
            {
                if (!Store.Settings.BoostKillList.Contains(s.Name, StringComparer.OrdinalIgnoreCase)) { Store.Settings.BoostKillList.Add(s.Name); Store.SaveSettings(); }
                add.Content = "Closed by Game Boost"; add.IsEnabled = false;
            }, accent: !listed);
            add.IsEnabled = !listed;
            add.Margin = new Thickness(8, 0, 0, 0);
            btns.Children.Add(kill); btns.Children.Add(add);
            Grid.SetColumn(btns, 1);
            g.Children.Add(btns);
        }
        return new Border { Child = g, Background = UI.B("CardHover"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8) };
    }

    static void DrawTimeline(Canvas c, StutterFinder.Report r)
    {
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w < 20 || r.Timeline.Count == 0) return;
        double max = Math.Max(r.MedianMs * 4, Math.Min(r.WorstMs, r.MedianMs * 12));
        double threshold = Math.Max(r.MedianMs * 2.2, r.MedianMs + 6);
        double bw = Math.Max(1, w / r.Timeline.Count);
        foreach (var (at, ms) in r.Timeline)
        {
            double x = at / Math.Max(0.001, r.Seconds) * (w - bw);
            double bh = Math.Max(2, Math.Min(1, ms / max) * (h - 6));
            var rect = new Rectangle { Width = bw, Height = bh, Fill = ms >= threshold ? UI.Hex("#F43F5E") : UI.Hex("#1F6F86") };
            Canvas.SetLeft(rect, x); Canvas.SetTop(rect, h - bh);
            c.Children.Add(rect);
        }
        var line = new Line { X1 = 0, X2 = w, Y1 = h - Math.Min(1, threshold / max) * (h - 6), Y2 = h - Math.Min(1, threshold / max) * (h - 6), Stroke = UI.Hex("#55F43F5E"), StrokeDashArray = new DoubleCollection { 4, 4 } };
        c.Children.Add(line);
    }
}

// =====================================================================================================
class LatencyCard : Border
{
    readonly Button _start;
    readonly ProgressBar _bar = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    readonly StackPanel _results = new();
    CancellationTokenSource _cts;

    public LatencyCard()
    {
        Style = UI.Res<Style>("CardBorder");
        Margin = new Thickness(0, 0, 0, 16);
        var sp = new StackPanel();
        sp.Children.Add(LabPage.CardHead("", "#F59E0B", "Driver Latency Check", "ADVANCED",
            "Finds drivers that freeze your CPU and cause micro-stutter."));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _start = UI.Btn("Run 20-second check", async (s, e) => await Run(), accent: true, icon: "");
        row.Children.Add(_start);
        row.Children.Add(UI.InfoDot("Works like LatencyMon: measures how long each driver blocks the CPU (DPC/ISR time). Best run with your game open so drivers are under load."));
        sp.Children.Add(row);
        sp.Children.Add(_bar);
        sp.Children.Add(_results);
        Child = sp;
    }

    async Task Run()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        _cts = new CancellationTokenSource();
        _start.Content = "Cancel";
        _bar.Visibility = Visibility.Visible; _bar.Value = 0;
        _results.Children.Clear();
        try
        {
            var res = await LatencyCheck.RunAsync(20, new Progress<double>(v => _bar.Value = v), _cts.Token);
            Show(res);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Store.Log("Latency check: " + ex);
            _results.Children.Add(UI.Tip("The check couldn't start: " + ex.Message + ". Another tool (like LatencyMon or a profiler) may be using Windows' kernel tracing. Close it and try again.", "\uE7BA", "Warn"));
        }
        finally { _cts = null; _start.Content = "Run 20-second check"; _bar.Visibility = Visibility.Collapsed; }
    }

    static (string Text, string Color) Grade(double us) =>
        us < 500 ? ("Excellent", "#2EE59D") : us < 1000 ? ("Good", "#2EE59D") : us < 2000 ? ("Could cause stutter", "#F59E0B") : ("Bad: will cause stutter", "#F43F5E");

    void Show(LatencyCheck.Result r)
    {
        _results.Margin = new Thickness(0, 16, 0, 0);
        var (grade, color) = Grade(r.MaxUs);
        var stats = new WrapPanel();
        stats.Children.Add(LabPage.StatBox("WORST DRIVER DELAY", $"{r.MaxUs:N0} µs", color));
        stats.Children.Add(LabPage.StatBox("RESULT", grade, color));
        stats.Children.Add(LabPage.StatBox("WORST DRIVER", r.MaxDriver ?? "-"));
        stats.Children.Add(LabPage.StatBox("EVENTS MEASURED", r.Events.ToString("N0")));
        _results.Children.Add(stats);
        if (r.Events == 0)
        {
            _results.Children.Add(UI.Tip("No driver activity was recorded. Windows may not allow kernel tracing on this PC right now; try restarting PC Optimizer.", "\uE7BA", "Warn"));
            return;
        }

        // table
        var head = Row("DRIVER", "WHAT IT IS", "WORST", "AVERAGE", "CALLS", header: true);
        _results.Children.Add(head);
        foreach (var d in r.Drivers.Take(10))
        {
            var (_, c) = Grade(d.MaxUs);
            _results.Children.Add(Row(d.Driver, string.IsNullOrEmpty(d.Friendly) ? "-" : d.Friendly, $"{d.MaxUs:N0} µs", $"{d.AvgUs:0.0} µs", d.Count.ToString("N0"), worstColor: c));
        }
        var bad = r.Drivers.Where(d => d.MaxUs >= 1000 && !string.IsNullOrEmpty(d.Fix)).Take(3).ToList();
        if (bad.Count > 0)
        {
            _results.Children.Add(UI.Section("How to fix it"));
            foreach (var d in bad) _results.Children.Add(UI.Tip($"{d.Friendly} ({d.Driver}, up to {d.MaxUs:N0} µs): {d.Fix}", "\uE7BA", "Warn"));
        }
        else _results.Children.Add(UI.Tip("No driver held the CPU long enough to cause stutter. If you still stutter, try the Stutter Finder above.", "\uE73E", "Good"));
        _results.Children.Add(UI.Text("Under 1,000 µs is fine. Over 2,000 µs causes stutter.", 11, "Sub"));
    }

    static Grid Row(string a, string b, string c, string d, string e, bool header = false, string worstColor = null)
    {
        var g = new Grid { Margin = new Thickness(0, header ? 8 : 0, 0, 0) };
        foreach (var w in new[] { 180.0, -1, 110, 100, 90 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        string[] vals = { a, b, c, d, e };
        for (int i = 0; i < 5; i++)
        {
            var t = UI.Text(vals[i], header ? 10.5 : 12.5, header ? "Sub" : i == 0 ? "Text" : "Sub", header || i == 0 ? FontWeights.SemiBold : FontWeights.Normal, false);
            t.TextTrimming = TextTrimming.CharacterEllipsis; t.Margin = new Thickness(0, 5, 10, 5);
            if (!header && i == 2 && worstColor != null) { t.Foreground = UI.Hex(worstColor); t.FontWeight = FontWeights.SemiBold; }
            if (i >= 2) t.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(t, i); g.Children.Add(t);
        }
        if (!header) g.Children.Add(new Border { BorderBrush = UI.B("Line"), BorderThickness = new Thickness(0, 0, 0, 1), IsHitTestVisible = false });
        if (!header) Grid.SetColumnSpan(g.Children[^1], 5);
        return g;
    }
}

// =====================================================================================================
class PollingCard : Border
{
    readonly Gauge _gauge = new() { Height = 230, Width = 300, UnitText = "Hz", Phase = "READY", Ticks = new double[] { 0, 125, 250, 500, 1000, 2000, 4000, 8000 } };
    readonly Button _start;
    readonly TextBlock _hint = UI.Text("Press Start, then move your mouse in fast circles.", 13, "Sub");
    readonly WrapPanel _stats = new() { Margin = new Thickness(0, 10, 0, 0) };
    HwndSource _src;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly List<long> _events = new(20000);
    readonly List<double> _rates = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _peak;
    long _started;

    public PollingCard()
    {
        Style = UI.Res<Style>("CardBorder");
        Margin = new Thickness(0, 0, 0, 16);
        var sp = new StackPanel();
        sp.Children.Add(LabPage.CardHead("", "#00E5FF", "Mouse Polling Rate Test", "INPUT",
            "Checks your mouse Hz. 1000 Hz+ means less input delay."));
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(_gauge);
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };
        right.Children.Add(_hint);
        _start = UI.Btn("Start test", (s, e) => Toggle(), accent: true, icon: "");
        _start.HorizontalAlignment = HorizontalAlignment.Left; _start.Margin = new Thickness(0, 12, 0, 0);
        right.Children.Add(_start);
        right.Children.Add(_stats);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        sp.Children.Add(g);
        Child = sp;
        _timer.Tick += (_, _) => Tick();
        Unloaded += (_, _) => Stop();
    }

    // ---- raw input ----
    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint headerSize);
    const uint RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 0x1, RID_INPUT = 0x10000003;
    static readonly IntPtr Buf = Marshal.AllocHGlobal(128);

    void Toggle() { if (_src != null) Stop(); else Start(); }

    void Start()
    {
        var w = Window.GetWindow(this);
        if (w == null) return;
        _src = HwndSource.FromHwnd(new WindowInteropHelper(w).Handle);
        if (_src == null) return;
        var dev = new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_INPUTSINK, Target = _src.Handle } };
        if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) { _hint.Text = "Couldn't read raw mouse input on this PC."; _src = null; return; }
        _src.AddHook(Hook);
        _events.Clear(); _rates.Clear(); _peak = 0; _started = _clock.ElapsedTicks;
        _stats.Children.Clear();
        _hint.Text = "Move your mouse in fast circles now...";
        _gauge.Phase = "MOVE YOUR MOUSE"; _gauge.SetRunning(true);
        _start.Content = "Stop";
        _timer.Start();
    }

    void Stop()
    {
        if (_src == null) return;
        _timer.Stop();
        try
        {
            var dev = new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_REMOVE, Target = IntPtr.Zero } };
            RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch { }
        _src.RemoveHook(Hook); _src = null;
        _gauge.SetRunning(false);
        _start.Content = "Start test";
        ShowResult();
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x00FF) return IntPtr.Zero; // WM_INPUT
        uint size = 128;
        if (GetRawInputData(lParam, RID_INPUT, Buf, ref size, 24) == uint.MaxValue) return IntPtr.Zero;
        if (Marshal.ReadInt32(Buf, 0) != 0) return IntPtr.Zero; // RIM_TYPEMOUSE
        int dx = Marshal.ReadInt32(Buf, 36), dy = Marshal.ReadInt32(Buf, 40);
        if (dx != 0 || dy != 0) _events.Add(_clock.ElapsedTicks);
        return IntPtr.Zero;
    }

    void Tick()
    {
        long now = _clock.ElapsedTicks, win = Stopwatch.Frequency / 5; // 200 ms window
        int n = 0;
        for (int i = _events.Count - 1; i >= 0 && _events[i] >= now - win; i--) n++;
        double rate = n * 5.0;
        // only count windows where the mouse moved the whole time (no long gaps)
        bool continuous = n > 20 && GapsOk(now - win);
        if (continuous) { _rates.Add(rate); _peak = Math.Max(_peak, rate); }
        _gauge.Value = continuous ? rate : rate * 0.6;
        if ((now - _started) / Stopwatch.Frequency > 12) Stop();
    }

    bool GapsOk(long from)
    {
        long maxGap = Stopwatch.Frequency / 40; // 25 ms without a report = hand stopped
        long prev = from;
        foreach (var t in _events.Where(t => t >= from)) { if (t - prev > maxGap) return false; prev = t; }
        return _clock.ElapsedTicks - prev < maxGap;
    }

    void ShowResult()
    {
        _stats.Children.Clear();
        if (_rates.Count < 3)
        {
            _hint.Text = "Not enough movement was recorded. Press Start and keep moving the mouse in fast circles.";
            _gauge.Value = 0; _gauge.Phase = "READY";
            return;
        }
        var top = _rates.OrderByDescending(x => x).Take(Math.Max(3, _rates.Count / 3)).ToList();
        double sustained = top[top.Count / 2];
        double[] steps = { 125, 250, 500, 1000, 2000, 4000, 8000 };
        double setting = steps.OrderBy(s => Math.Abs(Math.Log(s / sustained))).First();
        double stability = Math.Clamp(100 - top.Select(x => Math.Abs(x - sustained) / sustained * 100).Average(), 0, 100);
        _gauge.Value = sustained; _gauge.Phase = $"≈ {setting:0} Hz MOUSE";
        _stats.Children.Add(LabPage.StatBox("YOUR MOUSE", $"≈ {setting:0} Hz", setting >= 1000 ? "#2EE59D" : "#F59E0B"));
        _stats.Children.Add(LabPage.StatBox("MEASURED", $"{sustained:0} Hz"));
        _stats.Children.Add(LabPage.StatBox("PEAK", $"{_peak:0} Hz"));
        _stats.Children.Add(LabPage.StatBox("STABILITY", $"{stability:0}%"));
        _stats.Children.Add(LabPage.StatBox("ADDED DELAY", $"up to {1000 / setting:0.##} ms", setting >= 1000 ? "#2EE59D" : "#F59E0B"));
        _hint.Text = setting < 1000
            ? $"Your mouse reports about {setting:0} times per second. Set it to 1000 Hz (or higher) in your mouse's software to cut up to {1000 / setting - 1:0.#} ms of input delay."
            : setting >= 4000
                ? "Very high polling rate. Great for input delay, but it uses more CPU: if games stutter, try 2000 Hz."
                : "Your mouse is already at a low-latency polling rate. Nothing to change.";
    }
}
