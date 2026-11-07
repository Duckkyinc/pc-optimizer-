using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Overdrive.Core;

/// <summary>
/// Real FPS / frame-time capture using the same ETW events PresentMon uses (DXGI + D3D9 "Present" events).
/// Watches every process that draws frames, then reports for the chosen target. Read-only: never touches any game.
/// </summary>
public static class FpsMonitor
{
    static readonly Guid DXGI = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    const string SessionName = "PCOptimizer-FPS";

    class Track { public readonly List<double> Frames = new(); public double LastTs; public long LastWall; }

    static TraceEventSession _session;
    static Thread _thread;
    static readonly object Gate = new();
    static readonly Dictionary<int, Track> _tracks = new();

    public static int Pid { get; private set; }
    public static string ProcessName { get; private set; }
    public static bool Running => _session != null;
    public static bool HasTarget => Pid != 0;
    public static string Error { get; private set; }
    public static long EventsSeen;
    /// <summary>Optional listener for every presented frame (pid, absolute time). Used by the Stutter Finder.</summary>
    public static Action<int, DateTime> FrameHook;

    public static void Start()
    {
        if (Running) return;
        Error = null;
        try
        {
            try { using var old = TraceEventSession.GetActiveSession(SessionName); old?.Stop(true); } catch { }
            var s = new TraceEventSession(SessionName) { StopOnDispose = true, BufferSizeMB = 4 };
            // only the "frame presented" events, nothing else (keeps the cost in-game near zero)
            s.EnableProvider(DXGI, TraceEventLevel.Informational, ulong.MaxValue, new TraceEventProviderOptions { EventIDsToEnable = new List<int> { 42 } });
            s.EnableProvider(D3D9, TraceEventLevel.Informational, ulong.MaxValue, new TraceEventProviderOptions { EventIDsToEnable = new List<int> { 1 } });
            s.Source.AllEvents += OnEvent;
            _session = s;
            _thread = new Thread(() => { try { s.Source.Process(); } catch (Exception ex) { Error = ex.Message; Store.Log("FPS capture stopped: " + ex); } }) { IsBackground = true, Name = "fps-etw" };
            _thread.Start();
            Store.Log("FPS capture started");
        }
        catch (Exception ex)
        {
            Error = ex.Message.Contains("Access", StringComparison.OrdinalIgnoreCase) ? "needs administrator rights" : ex.Message;
            Store.Log("FPS capture failed to start: " + ex);
            _session = null;
        }
    }

    public static void SetTarget(int pid, string name)
    {
        Pid = pid; ProcessName = name;
    }

    public static void ClearTarget() { Pid = 0; ProcessName = null; }

    static void OnEvent(TraceEvent e)
    {
        bool present = (e.ProviderGuid == DXGI && (int)e.ID == 42) || (e.ProviderGuid == D3D9 && (int)e.ID == 1);
        if (!present) return;
        Interlocked.Increment(ref EventsSeen);
        int pid = e.ProcessID;
        var ts = e.TimeStampRelativeMSec;
        FrameHook?.Invoke(pid, e.TimeStamp);
        lock (Gate)
        {
            if (!_tracks.TryGetValue(pid, out var t)) { if (_tracks.Count > 64) Prune(); _tracks[pid] = t = new Track(); }
            if (ts <= t.LastTs) return;
            t.LastTs = ts; t.LastWall = Environment.TickCount64;
            t.Frames.Add(ts);
            if (t.Frames.Count > 4000 && t.Frames[0] < ts - 12000) t.Frames.RemoveRange(0, t.Frames.Count / 4);
        }
    }

    static void Prune()
    {
        var now = Environment.TickCount64;
        foreach (var k in _tracks.Where(kv => now - kv.Value.LastWall > 10000).Select(kv => kv.Key).ToList()) _tracks.Remove(k);
    }

    /// <summary>Processes that presented frames in the last 2 seconds, busiest first: (pid, fps).</summary>
    public static List<(int Pid, double Fps)> ActivePresenters()
    {
        var now = Environment.TickCount64;
        lock (Gate)
            return _tracks.Where(kv => now - kv.Value.LastWall < 2000 && kv.Value.Frames.Count > 5)
                .Select(kv => (kv.Key, FpsOf(kv.Value))).OrderByDescending(x => x.Item2).ToList();
    }

    static double FpsOf(Track t)
    {
        if (t.Frames.Count < 2) return 0;
        double now = t.Frames[^1];
        int i = t.Frames.FindIndex(x => x >= now - 1000);
        int n = t.Frames.Count - i;
        return n > 1 ? (n - 1) * 1000.0 / Math.Max(1, now - t.Frames[i]) : 0;
    }

    public record Stats(double Fps, double Low1, double FrameTimeMs, double MaxFrameTimeMs, List<double> RecentFrameTimes);

    /// <summary>FPS over the last second, 1% low over the last 10s, for the current target.</summary>
    public static Stats Read()
    {
        if (Pid == 0) return null;
        lock (Gate)
        {
            if (!_tracks.TryGetValue(Pid, out var t) || t.Frames.Count < 3) return null;
            if (Environment.TickCount64 - t.LastWall > 2000) return null; // no frames recently (minimized / loading / not DirectX)
            var f = t.Frames;
            double fps = FpsOf(t);
            double now = f[^1];
            int i10 = f.FindIndex(x => x >= now - 10000);
            var ft = new List<double>();
            for (int i = Math.Max(1, i10); i < f.Count; i++) ft.Add(f[i] - f[i - 1]);
            if (ft.Count == 0) return null;
            var sorted = ft.OrderByDescending(x => x).ToList();
            int worst = Math.Max(1, sorted.Count / 100);
            double low1 = 1000.0 / sorted.Take(worst).Average();
            var recent = ft.Skip(Math.Max(0, ft.Count - 240)).ToList();
            return new Stats(fps, low1, 1000.0 / Math.Max(fps, 0.001), sorted[0], recent);
        }
    }

    public static void Stop()
    {
        try { _session?.Dispose(); } catch { }
        _session = null; _thread = null; ClearTarget();
        lock (Gate) _tracks.Clear();
        try { using var old = TraceEventSession.GetActiveSession(SessionName); old?.Stop(true); } catch { }
    }
}

/// <summary>CPU, GPU, VRAM, temperature and ping sampling.</summary>
public static class HwMonitor
{
    public record Sample(double Cpu, double Ram, double RamUsedGb, double? Gpu, double? GpuTemp, double? VramUsedGb, double? VramTotalGb,
                         double? GpuClockMhz, double? GpuPowerW, double? PingMs);

    static PerformanceCounterCategory _gpuEngine, _gpuMem;
    static readonly string NvSmi = new[]
    {
        System.IO.Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
        @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
    }.FirstOrDefault(System.IO.File.Exists);

    static readonly SysInfo.CpuSampler CpuSampler = new();
    static double? _ping;
    static DateTime _lastPing;
    static bool _pinging;

    // A single nvidia-smi that keeps running and prints one line per second (much cheaper than starting it every second).
    static Process _nvProc;
    static volatile string _nvLine;
    static long _nvAt, _nvLastUse;
    static System.Threading.Timer _nvIdle;

    static void EnsureNvSmi()
    {
        _nvLastUse = Environment.TickCount64;
        if (_nvProc != null && !_nvProc.HasExited) return;
        try
        {
            var psi = new ProcessStartInfo(NvSmi, "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total,clocks.gr,power.draw --format=csv,noheader,nounits -lms 1000")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            _nvProc = Process.Start(psi);
            try { _nvProc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            _nvProc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { _nvLine = e.Data; _nvAt = Environment.TickCount64; } };
            _nvProc.BeginOutputReadLine();
            // stop it when nobody has asked for stats for a while
            _nvIdle ??= new System.Threading.Timer(_ =>
            {
                if (Environment.TickCount64 - _nvLastUse > 4000) StopNvSmi();
            }, null, 3000, 3000);
        }
        catch (Exception ex) { Store.Log("nvidia-smi failed: " + ex.Message); _nvProc = null; }
    }

    public static void StopNvSmi()
    {
        try { if (_nvProc != null && !_nvProc.HasExited) _nvProc.Kill(); } catch { }
        try { _nvProc?.Dispose(); } catch { }
        _nvProc = null;
    }

    static List<PerformanceCounter> _engineCounters;
    static DateTime _engineRefresh;

    public static Sample Read()
    {
        var cpu = SysInfo.CpuUsage(CpuSampler);
        var (ramUsed, ramPct) = SysInfo.RamUsage();
        double? gpu = null, temp = null, vram = null, vramTotal = null, clock = null, power = null;

        if (NvSmi != null && SysInfo.ShowNvidia)
        {
            EnsureNvSmi();
            var parts = (_nvLine ?? "").Split(',').Select(p => p.Trim()).ToArray();
            double? P(int i) => i < parts.Length && double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
            if (Environment.TickCount64 - _nvAt < 5000)
            {
                gpu = P(0); temp = P(1); vram = P(2) / 1024; vramTotal = P(3) / 1024; clock = P(4); power = P(5);
            }
        }
        gpu ??= GpuUtilFromCounters();
        vram ??= VramFromCounters();

        if (!_pinging && (DateTime.Now - _lastPing).TotalSeconds >= 2)
        {
            _pinging = true; _lastPing = DateTime.Now;
            _ = Task.Run(async () =>
            {
                try { using var p = new Ping(); var rep = await p.SendPingAsync("1.1.1.1", 1500); _ping = rep.Status == IPStatus.Success ? rep.RoundtripTime : null; }
                catch { _ping = null; }
                finally { _pinging = false; }
            });
        }
        return new Sample(cpu, ramPct, ramUsed, gpu, temp, vram, vramTotal, clock, power, _ping);
    }

    static double? GpuUtilFromCounters()
    {
        try
        {
            _gpuEngine ??= new PerformanceCounterCategory("GPU Engine");
            if (_engineCounters == null || (DateTime.Now - _engineRefresh).TotalSeconds > 30)
            {
                foreach (var c in _engineCounters ?? new()) c.Dispose();
                _engineCounters = _gpuEngine.GetInstanceNames().Where(n => n.EndsWith("engtype_3D"))
                    .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true)).ToList();
                foreach (var c in _engineCounters) c.NextValue(); // prime
                _engineRefresh = DateTime.Now;
                return null;
            }
            double total = 0;
            foreach (var c in _engineCounters) { try { total += c.NextValue(); } catch { } }
            return Math.Min(100, total);
        }
        catch { return null; }
    }

    static double? VramFromCounters()
    {
        try
        {
            _gpuMem ??= new PerformanceCounterCategory("GPU Adapter Memory");
            double max = 0;
            foreach (var n in _gpuMem.GetInstanceNames())
            {
                using var c = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", n, true);
                max = Math.Max(max, c.NextValue());
            }
            return max > 0 ? max / 1024 / 1024 / 1024 : null;
        }
        catch { return null; }
    }

    // ---- connection info (shown blurred in Streamer mode) ----
    public record Connection(string Type, string Name, string LocalIp, string Adapter, string Speed);

    public static Connection GetConnection()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)))
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                .FirstOrDefault();
            if (nic == null) return new Connection("Offline", "-", "-", "-", "-");
            var ip = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "-";
            bool wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            string name = wifi ? Ssid() ?? "Wi-Fi" : "Ethernet cable";
            string speed = nic.Speed > 0 ? (nic.Speed >= 1_000_000_000 ? $"{nic.Speed / 1_000_000_000.0:0.#} Gbps" : $"{nic.Speed / 1_000_000} Mbps") : "-";
            return new Connection(wifi ? "Wi-Fi" : "Ethernet", name, ip, nic.Description, speed);
        }
        catch { return new Connection("Unknown", "-", "-", "-", "-"); }
    }

    static string Ssid()
    {
        try
        {
            var o = Shell.Run("netsh", "wlan show interfaces", 4000).Output;
            var m = Regex.Match(o, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        catch { return null; }
    }
}
