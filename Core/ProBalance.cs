using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace Overdrive.Core;

/// <summary>
/// ProBalance: watches CPU use every second while you play. When a background app suddenly hogs the CPU
/// (antivirus scan, browser tab, updater...), it temporarily drops that app to Below Normal priority so the
/// game always wins the CPU, then puts it back as soon as it calms down. Never touches the game, Windows
/// itself, anti-cheat, voice chat or streaming apps.
/// </summary>
public static class ProBalance
{
    public record Restraint(string Name, DateTime At, double Cpu);

    static Thread _thread;
    static volatile bool _run;
    static readonly Dictionary<int, (string Name, ProcessPriorityClass Old, int Calm)> _restrained = new();
    static readonly Dictionary<int, int> _hot = new();
    public static readonly List<Restraint> Log = new();
    public static event Action Changed;
    public static bool Running => _run;
    public static int ActiveCount { get { lock (_restrained) return _restrained.Count; } }

    // Apps where lower priority could hurt you (voice crackle, stream drops) or that we must never touch.
    static readonly HashSet<string> Never = new(StringComparer.OrdinalIgnoreCase)
    {
        "audiodg", "dwm", "explorer", "csrss", "lsass", "winlogon", "services", "smss", "wininit", "System", "Idle", "Registry",
        "Memory Compression", "Secure System", "MsMpEng", "ctfmon", "Discord", "TeamSpeak", "ts3client_win64", "Mumble", "obs64", "obs32",
        "Streamlabs OBS", "XSplit.Core", "NVIDIA Share", "nvcontainer", "Medal", "Overwolf",
    };

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public static void Start()
    {
        if (_run || !Store.Settings.ProBalance) return;
        _run = true;
        lock (Log) Log.Clear();
        _thread = new Thread(Loop) { IsBackground = true, Name = "probalance", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public static void Stop()
    {
        _run = false;
        RestoreAll();
    }

    static void Loop()
    {
        var games = new HashSet<string>(Games.All.SelectMany(g => g.ProcessNames), StringComparer.OrdinalIgnoreCase);
        int threads = Math.Max(1, SysInfo.Threads > 0 ? SysInfo.Threads : Environment.ProcessorCount);
        Dictionary<int, (string Name, long Cpu)> prev = null;
        long prevAt = 0;
        while (_run)
        {
            try
            {
                var snap = ProcSampler.Snapshot();
                long now = Stopwatch.GetTimestamp();
                if (prev != null)
                {
                    double ms = (now - prevAt) * 1000.0 / Stopwatch.Frequency;
                    GetWindowThreadProcessId(GetForegroundWindow(), out var fg);
                    double totalBusy = 0;
                    var loads = new List<(int Pid, string Name, double Pct)>();
                    foreach (var p in snap)
                    {
                        if (p.Pid <= 4 || !prev.TryGetValue(p.Pid, out var o) || o.Name != p.Name) continue;
                        double pct = (p.CpuTicks - o.Cpu) / 10000.0 / ms * 100 / threads; // % of the whole CPU
                        totalBusy += pct;
                        loads.Add((p.Pid, p.Name, pct));
                    }
                    foreach (var (pid, name, pct) in loads)
                    {
                        bool exempt = pid == fg || pid == Environment.ProcessId || games.Contains(name) || Never.Contains(name)
                                      || Pages.AppsPage.SystemProcs.Contains(name) || Pages.AppsPage.AntiCheat.Contains(name) || SelfInstall.IsOurProcess(name);
                        lock (_restrained)
                        {
                            if (_restrained.TryGetValue(pid, out var r))
                            {
                                int calm = pct < 2 ? r.Calm + 1 : 0;
                                if (calm >= 5) Restore(pid); else _restrained[pid] = (r.Name, r.Old, calm);
                                continue;
                            }
                        }
                        if (exempt) continue;
                        // hogging: over 8% of the whole CPU twice in a row while the PC is busy
                        if (pct > 8 && totalBusy > 35)
                        {
                            _hot[pid] = _hot.TryGetValue(pid, out var h) ? h + 1 : 1;
                            if (_hot[pid] >= 2) { Restrain(pid, name, pct); _hot.Remove(pid); }
                        }
                        else _hot.Remove(pid);
                    }
                    // forget processes that exited
                    lock (_restrained)
                        foreach (var gone in _restrained.Keys.Where(k => !snap.Any(s => s.Pid == k)).ToList()) _restrained.Remove(gone);
                }
                prev = snap.GroupBy(s => s.Pid).ToDictionary(g => g.Key, g => (g.First().Name, g.First().CpuTicks));
                prevAt = now;
            }
            catch (Exception ex) { Store.Log("ProBalance: " + ex.Message); }
            Thread.Sleep(1000);
        }
    }

    static void Restrain(int pid, string name, double pct)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var old = p.PriorityClass;
            if (old is not (ProcessPriorityClass.Normal or ProcessPriorityClass.AboveNormal or ProcessPriorityClass.High)) return;
            p.PriorityClass = ProcessPriorityClass.BelowNormal;
            lock (_restrained) _restrained[pid] = (name, old, 0);
            lock (Log) { Log.Add(new Restraint(name, DateTime.Now, pct)); if (Log.Count > 200) Log.RemoveAt(0); }
            Changed?.Invoke();
        }
        catch { /* access denied or exited: leave it */ }
    }

    static void Restore(int pid)
    {
        (string Name, ProcessPriorityClass Old, int Calm) r;
        lock (_restrained) { if (!_restrained.Remove(pid, out r)) return; }
        try { using var p = Process.GetProcessById(pid); if (p.PriorityClass == ProcessPriorityClass.BelowNormal) p.PriorityClass = r.Old; } catch { }
        Changed?.Invoke();
    }

    static void RestoreAll()
    {
        int[] pids; lock (_restrained) pids = _restrained.Keys.ToArray();
        foreach (var pid in pids) Restore(pid);
    }

    /// <summary>Summary like "chrome x5, MsMpEng x2" for the boost status.</summary>
    public static string Summary()
    {
        lock (Log)
            return string.Join(", ", Log.GroupBy(l => l.Name).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} x{g.Count()}"));
    }
}

/// <summary>
/// Traffic Shaper: while you play, slows down apps that flood your connection (game launchers, cloud sync,
/// torrents) and Windows Update, so your game's packets don't wait in line. Everything is lifted when the
/// game closes, and the limits are temporary (they vanish on restart even if something goes wrong).
/// </summary>
public static class TrafficShaper
{
    public record App(string Exe, string Friendly);

    public static readonly App[] Apps =
    {
        new("steam.exe", "Steam"), new("EpicGamesLauncher.exe", "Epic Games"), new("Battle.net.exe", "Battle.net"), new("Agent.exe", "Battle.net updater"),
        new("EADesktop.exe", "EA app"), new("EABackgroundService.exe", "EA app updater"), new("upc.exe", "Ubisoft Connect"), new("UbisoftConnect.exe", "Ubisoft Connect"),
        new("GalaxyClient.exe", "GOG Galaxy"), new("XboxPcApp.exe", "Xbox app"), new("gamingservices.exe", "Xbox / Microsoft Store downloads"),
        new("OneDrive.exe", "OneDrive"), new("Dropbox.exe", "Dropbox"), new("GoogleDriveFS.exe", "Google Drive"), new("MEGAsync.exe", "MEGA"), new("iCloudDrive.exe", "iCloud"),
        new("qbittorrent.exe", "qBittorrent"), new("uTorrent.exe", "uTorrent"), new("BitTorrent.exe", "BitTorrent"), new("deluge.exe", "Deluge"), new("transmission-qt.exe", "Transmission"),
    };

    /// <summary>Upload cap per app while gaming. Capping upload also slows their downloads (TCP needs uploads to acknowledge data).</summary>
    const long CapBits = 512_000;
    const string Prefix = "PC Optimizer Shaper";
    const string DoKey = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";
    static bool _on;
    static object _fgOld, _bgOld;

    public static bool Active => _on;

    public static void Begin()
    {
        if (_on || !Store.Settings.TrafficShaper) return;
        _on = true;
        // 1) launchers, sync and torrent apps: temporary QoS throttle (ActiveStore = gone after a restart)
        var cmds = Apps.Select(a => $"New-NetQosPolicy -Name '{Prefix} {a.Exe}' -AppPathNameMatchCondition '{a.Exe}' -ThrottleRateActionBitsPerSecond {CapBits} -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Out-Null");
        RunPs($"Remove-NetQosPolicy -Name '{Prefix}*' -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue; " + string.Join("; ", cmds));
        // 2) Windows Update / Store downloads: cap Delivery Optimization to 10% of your bandwidth
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(DoKey, true);
            _fgOld = k.GetValue("DOPercentageMaxForegroundBandwidth"); _bgOld = k.GetValue("DOPercentageMaxBackgroundBandwidth");
            k.SetValue("DOPercentageMaxForegroundBandwidth", 10, RegistryValueKind.DWord);
            k.SetValue("DOPercentageMaxBackgroundBandwidth", 10, RegistryValueKind.DWord);
        }
        catch (Exception ex) { Store.Log("Traffic shaper (Windows Update): " + ex.Message); }
        Store.Log("Traffic shaper on");
    }

    public static void End()
    {
        if (!_on) return;
        _on = false;
        RunPs($"Remove-NetQosPolicy -Name '{Prefix}*' -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue");
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(DoKey, true);
            void Put(string n, object v) { if (v == null) k.DeleteValue(n, false); else k.SetValue(n, v, RegistryValueKind.DWord); }
            Put("DOPercentageMaxForegroundBandwidth", _fgOld); Put("DOPercentageMaxBackgroundBandwidth", _bgOld);
        }
        catch { }
        Store.Log("Traffic shaper off");
    }

    /// <summary>Clean up limits left behind if the app was closed mid-game.</summary>
    public static void Cleanup() => RunPs($"Remove-NetQosPolicy -Name '{Prefix}*' -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue", wait: false);

    static void RunPs(string script, bool wait = true)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            if (wait) p?.WaitForExit(15000);
        }
        catch (Exception ex) { Store.Log("Traffic shaper: " + ex.Message); }
    }
}
