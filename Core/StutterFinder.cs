using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Overdrive.Core;

/// <summary>
/// Reads CPU time and disk I/O of every process in one system call (NtQuerySystemInformation).
/// It never opens a handle to any process, so games and anti-cheat never see us.
/// </summary>
public static class ProcSampler
{
    public record struct ProcTimes(int Pid, string Name, long CpuTicks, long IoBytes);

    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    static IntPtr _buf = IntPtr.Zero;
    static int _size = 1 << 20;

    public static List<ProcTimes> Snapshot()
    {
        var list = new List<ProcTimes>(400);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (_buf == IntPtr.Zero) _buf = Marshal.AllocHGlobal(_size);
            int st = NtQuerySystemInformation(5, _buf, _size, out int need);
            if (st == unchecked((int)0xC0000004)) // STATUS_INFO_LENGTH_MISMATCH
            {
                Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; _size = Math.Max(_size * 2, need + 65536);
                continue;
            }
            if (st != 0) return list;
            long off = 0;
            while (true)
            {
                IntPtr p = _buf + (int)off;
                uint next = (uint)Marshal.ReadInt32(p, 0);
                long user = Marshal.ReadInt64(p, 40), kernel = Marshal.ReadInt64(p, 48);
                ushort len = (ushort)Marshal.ReadInt16(p, 56);
                IntPtr namePtr = Marshal.ReadIntPtr(p, 64);
                int pid = (int)Marshal.ReadIntPtr(p, 80);
                long io = Marshal.ReadInt64(p, 232) + Marshal.ReadInt64(p, 240);
                string name = namePtr == IntPtr.Zero || len == 0 ? (pid == 0 ? "Idle" : "System") : Marshal.PtrToStringUni(namePtr, len / 2);
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                list.Add(new ProcTimes(pid, name, user + kernel, io));
                if (next == 0) break;
                off += next;
            }
            return list;
        }
        return list;
    }
}

/// <summary>
/// Records a game for a while and, for every frame-time spike, checks which other programs suddenly used
/// CPU or disk at that exact moment. The result is a ranked list of the programs most likely causing stutter.
/// </summary>
public static class StutterFinder
{
    public record Spike(double AtSec, double FrameMs, string Culprit, string Reason);
    public record Suspect(string Name, int Spikes, double AvgCpu, double AvgIoMb, bool IsGame, bool CanEnd);
    public record Report(string Game, double Seconds, int Frames, double AvgFps, double MedianMs, double WorstMs,
                         List<Spike> Spikes, List<Suspect> Suspects, int Unexplained, List<(double At, double Ms)> Timeline);

    public static bool Active { get; private set; }
    public static string Status { get; private set; } = "";
    public static event Action<Report> Finished;
    public static event Action StatusChanged;

    static CancellationTokenSource _cts;

    record Sample(DateTime At, Dictionary<int, (string Name, long Cpu, long Io)> Procs);

    public static void Start(int seconds)
    {
        if (Active) return;
        Active = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var t = new Thread(() => { try { Run(seconds, ct); } catch (Exception ex) { Store.Log("Stutter finder: " + ex); SetStatus("Something went wrong: " + ex.Message); } finally { Active = false; FpsMonitor.FrameHook = null; StatusChanged?.Invoke(); } })
        { IsBackground = true, Name = "stutter-finder", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    public static void Cancel() => _cts?.Cancel();

    static void SetStatus(string s) { Status = s; StatusChanged?.Invoke(); }

    static void Run(int seconds, CancellationToken ct)
    {
        // 1) wait for a game to be rendering (the live monitor picks it up automatically)
        SetStatus("Waiting for a game... open your game and play normally.");
        int pid = 0; string game = null;
        var waitUntil = DateTime.Now.AddMinutes(5);
        while (!ct.IsCancellationRequested && DateTime.Now < waitUntil)
        {
            if (FpsMonitor.HasTarget) { pid = FpsMonitor.Pid; game = FpsMonitor.ProcessName; break; }
            Thread.Sleep(500);
        }
        if (pid == 0) { SetStatus(ct.IsCancellationRequested ? "Cancelled." : "No game found within 5 minutes."); return; }

        // 2) record frames + process activity
        var frames = new List<DateTime>(seconds * 300);
        var gate = new object();
        FpsMonitor.FrameHook = (p, ts) => { if (p == pid) lock (gate) frames.Add(ts); };
        var samples = new List<Sample>(seconds * 5 + 10);
        var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested && sw.Elapsed.TotalSeconds < seconds)
        {
            var snap = ProcSampler.Snapshot();
            var d = new Dictionary<int, (string, long, long)>(snap.Count);
            foreach (var x in snap) d[x.Pid] = (x.Name, x.CpuTicks, x.IoBytes);
            samples.Add(new Sample(DateTime.Now, d));
            int left = seconds - (int)sw.Elapsed.TotalSeconds;
            int n; lock (gate) n = frames.Count;
            if (samples.Count % 5 == 0) SetStatus($"Recording {Pretty(game)}  ·  {left}s left  ·  {n:N0} frames");
            Thread.Sleep(200);
        }
        FpsMonitor.FrameHook = null;
        if (ct.IsCancellationRequested) { SetStatus("Cancelled."); return; }
        Thread.Sleep(1500); // ETW delivers frames in batches; let the last ones arrive
        List<DateTime> f; lock (gate) f = frames.OrderBy(x => x).ToList();
        if (f.Count < 120) { SetStatus("Not enough frames were captured. Make sure the game is in focus and rendering."); return; }

        var report = Analyze(Pretty(game), pid, f, samples);
        SetStatus("Done.");
        Finished?.Invoke(report);
    }

    static string Pretty(string exe) => Games.All.FirstOrDefault(g => g.ProcessNames.Any(p => p.Equals(exe, StringComparison.OrdinalIgnoreCase)))?.Name ?? exe;

    static Report Analyze(string game, int gamePid, List<DateTime> f, List<Sample> samples)
    {
        var t0 = f[0];
        var ft = new List<double>(f.Count);
        for (int i = 1; i < f.Count; i++) ft.Add((f[i] - f[i - 1]).TotalMilliseconds);
        var sorted = ft.OrderBy(x => x).ToList();
        double median = sorted[sorted.Count / 2];
        double total = (f[^1] - f[0]).TotalSeconds;

        // per-interval deltas for each process (CPU as % of one core, IO in MB)
        var deltas = new List<(DateTime From, DateTime To, Dictionary<int, (string Name, double Cpu, double Io)> P)>();
        for (int i = 1; i < samples.Count; i++)
        {
            var a = samples[i - 1]; var b = samples[i];
            double ms = Math.Max(1, (b.At - a.At).TotalMilliseconds);
            var d = new Dictionary<int, (string, double, double)>();
            foreach (var (p, v) in b.Procs)
            {
                if (p == 0 || !a.Procs.TryGetValue(p, out var o) || o.Name != v.Name) continue;
                double cpu = (v.Cpu - o.Cpu) / 10000.0 / ms * 100; // 100ns ticks -> ms -> % of a core
                double io = (v.Io - o.Io) / 1048576.0;
                if (cpu > 0.5 || io > 0.2) d[p] = (v.Name, cpu, io);
            }
            deltas.Add((a.At, b.At, d));
        }

        // baseline (median) per process name, so always-busy apps don't get blamed for every spike
        var baseCpu = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in deltas.SelectMany(x => x.P.Values).GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            var cpus = g.Select(v => v.Cpu).OrderBy(x => x).ToList();
            // missing intervals count as zero
            int zeros = Math.Max(0, deltas.Count - cpus.Count);
            int idx = (cpus.Count + zeros) / 2 - zeros;
            baseCpu[g.Key] = idx < 0 ? 0 : cpus[Math.Min(idx, cpus.Count - 1)];
        }

        double threshold = Math.Max(median * 2.2, median + 6);
        var spikes = new List<Spike>();
        var blame = new Dictionary<string, List<(double Cpu, double Io)>>(StringComparer.OrdinalIgnoreCase);
        var gameNames = new HashSet<string>(Games.All.SelectMany(g => g.ProcessNames), StringComparer.OrdinalIgnoreCase);
        int unexplained = 0;
        for (int i = 0; i < ft.Count; i++)
        {
            if (ft[i] < threshold) continue;
            var end = f[i + 1]; var start = f[i].AddMilliseconds(-150);
            var hits = deltas.Where(x => x.To >= start && x.From <= end).ToList();
            var score = new Dictionary<string, (double Cpu, double Io, bool Game)>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in hits)
                foreach (var (p, v) in h.P)
                {
                    double extra = v.Cpu - (baseCpu.TryGetValue(v.Name, out var bc) ? bc : 0);
                    score.TryGetValue(v.Name, out var cur);
                    score[v.Name] = (Math.Max(cur.Cpu, extra), Math.Max(cur.Io, v.Io), p == gamePid);
                }
            var best = score.Where(kv => kv.Value.Cpu > 12 || kv.Value.Io > 8)
                            .OrderByDescending(kv => kv.Value.Cpu + kv.Value.Io * 2).FirstOrDefault();
            string culprit = null, reason;
            if (best.Key != null)
            {
                culprit = best.Key;
                reason = best.Value.Game ? "the game itself spiked (loading, shader compiling or streaming)"
                       : best.Value.Io > 8 && best.Value.Io * 2 > best.Value.Cpu ? $"read/wrote {best.Value.Io:0.#} MB of disk"
                       : $"jumped to {best.Value.Cpu:0}% of a CPU core";
                if (!blame.TryGetValue(culprit, out var l)) blame[culprit] = l = new();
                l.Add((best.Value.Cpu, best.Value.Io));
            }
            else { unexplained++; reason = "no program stood out (driver, GPU or network?)"; }
            spikes.Add(new Spike((f[i + 1] - t0).TotalSeconds, ft[i], culprit, reason));
        }

        var gamePidName = samples.LastOrDefault()?.Procs.TryGetValue(gamePid, out var gp) == true ? gp.Name : "";
        var suspects = blame.Select(kv => new Suspect(kv.Key, kv.Value.Count, kv.Value.Average(x => x.Cpu), kv.Value.Average(x => x.Io),
                IsGame: kv.Key.Equals(gamePidName, StringComparison.OrdinalIgnoreCase) || gameNames.Contains(kv.Key),
                CanEnd: !Pages.AppsPage.SystemProcs.Contains(kv.Key) && !Pages.AppsPage.AntiCheat.Contains(kv.Key) && !gameNames.Contains(kv.Key)
                        && !kv.Key.Equals(gamePidName, StringComparison.OrdinalIgnoreCase) && !SelfInstall.IsOurProcess(kv.Key)))
            .OrderByDescending(s => s.Spikes).ThenByDescending(s => s.AvgCpu).ToList();

        // timeline: downsample to at most ~600 points keeping the worst frame in each bucket
        var timeline = new List<(double, double)>();
        int bucket = Math.Max(1, ft.Count / 600);
        for (int i = 0; i < ft.Count; i += bucket)
        {
            double worst = 0; int wi = i;
            for (int j = i; j < Math.Min(ft.Count, i + bucket); j++) if (ft[j] > worst) { worst = ft[j]; wi = j; }
            timeline.Add(((f[wi + 1] - t0).TotalSeconds, worst));
        }

        return new Report(game, total, f.Count, (f.Count - 1) / Math.Max(0.001, total), median, sorted[^1], spikes, suspects, unexplained, timeline);
    }
}

/// <summary>
/// Driver latency check (like LatencyMon): records how long each driver's DPC and interrupt routines run.
/// Long-running driver code blocks the CPU and causes stutter, audio crackle and input lag.
/// </summary>
public static class LatencyCheck
{
    public record DriverStat(string Driver, string Friendly, string Fix, long Count, double MaxUs, double AvgUs, double TotalMs);
    public record Result(double Seconds, double MaxUs, string MaxDriver, List<DriverStat> Drivers, long Events);

    [DllImport("psapi.dll", SetLastError = true)] static extern bool EnumDeviceDrivers([Out] IntPtr[] addrs, int cb, out int needed);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)] static extern int GetDeviceDriverBaseNameW(IntPtr addr, System.Text.StringBuilder name, int size);

    static (ulong Base, string Name)[] Drivers()
    {
        EnumDeviceDrivers(null, 0, out int need);
        var arr = new IntPtr[need / IntPtr.Size + 16];
        if (!EnumDeviceDrivers(arr, arr.Length * IntPtr.Size, out need)) return Array.Empty<(ulong, string)>();
        var list = new List<(ulong, string)>();
        var sb = new System.Text.StringBuilder(260);
        for (int i = 0; i < need / IntPtr.Size; i++)
        {
            if (arr[i] == IntPtr.Zero) continue;
            sb.Clear();
            if (GetDeviceDriverBaseNameW(arr[i], sb, 260) > 0) list.Add(((ulong)arr[i].ToInt64(), sb.ToString()));
        }
        return list.OrderBy(x => x.Item1).ToArray();
    }

    static string Lookup((ulong Base, string Name)[] d, ulong addr)
    {
        int lo = 0, hi = d.Length - 1, best = -1;
        while (lo <= hi) { int m = (lo + hi) / 2; if (d[m].Base <= addr) { best = m; lo = m + 1; } else hi = m - 1; }
        return best >= 0 ? d[best].Name : "unknown";
    }

    public static (string Friendly, string Fix) Describe(string drv)
    {
        string n = drv.ToLowerInvariant();
        if (n.StartsWith("nvlddmkm")) return ("NVIDIA graphics driver", "Update or clean-reinstall the NVIDIA driver. Very high values can also come from an overclock or a bad driver version.");
        if (n.StartsWith("amdkmdag") || n.StartsWith("atikmdag") || n.StartsWith("amdkmpfd")) return ("AMD graphics driver", "Update or clean-reinstall the AMD driver (AMD Cleanup Utility, then the newest Adrenalin).");
        if (n.StartsWith("igdkmd")) return ("Intel graphics driver", "Update the Intel graphics driver.");
        if (n.StartsWith("dxgkrnl")) return ("DirectX graphics kernel", "Usually follows the GPU driver. Update your graphics driver.");
        if (n.StartsWith("ndis") || n.StartsWith("tcpip") || n.StartsWith("netio") || n.StartsWith("afd")) return ("Windows networking", "Network adapter activity. Update the network driver, or use Ethernet instead of Wi-Fi while gaming.");
        if (n.StartsWith("netwtw") || n.StartsWith("netwlv") || n.StartsWith("netwbw") || n.StartsWith("rtwlane") || n.StartsWith("rtwlanu") || n.StartsWith("athw") || n.StartsWith("mtkwl") || n.StartsWith("bcmwl") || n.StartsWith("qcwlan"))
            return ("Wi-Fi adapter driver", "Wi-Fi drivers are a top cause of stutter. Update it from the PC/Wi-Fi maker, turn on 'Pause Wi-Fi scans in games', or use Ethernet.");
        if (n.StartsWith("rt640") || n.StartsWith("rt68") || n.StartsWith("rtux") || n.StartsWith("e1d") || n.StartsWith("e1r") || n.StartsWith("e2f") || n.StartsWith("ixgbe") || n.StartsWith("kill"))
            return ("Ethernet adapter driver", "Update the network driver from the motherboard maker's site.");
        if (n.StartsWith("hdaudbus") || n.StartsWith("hdaudio") || n.StartsWith("rtkvhd") || n.StartsWith("portcls") || n.StartsWith("ks.sys") || n.StartsWith("nvhda") || n.StartsWith("atihdw"))
            return ("Audio driver", "Update the audio driver. Disabling unused audio devices (e.g. monitor/NVIDIA HDMI audio) can help.");
        if (n.StartsWith("usbxhci") || n.StartsWith("ucx01000") || n.StartsWith("usbport") || n.StartsWith("usbhub")) return ("USB controller", "Unplug USB devices you don't need, and plug the mouse/keyboard into ports directly on the motherboard.");
        if (n.StartsWith("storport") || n.StartsWith("stornvme") || n.StartsWith("storahci") || n.StartsWith("iastor") || n.StartsWith("ataport")) return ("Storage (SSD/HDD) driver", "Disk activity. Close downloads/updates while gaming; update chipset/storage drivers.");
        if (n.StartsWith("acpi") || n.StartsWith("intelppm") || n.StartsWith("amdppm") || n.StartsWith("processr")) return ("Power management (ACPI/CPU power)", "Use the Ultimate/High performance power plan (Windows tab) and update the BIOS and chipset driver.");
        if (n.StartsWith("ntoskrnl") || n.StartsWith("hal")) return ("Windows kernel", "Often timers and power management. Use a performance power plan; usually not a problem by itself.");
        if (n.StartsWith("wdf01000")) return ("Driver framework (used by many devices)", "Some device driver is busy through the Windows driver framework. Check USB devices and RGB/peripheral software.");
        if (n.StartsWith("dxgmms")) return ("Graphics memory manager", "Usually follows the GPU driver. Update your graphics driver.");
        if (n.StartsWith("bthport") || n.StartsWith("bthusb")) return ("Bluetooth", "Turn Bluetooth off while gaming if you don't use it.");
        return ("", "");
    }

    public static Task<Result> RunAsync(int seconds, IProgress<double> progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var drivers = Drivers();
            var stats = new Dictionary<string, (long Count, double Max, double Sum)>(StringComparer.OrdinalIgnoreCase);
            long events = 0;
            const string name = "PCOptimizer-Latency";
            try { using var old = Microsoft.Diagnostics.Tracing.Session.TraceEventSession.GetActiveSession(name); old?.Stop(true); } catch { }
            using var session = new Microsoft.Diagnostics.Tracing.Session.TraceEventSession(name) { StopOnDispose = true, BufferSizeMB = 16 };
            session.EnableKernelProvider(Microsoft.Diagnostics.Tracing.Parsers.KernelTraceEventParser.Keywords.DeferedProcedureCalls
                                       | Microsoft.Diagnostics.Tracing.Parsers.KernelTraceEventParser.Keywords.Interrupt);
            void Add(ulong routine, double ms)
            {
                var drv = Lookup(drivers, routine);
                double us = ms * 1000;
                stats.TryGetValue(drv, out var s);
                stats[drv] = (s.Count + 1, Math.Max(s.Max, us), s.Sum + us);
                events++;
            }
            var k = session.Source.Kernel;
            k.PerfInfoDPC += d => Add(d.Routine, d.ElapsedTimeMSec);
            k.PerfInfoTimerDPC += d => Add(d.Routine, d.ElapsedTimeMSec);
            k.PerfInfoThreadedDPC += d => Add(d.Routine, d.ElapsedTimeMSec);
            k.PerfInfoISR += d => Add(d.Routine, d.ElapsedTimeMSec);
            var sw = Stopwatch.StartNew();
            var timer = new Thread(() =>
            {
                while (sw.Elapsed.TotalSeconds < seconds && !ct.IsCancellationRequested) { progress?.Report(sw.Elapsed.TotalSeconds / seconds); Thread.Sleep(200); }
                try { session.Source.StopProcessing(); } catch { }
            }) { IsBackground = true };
            timer.Start();
            session.Source.Process();
            ct.ThrowIfCancellationRequested();
            var list = stats.Select(kv =>
            {
                var (f, fix) = Describe(kv.Key);
                return new DriverStat(kv.Key, f, fix, kv.Value.Count, kv.Value.Max, kv.Value.Sum / Math.Max(1, kv.Value.Count), kv.Value.Sum / 1000);
            }).OrderByDescending(x => x.MaxUs).ToList();
            var top = list.FirstOrDefault();
            return new Result(sw.Elapsed.TotalSeconds, top?.MaxUs ?? 0, top?.Driver, list, events);
        }, ct);
    }
}
