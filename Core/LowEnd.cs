using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace Overdrive.Core;

/// <summary>
/// Extra boosts for low-end PCs (few CPU cores, 8 GB RAM, integrated graphics). On these PCs, background
/// Windows work steals a real share of the CPU and memory, so pausing it during games gives real FPS.
/// Everything is temporary: it all goes back when the game closes. Games are never touched.
/// </summary>
public static class LowEnd
{
    public static bool Enabled => Store.Settings.LowEndMode ?? IsLowEndPc;

    public static bool IsLowEndPc =>
        SysInfo.RamGb > 0 && SysInfo.RamGb <= 8.5
        || SysInfo.Threads > 0 && SysInfo.Threads <= 4
        || IntegratedOnly;

    public static bool IntegratedOnly =>
        SysInfo.Gpus.Count > 0 && SysInfo.Gpus.All(g =>
            g.Name?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true && !g.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(g.Name ?? "", @"Radeon\(TM\) Graphics|Radeon Graphics|Vega \d+ Graphics|Radeon \d+M", RegexOptions.IgnoreCase));

    public static string Why()
    {
        var r = new List<string>();
        if (SysInfo.RamGb > 0 && SysInfo.RamGb <= 8.5) r.Add($"{SysInfo.RamGb:0} GB RAM");
        if (SysInfo.Threads > 0 && SysInfo.Threads <= 4) r.Add($"{SysInfo.Threads} CPU threads");
        if (IntegratedOnly) r.Add("integrated graphics");
        return string.Join(", ", r);
    }

    // ------------------------------------------------------------------ background services
    /// <summary>Services that do heavy background work (indexing, updates, prefetching, telemetry). Safe to pause for a game session.</summary>
    static readonly (string Name, string Friendly)[] Services =
    {
        ("SysMain", "SysMain (prefetching)"),
        ("WSearch", "Windows Search indexing"),
        ("wuauserv", "Windows Update"),
        ("UsoSvc", "Update Orchestrator"),
        ("DoSvc", "Delivery Optimization"),
        ("DiagTrack", "Telemetry"),
        ("WerSvc", "Error reporting"),
        ("MapsBroker", "Offline maps updates"),
    };
    static readonly List<string> _stopped = new();

    public static int PauseServices()
    {
        int n = 0;
        foreach (var (name, _) in Services)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status != ServiceControllerStatus.Running || !sc.CanStop) continue;
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                lock (_stopped) _stopped.Add(name);
                n++;
            }
            catch (Exception ex) { Store.Log($"Low-end: couldn't pause {name}: {ex.Message}"); }
        }
        if (n > 0) Store.Log($"Low-end: paused {n} background services");
        return n;
    }

    public static void ResumeServices()
    {
        string[] list; lock (_stopped) { list = _stopped.ToArray(); _stopped.Clear(); }
        foreach (var name in list)
        {
            try { using var sc = new ServiceController(name); if (sc.Status == ServiceControllerStatus.Stopped) sc.Start(); }
            catch (Exception ex) { Store.Log($"Low-end: couldn't restart {name}: {ex.Message}"); }
        }
    }

    // ------------------------------------------------------------------ background app priority
    /// <summary>Common apps that keep running next to a game. They get lower CPU priority so the game wins every time.</summary>
    static readonly string[] BackgroundApps =
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "msedgewebview2",
        "Discord", "Spotify", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "RiotClientServices", "RiotClientUx",
        "OneDrive", "Teams", "ms-teams", "Slack", "Telegram", "WhatsApp", "Zoom", "Dropbox", "GoogleDriveFS",
        "Overwolf", "Medal", "obs64", "SearchHost", "SearchApp", "PhoneExperienceHost", "Widgets", "WidgetService",
        "NVIDIA Share", "nvcontainer", "RadeonSoftware", "AMDRSServ", "Razer Synapse 3", "RazerCentralService", "lghub", "iCUE",
    };
    static readonly List<(int Pid, ProcessPriorityClass Old)> _lowered = new();

    public static int LowerBackgroundApps()
    {
        int n = 0;
        foreach (var name in BackgroundApps)
        {
            Process[] ps; try { ps = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in ps)
            {
                try
                {
                    var old = p.PriorityClass;
                    if (old is ProcessPriorityClass.Normal or ProcessPriorityClass.AboveNormal)
                    {
                        p.PriorityClass = ProcessPriorityClass.BelowNormal;
                        lock (_lowered) _lowered.Add((p.Id, old));
                        n++;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        return n;
    }

    public static void RestoreBackgroundApps()
    {
        (int, ProcessPriorityClass)[] list; lock (_lowered) { list = _lowered.ToArray(); _lowered.Clear(); }
        foreach (var (pid, old) in list)
        {
            try { using var p = Process.GetProcessById(pid); if (p.PriorityClass == ProcessPriorityClass.BelowNormal) p.PriorityClass = old; } catch { }
        }
    }

    // ------------------------------------------------------------------ RAM
    [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string sys, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, int len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attr; }

    static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out var tok)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attr = 2 };
            return AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(tok); }
    }

    public static double FreeRamPercent()
    {
        var m = new Native.MEMORYSTATUSEX();
        return Native.GlobalMemoryStatusEx(m) ? 100 - m.dwMemoryLoad : 100;
    }

    /// <summary>
    /// Empties Windows' standby memory list (cached files) so the game gets free RAM right away instead of
    /// waiting for Windows to hand it over mid-match. Only matters when RAM is nearly full. Returns MB freed.
    /// </summary>
    public static double FreeStandbyRam()
    {
        var before = new Native.MEMORYSTATUSEX(); Native.GlobalMemoryStatusEx(before);
        EnablePrivilege("SeProfileSingleProcessPrivilege");
        int cmd = 4; // MemoryPurgeStandbyList
        int st = NtSetSystemInformation(80, ref cmd, sizeof(int)); // SystemMemoryListInformation
        if (st != 0) { Store.Log($"Free RAM failed: 0x{st:X}"); return 0; }
        var after = new Native.MEMORYSTATUSEX(); Native.GlobalMemoryStatusEx(after);
        return Math.Max(0, ((double)after.ullAvailPhys - before.ullAvailPhys) / 1048576.0);
    }

    // ------------------------------------------------------------------ game session hooks
    static Timer _ramTimer;

    public static List<string> Begin()
    {
        var notes = new List<string>();
        int s = PauseServices(); if (s > 0) notes.Add($"paused {s} Windows services");
        int a = LowerBackgroundApps(); if (a > 0) notes.Add($"lowered {a} background apps");
        if (FreeRamPercent() < 35) { var mb = FreeStandbyRam(); if (mb > 50) notes.Add($"freed {mb:0} MB RAM"); }
        // keep RAM free during long sessions on low-memory PCs
        _ramTimer = new Timer(_ => { try { if (FreeRamPercent() < 12) FreeStandbyRam(); } catch { } }, null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3));
        return notes;
    }

    public static void End()
    {
        _ramTimer?.Dispose(); _ramTimer = null;
        RestoreBackgroundApps();
        ResumeServices();
    }
}

/// <summary>
/// Writes low-end graphics presets straight into a game's own settings file (with a backup). For weak PCs
/// in-game settings are by far the biggest FPS lever: often +30-80% FPS. Only for games that keep settings
/// in a plain local file, and only while the game is closed.
/// </summary>
public static class GameConfig
{
    public record Change(string Key, string Value);
    public record Profile(string GameId, string Label, Func<string> Path, Change[] Changes, string Format, string Note);

    public static readonly Profile[] Profiles =
    {
        new("minecraft", "Minecraft Java: low-end settings",
            () => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "options.txt"),
            new Change[]
            {
                new("renderDistance", "8"), new("simulationDistance", "6"), new("graphicsMode", "0"), new("particles", "2"),
                new("entityShadows", "false"), new("ao", "false"), new("biomeBlendRadius", "0"), new("mipmapLevels", "0"),
                new("renderClouds", "\"false\""), new("enableVsync", "false"), new("maxFps", "260"), new("entityDistanceScaling", "0.75"),
            }, "colon",
            "Render distance 8, Fast graphics, minimal particles, no clouds or smooth lighting. Biggest FPS gain in Minecraft. For even more FPS install Sodium (Fabric)."),
        new("fortnite", "Fortnite: low-end settings",
            () => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Config", "WindowsClient", "GameUserSettings.ini"),
            new Change[]
            {
                new("sg.ViewDistanceQuality", "1"), new("sg.AntiAliasingQuality", "0"), new("sg.ShadowQuality", "0"),
                new("sg.GlobalIlluminationQuality", "0"), new("sg.ReflectionQuality", "0"), new("sg.PostProcessQuality", "0"),
                new("sg.TextureQuality", "0"), new("sg.EffectsQuality", "0"), new("sg.FoliageQuality", "0"), new("sg.ShadingQuality", "0"),
                new("bUseVSync", "False"), new("bMotionBlur", "False"), new("bShowGrass", "False"),
            }, "ini",
            "Every quality setting to Low, no shadows, V-Sync and motion blur off. For the most FPS also pick 'Performance' rendering mode in Fortnite's Video settings."),
    };

    public static Profile For(string gameId) => Profiles.FirstOrDefault(p => p.GameId == gameId);

    static string Backup(Profile p) => p.Path() + ".pcoptimizer-backup";

    public static bool Exists(Profile p) { try { return File.Exists(p.Path()); } catch { return false; } }
    public static bool HasBackup(Profile p) { try { return File.Exists(Backup(p)); } catch { return false; } }

    /// <summary>Returns how many settings were changed.</summary>
    public static int Apply(Profile p)
    {
        string path = p.Path();
        var lines = File.ReadAllLines(path).ToList();
        if (!File.Exists(Backup(p))) File.Copy(path, Backup(p));
        int changed = 0;
        foreach (var c in p.Changes)
        {
            string sep = p.Format == "colon" ? ":" : "=";
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (!l.StartsWith(c.Key + sep, StringComparison.Ordinal)) continue;
                var nl = c.Key + sep + c.Value;
                if (l != nl) { lines[i] = nl; changed++; }
            }
        }
        File.WriteAllLines(path, lines);
        Store.Log($"GameConfig: applied {p.GameId} ({changed} changes)");
        return changed;
    }

    public static void Restore(Profile p)
    {
        File.Copy(Backup(p), p.Path(), overwrite: true);
        File.Delete(Backup(p));
    }
}
