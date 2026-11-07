using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Overdrive.Core;

namespace Overdrive.Pages;

/// <summary>Summary of one play session, shown after the game closes.</summary>
public record Session(string Game, TimeSpan Length, double AvgFps, double Low1, double MaxGpuTemp, double AvgCpu, double AvgGpu, DateTime Ended);

/// <summary>
/// One shared sampler for the Performance tab and the in-game overlay.
/// Keeps history, so graphs are already full when you open the tab.
/// </summary>
public static class LiveMonitor
{
    static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(1) };
    static bool _reading;
    static int _tick;
    public static int? ManualPid { get; set; }
    public static bool PageVisible { get; set; }

    public static FpsMonitor.Stats Stats { get; private set; }
    public static HwMonitor.Sample Hw { get; private set; }
    public static string Game => FpsMonitor.HasTarget ? FpsMonitor.ProcessName : null;
    public static readonly List<double> FpsHistory = new(), CpuHistory = new(), GpuHistory = new(), TempHistory = new(), VramHistory = new(), RamHistory = new(), PingHistory = new();
    public static Session LastSession { get; private set; }
    public static event Action Updated;

    // session accumulation
    static DateTime _sessionStart;
    static readonly List<double> _sFps = new(), _sLow = new(), _sCpu = new(), _sGpu = new();
    static double _sMaxTemp;

    static OverlayWindow _overlay;

    public static void Start()
    {
        Timer.Tick -= OnTick;
        Timer.Tick += OnTick;
        Timer.Start();
    }

    /// <summary>Only sample when someone is looking: the Performance tab on screen, or the in-game overlay on.</summary>
    static bool Needed
    {
        get
        {
            var w = Application.Current?.MainWindow;
            bool tabOnScreen = PageVisible && w != null && w.IsVisible && w.WindowState != WindowState.Minimized;
            return tabOnScreen || Store.Settings.FpsOverlay || Benchmarking || StutterFinder.Active;
        }
    }

    static async void OnTick(object s, EventArgs e)
    {
        if (_reading) return;
        if (!Needed)
        {
            if (FpsMonitor.Running) { if (FpsMonitor.HasTarget) EndSession(); await Task.Run(FpsMonitor.Stop); }
            _overlay?.Hide();
            return;
        }
        _reading = true;
        try
        {
            _tick++;
            if (!FpsMonitor.Running) await Task.Run(FpsMonitor.Start);
            TrackForeground();
            if (_tick % 2 == 1)
            {
                var target = await Task.Run(FindTarget);
                if (target.HasValue && target.Value.Pid != FpsMonitor.Pid)
                {
                    if (FpsMonitor.HasTarget) EndSession();
                    FpsHistory.Clear();
                    FpsMonitor.SetTarget(target.Value.Pid, target.Value.Name);
                    _targetLastSeen = Environment.TickCount64;
                    BeginSession();
                }
                else if (!target.HasValue && FpsMonitor.HasTarget) { EndSession(); FpsMonitor.ClearTarget(); }
            }

            Hw = await Task.Run(HwMonitor.Read);
            Stats = FpsMonitor.HasTarget ? FpsMonitor.Read() : null;

            if (Stats != null)
            {
                Add(FpsHistory, Stats.Fps, 120);
                _sFps.Add(Stats.Fps); _sLow.Add(Stats.Low1);
            }
            Add(CpuHistory, Hw.Cpu, 60);
            if (Hw.Gpu.HasValue) Add(GpuHistory, Hw.Gpu.Value, 60);
            if (Hw.GpuTemp.HasValue) { Add(TempHistory, Hw.GpuTemp.Value, 60); _sMaxTemp = Math.Max(_sMaxTemp, Hw.GpuTemp.Value); }
            if (Hw.VramUsedGb.HasValue) Add(VramHistory, Hw.VramUsedGb.Value, 60);
            Add(RamHistory, Hw.Ram, 60);
            if (Hw.PingMs.HasValue) Add(PingHistory, Hw.PingMs.Value, 60);
            if (FpsMonitor.HasTarget) { _sCpu.Add(Hw.Cpu); if (Hw.Gpu.HasValue) _sGpu.Add(Hw.Gpu.Value); }

            // in-game overlay: only while a game is actually rendering
            if (Store.Settings.FpsOverlay && Stats != null)
            {
                _overlay ??= new OverlayWindow();
                if (!_overlay.IsVisible) _overlay.Show();
                _overlay.Update();
            }
            else _overlay?.Hide();

            BenchTick();
            Updated?.Invoke();
        }
        catch (Exception ex) { Store.Log("Monitor tick: " + ex.Message); }
        finally { _reading = false; }
    }

    static void Add(List<double> list, double v, int cap) { list.Add(v); if (list.Count > cap) list.RemoveAt(0); }

    static void BeginSession()
    {
        _sessionStart = DateTime.Now;
        _sFps.Clear(); _sLow.Clear(); _sCpu.Clear(); _sGpu.Clear(); _sMaxTemp = 0;
    }

    static void EndSession()
    {
        var len = DateTime.Now - _sessionStart;
        if (_sFps.Count >= 10 && Game != null)
        {
            LastSession = new Session(Game, len, _sFps.Average(), _sLow.Average(), _sMaxTemp,
                _sCpu.Count > 0 ? _sCpu.Average() : 0, _sGpu.Count > 0 ? _sGpu.Average() : 0, DateTime.Now);
            Store.Log($"Session: {LastSession}");
        }
        _sFps.Clear();
    }

    // the last app you had in front that wasn't us or the desktop (so alt-tabbing to look at the graph keeps the game)
    static int _lastForeground;
    static long _targetLastSeen;
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "ApplicationFrameHost",
        "chrome", "msedge", "firefox", "opera", "brave", "Discord", "Spotify", "steamwebhelper", "EpicGamesLauncher", "Teams", "ms-teams",
        "obs64", "NVIDIA Overlay", "NVIDIA App", "RadeonSoftware", "Code", "devenv", "notepad", "Taskmgr", "SystemSettings", "WhatsApp",
        "Battle.net", "RiotClientServices", "RiotClientUx", "steam", "LockApp", "ScreenClippingHost", "SnippingTool",
    };

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);

    static string NameOf(int pid) { try { using var p = Process.GetProcessById(pid); return p.ProcessName; } catch { return null; } }

    static void TrackForeground()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out int pid);
            if (pid == 0 || pid == Environment.ProcessId) return;
            var n = NameOf(pid);
            if (n != null && !NotGames.Contains(n) && !SelfInstall.IsOurProcess(n)) _lastForeground = pid;
        }
        catch { }
    }

    static string Pretty(int pid, string fallback)
    {
        var n = NameOf(pid) ?? fallback;
        var g = Games.All.FirstOrDefault(x => x.ProcessNames.Any(pn => string.Equals(pn, n, StringComparison.OrdinalIgnoreCase)));
        return g?.Name ?? n;
    }

    static (int Pid, string Name)? FindTarget()
    {
        var presenting = FpsMonitor.ActivePresenters();
        bool Presents(int pid) => presenting.Any(p => p.Pid == pid);

        // keep the current game through short pauses (loading screens), drop it once it stops drawing for a while
        if (FpsMonitor.HasTarget && !ManualPid.HasValue)
        {
            if (Presents(FpsMonitor.Pid)) _targetLastSeen = Environment.TickCount64;
            else if (Environment.TickCount64 - _targetLastSeen < 8000 && NameOf(FpsMonitor.Pid) != null) return (FpsMonitor.Pid, FpsMonitor.ProcessName);
        }

        // 1) you picked it
        if (ManualPid.HasValue)
        {
            if (NameOf(ManualPid.Value) is string mn) return (ManualPid.Value, Pretty(ManualPid.Value, mn));
            ManualPid = null;
        }
        // 2) a known game that is drawing frames
        var running = Games.RunningNames();
        foreach (var g in Games.All)
            foreach (var pn in g.ProcessNames)
            {
                if (!running.Contains(pn)) continue;
                if (g.Id == "minecraft" && !Games.IsRunning(g)) continue;
                Process[] ps; try { ps = Process.GetProcessesByName(pn); } catch { continue; }
                var hit = ps.FirstOrDefault(x => Presents(x.Id)); // must be drawing frames right now, not just running in the tray
                if (hit != null) return (hit.Id, g.Name);
            }
        // 3) the app you were last playing in front, if it draws frames (any game, even unknown ones)
        if (_lastForeground != 0 && Presents(_lastForeground) && NameOf(_lastForeground) is string fn) return (_lastForeground, Pretty(_lastForeground, fn));
        // 4) the busiest app drawing frames that isn't a browser/launcher/desktop
        foreach (var (pid, fps) in presenting)
        {
            if (pid == Environment.ProcessId || fps < 20) continue;
            var n = NameOf(pid);
            if (n == null || NotGames.Contains(n) || SelfInstall.IsOurProcess(n)) continue;
            return (pid, Pretty(pid, n));
        }
        return null;
    }

    public static List<(int Pid, string Name, double Fps)> PresentersForPicker()
    {
        var list = new List<(int, string, double)>();
        foreach (var (pid, fps) in FpsMonitor.ActivePresenters())
        {
            if (pid == Environment.ProcessId) continue;
            var n = NameOf(pid);
            if (n == null || n.Equals("dwm", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add((pid, Pretty(pid, n), fps));
        }
        return list;
    }

    public static string StatusText()
    {
        if (FpsMonitor.Error != null) return "FPS capture unavailable: " + FpsMonitor.Error;
        if (!FpsMonitor.Running) return "Starting FPS capture...";
        if (FpsMonitor.HasTarget && Stats == null) return $"{FpsMonitor.ProcessName}: waiting for frames (minimized, loading, or a Vulkan/OpenGL game)";
        if (Interlocked.Read(ref FpsMonitor.EventsSeen) == 0) return "Start a game. (No apps are drawing DirectX frames right now.)";
        return "Start a game, or click it once so it's the app in front";
    }

    // ---------------- before / after FPS test ----------------
    public static bool Benchmarking => _benchEnd > 0;
    public static double BenchRemaining => Math.Max(0, (_benchEnd - Environment.TickCount64) / 1000.0);
    public static string BenchLabel { get; private set; }
    static long _benchEnd;
    static readonly List<double> _bFps = new(), _bLow = new(), _bFt = new();
    public static event Action<BenchResult> BenchDone;

    public static void StartBenchmark(string label, int seconds = 60)
    {
        BenchLabel = label;
        _bFps.Clear(); _bLow.Clear(); _bFt.Clear();
        _benchEnd = Environment.TickCount64 + seconds * 1000L;
    }

    public static void CancelBenchmark() { _benchEnd = 0; }

    static void BenchTick()
    {
        if (_benchEnd == 0) return;
        if (Stats != null) { _bFps.Add(Stats.Fps); _bLow.Add(Stats.Low1); _bFt.Add(Stats.FrameTimeMs); }
        if (Environment.TickCount64 < _benchEnd) return;
        _benchEnd = 0;
        if (_bFps.Count < 15) { BenchDone?.Invoke(null); return; }
        var r = new BenchResult { Game = Game ?? "Unknown", Label = BenchLabel, AvgFps = _bFps.Average(), Low1 = _bLow.Average(), FrameTime = _bFt.Average(), At = DateTime.Now };
        Store.Settings.Bench.Add(r);
        if (Store.Settings.Bench.Count > 40) Store.Settings.Bench.RemoveAt(0);
        Store.SaveSettings();
        BenchDone?.Invoke(r);
    }

    public static event Action OverlayChanged;

    /// <summary>The one place the overlay gets turned on/off (Settings, Performance tab, tray menu, hotkey).</summary>
    public static void SetOverlay(bool on)
    {
        Store.Settings.FpsOverlay = on; Store.SaveSettings();
        if (!on) _overlay?.Hide();
        OverlayChanged?.Invoke();
    }

    public static void Shutdown()
    {
        Timer.Stop();
        try { _overlay?.Close(); } catch { }
        _overlay = null;
    }
}

/// <summary>Live FPS, frame time and hardware graphs.</summary>
public class PerformancePage : UserControl, IRefreshable
{
    readonly Graph _fpsGraph = new() { Height = 150, Capacity = 120, Unit = "", TargetLabel = null, ShowGrid = false };
    readonly Graph _ftGraph = new() { Height = 100, Capacity = 240, Unit = " ms", Format = "0.0", Smooth = false, SecondsPerPoint = 0.01,
        Line = (Color)ColorConverter.ConvertFromString("#2EE59D"), Line2 = (Color)ColorConverter.ConvertFromString("#00E5FF") };
    readonly TextBlock _fps = new() { FontSize = 60, FontWeight = FontWeights.Black, Foreground = UI.B("AccentGrad"), Text = "--" };
    readonly TextBlock _low = UI.Text("--", 22, "Text", FontWeights.Bold, false);
    readonly TextBlock _ft = UI.Text("--", 22, "Text", FontWeights.Bold, false);
    readonly TextBlock _spike = UI.Text("--", 22, "Text", FontWeights.Bold, false);
    readonly TextBlock _smooth = UI.Text("", 12, "Sub", FontWeights.SemiBold);
    readonly TextBlock _game = UI.Text("Start a game to see live FPS", 14, "Sub", FontWeights.SemiBold);
    Button _pick;
    readonly Dictionary<string, (TextBlock Value, TextBlock Sub, Graph Graph)> _tiles = new();
    readonly TextBlock _connType = UI.Text("-", 14, "Text", FontWeights.SemiBold), _connSpeed = UI.Text("-", 14, "Text", FontWeights.SemiBold);
    readonly StackPanel _sessionBox = new();
    int _connTick;

    public PerformancePage()
    {
        var body = new StackPanel();
        body.Children.Add(UI.Header("FPS Monitor", "Live FPS, frame times and hardware stats."));

        // ---------------- FPS hero ----------------
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        var fpsRow = new StackPanel { Orientation = Orientation.Horizontal };
        fpsRow.Children.Add(_fps);
        var fpsUnit = UI.Text("FPS", 16, "Sub", FontWeights.Bold, false); fpsUnit.VerticalAlignment = VerticalAlignment.Bottom; fpsUnit.Margin = new Thickness(10, 0, 0, 14);
        fpsRow.Children.Add(fpsUnit);
        left.Children.Add(fpsRow);
        left.Children.Add(_game);
        left.Children.Add(_smooth);
        top.Children.Add(left);

        var tools = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        _pick = UI.Btn("Game: auto-detect", (s2, e2) => OpenPicker(), icon: "\uE70D");
        _pick.Padding = new Thickness(12, 6, 12, 6); _pick.HorizontalAlignment = HorizontalAlignment.Right;
        tools.Children.Add(_pick);
        var overlayBtn = new CheckBox { Style = UI.Res<Style>("Check"), Content = UI.Text("In-game overlay", 12, "Sub"), IsChecked = Store.Settings.FpsOverlay, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), ToolTip = "Shortcut: Ctrl+Shift+O" };
        overlayBtn.Checked += (_, _) => { if (!Store.Settings.FpsOverlay) LiveMonitor.SetOverlay(true); };
        overlayBtn.Unchecked += (_, _) => { if (Store.Settings.FpsOverlay) LiveMonitor.SetOverlay(false); };
        LiveMonitor.OverlayChanged += () => overlayBtn.IsChecked = Store.Settings.FpsOverlay;
        tools.Children.Add(overlayBtn);
        Grid.SetColumn(tools, 1);
        top.Children.Add(tools);

        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 16, 0, 16) };
        stats.Children.Add(Mini("1% LOW", _low));
        stats.Children.Add(Mini("FRAME TIME", _ft));
        stats.Children.Add(Mini("WORST (10s)", _spike));

        var hero = new StackPanel();
        hero.Children.Add(top);
        hero.Children.Add(stats);
        hero.Children.Add(_fpsGraph);
        var heroCard = UI.Card(hero, background: UI.B("HeroGrad"));
        heroCard.Padding = new Thickness(24);
        heroCard.Margin = new Thickness(0, 0, 0, 12);
        body.Children.Add(heroCard);

        var ftBox = new StackPanel();
        ftBox.Children.Add(UI.Text("Flat line = smooth. Spikes = stutter.", 12, "Sub"));
        _ftGraph.Margin = new Thickness(0, 10, 0, 0);
        ftBox.Children.Add(_ftGraph);
        body.Children.Add(UI.Collapsible("Frame times (stutter check)", ftBox, "\uE9D9"));

        // ---------------- before / after test ----------------
        body.Children.Add(UI.Section("Before / after FPS test"));
        body.Children.Add(BuildBench());

        // ---------------- last session ----------------
        body.Children.Add(_sessionBox);

        // ---------------- hardware tiles ----------------
        body.Children.Add(UI.Section("Hardware"));
        var grid = new UniformGrid { Columns = 3 };
        grid.Children.Add(Tile("cpu", "", "CPU USAGE", 100, "%", "#00E5FF", "#8B5CF6"));
        grid.Children.Add(Tile("gpu", "", "GPU USAGE", 100, "%", "#2EE59D", "#00E5FF"));
        grid.Children.Add(Tile("temp", "", "GPU TEMPERATURE", 100, "°C", "#FFB547", "#FF5470"));
        grid.Children.Add(Tile("vram", "", "VIDEO MEMORY (VRAM)", null, " GB", "#B69CFF", "#8B5CF6"));
        grid.Children.Add(Tile("ram", "", "RAM", 100, "%", "#00E5FF", "#2EE59D"));
        grid.Children.Add(Tile("ping", "", "PING (1.1.1.1)", null, " ms", "#FF8AD8", "#8B5CF6"));
        _tiles["vram"].Graph.Format = "0.0";
        body.Children.Add(grid);

        // ---------------- connection ----------------
        body.Children.Add(UI.Section("Connection"));
        var conn = new UniformGrid { Columns = 2 };
        conn.Children.Add(Field("TYPE", _connType));
        conn.Children.Add(Field("LINK SPEED", _connSpeed));
        var connBox = new StackPanel();
        connBox.Children.Add(conn);
        body.Children.Add(UI.Card(connBox));

        Content = UI.PageScroll(body);

        LiveMonitor.Updated += () => { if (IsVisible) Render(); };
        Loaded += (_, _) => { LiveMonitor.PageVisible = true; LoadConnection(); FillFromHistory(); Render(); };
        Unloaded += (_, _) => LiveMonitor.PageVisible = false;
    }

    readonly TextBlock _benchStatus = UI.Text("", 13, "Sub");
    readonly StackPanel _benchResult = new();
    Button _bBefore, _bAfter;

    UIElement BuildBench()
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.Text("Record 60s of gameplay before tweaks, then again after, same map. See the exact FPS change.", 13, "Sub"));
        _bBefore = UI.Btn("Record BEFORE (60s)", (s, e) => StartBench("Before"), icon: "\uE7C8");
        _bAfter = UI.Btn("Record AFTER (60s)", (s, e) => StartBench("After"), accent: true, icon: "\uE7C8");
        var row = UI.Row(_bBefore, _bAfter); row.Margin = new Thickness(0, 12, 0, 8);
        sp.Children.Add(row);
        sp.Children.Add(_benchStatus);
        _benchResult.Margin = new Thickness(0, 10, 0, 0);
        sp.Children.Add(_benchResult);
        LiveMonitor.BenchDone += r => Dispatcher.BeginInvoke(() =>
        {
            _bBefore.IsEnabled = _bAfter.IsEnabled = true;
            if (r == null) { _benchStatus.Text = "Not enough FPS data. Make sure the game is running in DirectX and in front, then try again."; _benchStatus.Foreground = UI.B("Warn"); return; }
            _benchStatus.Text = $"Recorded {r.Label}: {r.Game}, avg {r.AvgFps:0} FPS, 1% low {r.Low1:0}.";
            _benchStatus.Foreground = UI.B("Good");
            ShowBenchCompare();
        });
        ShowBenchCompare();
        return UI.Card(sp);
    }

    async void StartBench(string label)
    {
        _bBefore.IsEnabled = _bAfter.IsEnabled = false;
        for (int i = 5; i > 0; i--) { _benchStatus.Text = $"Switch to your game now... starting in {i}"; _benchStatus.Foreground = UI.B("Accent"); await Task.Delay(1000); }
        LiveMonitor.StartBenchmark(label);
        while (LiveMonitor.Benchmarking)
        {
            _benchStatus.Text = $"Recording {label}... {LiveMonitor.BenchRemaining:0}s left ({(LiveMonitor.Game ?? "waiting for a game")}). Keep playing.";
            await Task.Delay(1000);
        }
    }

    void ShowBenchCompare()
    {
        _benchResult.Children.Clear();
        var list = Store.Settings.Bench;
        var after = list.LastOrDefault(b => b.Label == "After");
        var before = after != null ? list.LastOrDefault(b => b.Label == "Before" && b.Game == after.Game && b.At < after.At) : list.LastOrDefault(b => b.Label == "Before");
        if (before == null) return;
        var g = new UniformGrid { Columns = 3 };
        Border M(string label, string value, string sub, string color)
        {
            var p = new StackPanel();
            p.Children.Add(UI.Label(label));
            p.Children.Add(UI.Text(value, 24, color, FontWeights.Bold, false));
            p.Children.Add(UI.Text(sub, 11, "Dim"));
            return new Border { Child = p, Background = UI.Hex("#0F131B"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 10, 0) };
        }
        if (after == null)
        {
            g.Children.Add(M("BEFORE: AVG FPS", $"{before.AvgFps:0}", before.Game, "Text"));
            g.Children.Add(M("BEFORE: 1% LOW", $"{before.Low1:0}", "now turn on tweaks, then record AFTER", "Text"));
            _benchResult.Children.Add(g);
            return;
        }
        string Pct(double a, double b) { var d = (b - a) / Math.Max(1, a) * 100; return (d >= 0 ? "+" : "") + d.ToString("0.0") + "%"; }
        string Col(double a, double b) => b >= a * 1.02 ? "Good" : b <= a * 0.98 ? "Bad" : "Text";
        g.Children.Add(M("AVERAGE FPS", Pct(before.AvgFps, after.AvgFps), $"{before.AvgFps:0} → {after.AvgFps:0}", Col(before.AvgFps, after.AvgFps)));
        g.Children.Add(M("1% LOW (SMOOTHNESS)", Pct(before.Low1, after.Low1), $"{before.Low1:0} → {after.Low1:0}", Col(before.Low1, after.Low1)));
        g.Children.Add(M("FRAME TIME", $"{after.FrameTime - before.FrameTime:+0.00;-0.00} ms", $"{before.FrameTime:0.00} → {after.FrameTime:0.00} ms", after.FrameTime <= before.FrameTime ? "Good" : "Bad"));
        _benchResult.Children.Add(g);
        double dAvg = (after.AvgFps - before.AvgFps) / Math.Max(1, before.AvgFps), dLow = (after.Low1 - before.Low1) / Math.Max(1, before.Low1);
        string verdict = dAvg < -0.03 || dLow < -0.05
            ? "FPS went DOWN. One of the tweaks doesn't suit your PC. Usual suspects: the FPS cap, Hardware-accelerated GPU scheduling, CPU sleep states off, Low Latency Mode. Turn them off one at a time and test again."
            : dAvg > 0.03 || dLow > 0.05 ? "The tweaks are working on your PC."
            : "About the same. That's normal on a strong PC: these tweaks mostly cut stutter and input delay rather than raise average FPS. Bigger FPS gains come from in-game settings, DLSS/FSR, and turning off Memory Integrity.";
        var v = UI.Text(verdict, 13, dAvg < -0.03 || dLow < -0.05 ? "Warn" : "Text");
        v.Margin = new Thickness(0, 10, 0, 0);
        _benchResult.Children.Add(v);
        _benchResult.Children.Add(UI.Text("Tip: results are only fair if you test the same map/mode and do similar things both times. Run each a couple of times if the numbers are close.", 11, "Dim"));
    }

    static StackPanel Mini(string label, TextBlock value)
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.Label(label));
        sp.Children.Add(value);
        return sp;
    }

    static Border Field(string label, FrameworkElement value)
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.Label(label));
        sp.Children.Add(value);
        return new Border { Child = sp, Margin = new Thickness(0, 0, 12, 0) };
    }

    Border Tile(string key, string icon, string label, double? max, string unit, string c1, string c2)
    {
        var sp = new StackPanel();
        sp.Children.Add(UI.Row(new TextBlock { Text = icon, FontFamily = UI.IconFont, FontSize = 14, Foreground = UI.Hex(c1), Margin = new Thickness(0, 0, 10, 0) }, UI.Label(label)));
        var v = UI.Text("--", 26, "Text", FontWeights.Bold, false);
        var sub = UI.Text("", 11, "Dim", wrap: false);
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        sp.Children.Add(v); sp.Children.Add(sub);
        var g = new Graph { Height = 70, Capacity = 60, FixedMax = max, ShowGrid = false, ShowStats = false, Unit = unit, Margin = new Thickness(0, 10, 0, 0),
                            Line = (Color)ColorConverter.ConvertFromString(c1), Line2 = (Color)ColorConverter.ConvertFromString(c2) };
        sp.Children.Add(g);
        _tiles[key] = (v, sub, g);
        return UI.Card(sp, new Thickness(0, 0, 12, 12));
    }

    void FillFromHistory()
    {
        _fpsGraph.SetAll(LiveMonitor.FpsHistory);
        _tiles["cpu"].Graph.SetAll(LiveMonitor.CpuHistory);
        _tiles["gpu"].Graph.SetAll(LiveMonitor.GpuHistory);
        _tiles["temp"].Graph.SetAll(LiveMonitor.TempHistory);
        _tiles["vram"].Graph.SetAll(LiveMonitor.VramHistory);
        _tiles["ram"].Graph.SetAll(LiveMonitor.RamHistory);
        _tiles["ping"].Graph.SetAll(LiveMonitor.PingHistory);
    }

    void Render()
    {
        var st = LiveMonitor.Stats;
        var hw = LiveMonitor.Hw;
        var disp = Display.Cached;
        _fpsGraph.Target = disp?.CurrentHz;
        _fpsGraph.TargetLabel = disp != null ? $"{disp.CurrentHz} Hz" : null;

        if (st != null)
        {
            _fps.Text = $"{st.Fps:0}";
            _low.Text = $"{st.Low1:0}";
            _ft.Text = $"{st.FrameTimeMs:0.0} ms";
            _spike.Text = $"{st.MaxFrameTimeMs:0.0} ms";
            _spike.Foreground = UI.B(st.MaxFrameTimeMs > st.FrameTimeMs * 4 ? "Warn" : "Text");
            _game.Text = $"{LiveMonitor.Game}: capturing";
            _game.Foreground = UI.B("Good");
            double ratio = st.Low1 / Math.Max(1, st.Fps);
            _smooth.Text = ratio > 0.75 ? "Smooth: lows are close to your average" : ratio > 0.5 ? "Some stutter: 1% lows are well below average" : "Stuttery: big gap between average FPS and 1% lows";
            _smooth.Foreground = UI.B(ratio > 0.75 ? "Good" : ratio > 0.5 ? "Warn" : "Bad");
        }
        else
        {
            _fps.Text = "--"; _low.Text = "--"; _ft.Text = "--"; _spike.Text = "--"; _smooth.Text = "";
            _game.Text = LiveMonitor.StatusText();
            _game.Foreground = UI.B("Sub");
        }
        _fpsGraph.SetAll(LiveMonitor.FpsHistory);
        _ftGraph.SetAll(st?.RecentFrameTimes ?? new List<double>());

        if (hw != null)
        {
            Set("cpu", LiveMonitor.CpuHistory, $"{hw.Cpu:0}%", SysInfo.CpuName);
            Set("gpu", LiveMonitor.GpuHistory, hw.Gpu.HasValue ? $"{hw.Gpu:0}%" : "n/a", hw.GpuClockMhz.HasValue ? $"{hw.GpuClockMhz:0} MHz" + (hw.GpuPowerW.HasValue ? $"  ·  {hw.GpuPowerW:0} W" : "") : SysInfo.PrimaryGpu?.Name ?? "");
            Set("temp", LiveMonitor.TempHistory, hw.GpuTemp.HasValue ? $"{hw.GpuTemp:0}°C" : "n/a", hw.GpuTemp is > 83 ? "Running hot. Check fans and airflow." : hw.GpuTemp.HasValue ? "Normal under load: 60-80°C" : "Available on NVIDIA GPUs");
            if (hw.GpuTemp is > 83) _tiles["temp"].Value.Foreground = UI.B("Bad");
            Set("vram", LiveMonitor.VramHistory, hw.VramUsedGb.HasValue ? $"{hw.VramUsedGb:0.0} GB" : "n/a", hw.VramTotalGb.HasValue ? $"of {hw.VramTotalGb:0} GB" : "in use");
            Set("ram", LiveMonitor.RamHistory, $"{hw.RamUsedGb:0.0} GB", $"{hw.Ram:0}% of {Math.Round(SysInfo.RamGb)} GB");
            Set("ping", LiveMonitor.PingHistory, hw.PingMs.HasValue ? $"{hw.PingMs:0} ms" : "--", "To Cloudflare. Shows spikes on your own connection");
        }

        RenderSession();
        if (++_connTick % 15 == 0) LoadConnection();
    }

    Session _shownSession;
    void RenderSession()
    {
        var s = LiveMonitor.LastSession;
        if (s == null || s == _shownSession) return;
        _shownSession = s;
        _sessionBox.Children.Clear();
        _sessionBox.Children.Add(UI.Section($"Last session: {s.Game}"));
        var wp = new UniformGrid { Columns = 6 };
        Border M(string l, string v)
        {
            var sp = new StackPanel(); sp.Children.Add(UI.Label(l)); sp.Children.Add(UI.Text(v, 20, "Text", FontWeights.Bold, false));
            return new Border { Child = sp, Margin = new Thickness(0, 0, 12, 0) };
        }
        wp.Children.Add(M("PLAYED", s.Length.TotalHours >= 1 ? $"{(int)s.Length.TotalHours}h {s.Length.Minutes}m" : $"{s.Length.Minutes}m {s.Length.Seconds}s"));
        wp.Children.Add(M("AVG FPS", $"{s.AvgFps:0}"));
        wp.Children.Add(M("AVG 1% LOW", $"{s.Low1:0}"));
        wp.Children.Add(M("AVG CPU", $"{s.AvgCpu:0}%"));
        wp.Children.Add(M("AVG GPU", s.AvgGpu > 0 ? $"{s.AvgGpu:0}%" : "n/a"));
        wp.Children.Add(M("MAX GPU TEMP", s.MaxGpuTemp > 0 ? $"{s.MaxGpuTemp:0}°C" : "n/a"));
        var box = new StackPanel();
        box.Children.Add(wp);
        string hint = s.AvgGpu > 95 ? "Your GPU was maxed out the whole time (GPU-bound). Lower graphics settings or use DLSS/FSR for more FPS."
                    : s.AvgGpu > 0 && s.AvgGpu < 70 ? "Your GPU wasn't fully used, so the CPU or the game engine was the limit (CPU-bound). The Windows CPU tweaks and closing background apps help most here."
                    : "Balanced load. Compare these numbers after turning on tweaks to see what really helped.";
        var h = UI.Text(hint, 12, "Sub"); h.Margin = new Thickness(0, 12, 0, 0);
        box.Children.Add(h);
        _sessionBox.Children.Add(UI.Card(box));
    }

    Popup _popup;

    void OpenPicker()
    {
        if (_popup?.IsOpen == true) { _popup.IsOpen = false; return; }
        var list = new StackPanel { Margin = new Thickness(6) };

        Button Row(string title, string sub, string right, int? pid, bool selected)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            sp.Children.Add(UI.Text(title, 13, "Text", FontWeights.SemiBold, false));
            if (!string.IsNullOrEmpty(sub)) sp.Children.Add(UI.Text(sub, 11, "Dim", wrap: false));
            g.Children.Add(sp);
            if (right != null)
            {
                var r = UI.Text(right, 12, "Good", FontWeights.Bold, false);
                r.VerticalAlignment = VerticalAlignment.Center; r.Margin = new Thickness(16, 0, 0, 0);
                Grid.SetColumn(r, 1); g.Children.Add(r);
            }
            var btn = new Button { Content = g, Style = UI.Res<Style>("MenuRow"), Tag = selected ? "sel" : null };
            btn.Click += (_, _) =>
            {
                LiveMonitor.ManualPid = pid; _fpsGraph.Clear();
                SetPickLabel(pid == null ? "Game: auto-detect" : "Game: " + title);
                _popup.IsOpen = false;
            };
            return btn;
        }

        list.Children.Add(Row("Auto-detect", "Picks the game you're playing", null, null, LiveMonitor.ManualPid == null));
        var presenters = LiveMonitor.PresentersForPicker();
        var head = UI.Label(presenters.Count > 0 ? "DRAWING FRAMES RIGHT NOW" : "NOTHING IS DRAWING DIRECTX FRAMES RIGHT NOW");
        head.Margin = new Thickness(10, 12, 10, 6);
        list.Children.Add(head);
        foreach (var (pid, name, fps) in presenters)
            list.Children.Add(Row(name, null, $"{fps:0} FPS", pid, LiveMonitor.ManualPid == pid));
        if (presenters.Count == 0)
        {
            var hint = UI.Text("Start your game (DirectX games only), then open this list again.", 12, "Sub");
            hint.Margin = new Thickness(10, 0, 10, 8); hint.MaxWidth = 300;
            list.Children.Add(hint);
        }

        _popup = new Popup
        {
            PlacementTarget = _pick, Placement = PlacementMode.Bottom, VerticalOffset = 6, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = UI.Hex("#161B26"), BorderBrush = UI.B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                MinWidth = 300, MaxHeight = 380,
                Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list },
            },
        };
        _popup.IsOpen = true;
    }

    void SetPickLabel(string text)
    {
        if (_pick.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb) tb.Text = text;
    }

    static string Trim(string s) => s.Length > 28 ? s[..28] + "..." : s;

    void Set(string key, List<double> history, string text, string sub)
    {
        var t = _tiles[key];
        t.Value.Text = text;
        t.Value.Foreground = UI.B("Text");
        t.Sub.Text = sub;
        t.Graph.SetAll(history);
    }

    void LoadConnection()
    {
        Task.Run(HwMonitor.GetConnection).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            var c = t.Result;
            _connType.Text = c.Type;
            _connType.Foreground = UI.B(c.Type == "Wi-Fi" ? "Warn" : "Good");
            _connSpeed.Text = c.Speed;
        }));
    }

    public void Refresh() { }
}

/// <summary>
/// In-game overlay: always on top, click-through, never steals focus.
/// Shows FPS, 1% low, live FPS + frame-time graphs and hardware stats while a game is running.
/// </summary>
public class OverlayWindow : Window
{
    readonly TextBlock _fps = new() { FontSize = 26, FontWeight = FontWeights.Black, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00E5FF")), FontFamily = new FontFamily("Segoe UI") };
    readonly TextBlock _sub = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(8, 0, 0, 3), VerticalAlignment = VerticalAlignment.Bottom };
    readonly TextBlock _hw = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 6, 0, 0) };
    readonly Graph _fpsG = new() { Width = 240, Height = 46, Capacity = 60, ShowGrid = false, ShowStats = false, Margin = new Thickness(0, 4, 0, 0) };
    readonly Graph _ftG = new() { Width = 240, Height = 30, Capacity = 160, ShowGrid = false, ShowStats = false, Smooth = false, Margin = new Thickness(0, 4, 0, 0),
        Line = (Color)ColorConverter.ConvertFromString("#2EE59D"), Line2 = (Color)ColorConverter.ConvertFromString("#00E5FF") };

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
        ShowInTaskbar = false; ShowActivated = false; SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
        Left = 12; Top = 12; Focusable = false; IsHitTestVisible = false;
        var sp = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(_fps); top.Children.Add(_sub);
        sp.Children.Add(top);
        sp.Children.Add(Label("FPS"));
        sp.Children.Add(_fpsG);
        sp.Children.Add(Label("FRAME TIME"));
        sp.Children.Add(_ftG);
        sp.Children.Add(_hw);
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(170, 8, 10, 14)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 10), Child = sp,
                               BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0, 229, 255)), BorderThickness = new Thickness(1) };
        _fps.Text = "-- FPS";
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
        };
    }

    static TextBlock Label(string s) => new() { Text = s, FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)), Margin = new Thickness(0, 4, 0, 0), FontFamily = new FontFamily("Segoe UI") };

    public void Update()
    {
        var st = LiveMonitor.Stats; var hw = LiveMonitor.Hw;
        _fps.Text = st != null ? $"{st.Fps:0} FPS" : "-- FPS";
        _sub.Text = st != null ? $"1% low {st.Low1:0}  ·  {st.FrameTimeMs:0.0} ms" : "";
        _fpsG.SetAll(LiveMonitor.FpsHistory);
        _ftG.SetAll(st?.RecentFrameTimes ?? new List<double>());
        if (hw != null)
            _hw.Text = $"CPU {hw.Cpu:0}%" + (hw.Gpu.HasValue ? $"   GPU {hw.Gpu:0}%" : "") + (hw.GpuTemp.HasValue ? $"   {hw.GpuTemp:0}°C" : "") + (hw.PingMs.HasValue ? $"   {hw.PingMs:0} ms" : "");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
}
