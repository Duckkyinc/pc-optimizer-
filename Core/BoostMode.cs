using System.Diagnostics;
using System.Management;
using System.Windows.Threading;

namespace Overdrive.Core;

/// <summary>
/// Game-time boost: when a known game starts, Overdrive switches to the best power plan,
/// raises the game's priority and closes the apps you picked. Everything goes back when you quit.
/// </summary>
public static class BoostMode
{
    static DispatcherTimer _timer;
    static GameDef _active;
    static string _planBefore;
    static bool _timerSet;

    public static event Action<string> StatusChanged;
    public static string Status { get; private set; } = "Idle";
    public static GameDef ActiveGame => _active;

    public static void Start()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick -= Tick;
        _timer.Tick += Tick;
        _timer.Start();
    }

    static bool _ticking;
    static async void Tick(object s, EventArgs e)
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            if (!Store.Settings.AutoBoost)
            {
                if (_active != null) await Task.Run(End);
                return;
            }
            var running = await Task.Run(() => Games.All.FirstOrDefault(Games.IsOnScreen));
            if (running != null && _active == null) await Task.Run(() => Begin(running));
            else if (running == null && _active != null) await Task.Run(End);
        }
        catch (Exception ex) { Store.Log("Boost tick failed: " + ex.Message); }
        finally { _ticking = false; }
    }

    public static void Begin(GameDef g)
    {
        _active = g;
        var notes = new List<string>();
        try
        {
            _planBefore = PowerPlan.ActiveGuid();
            var best = PowerPlan.EnsureBestPlan();
            if (best != null && best != _planBefore && PowerPlan.SetActive(best)) notes.Add("max power plan");
            else _planBefore = null;
        }
        catch (Exception ex) { Store.Log("Boost plan failed: " + ex.Message); }

        // Note: PC Optimizer never opens or touches the game process itself (anti-cheat safety).
        // Step out of the game's way: our own process runs below normal priority while you play.
        try { using var me = Process.GetCurrentProcess(); me.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        try
        {
            if (Native.NtSetTimerResolution(5000, true, out _) == 0) { _timerSet = true; notes.Add("0.5ms timer"); }
        }
        catch { }

        try { WifiScan.Pause(); if (Store.Settings.WifiScanPausedOn.Count > 0) notes.Add("Wi-Fi scanning paused"); } catch { }

        if (LowEnd.Enabled) { try { notes.AddRange(LowEnd.Begin()); } catch (Exception ex) { Store.Log("Low-end boost failed: " + ex.Message); } }

        try { ProBalance.Start(); if (ProBalance.Running) notes.Add("ProBalance on"); } catch (Exception ex) { Store.Log("ProBalance failed: " + ex.Message); }
        try { Task.Run(TrafficShaper.Begin); if (Store.Settings.TrafficShaper) notes.Add("downloads slowed"); } catch { }

        int closed = 0;
        foreach (var name in Store.Settings.BoostKillList)
            foreach (var p in SafeGet(name))
            {
                try { p.CloseMainWindow(); if (!p.WaitForExit(1500)) p.Kill(); closed++; } catch { }
            }
        if (closed > 0) notes.Add($"closed {closed} background app{(closed == 1 ? "" : "s")}");

        SetStatus($"Boosting {g.Name}" + (notes.Count > 0 ? ": " + string.Join(", ", notes.Distinct()) : ""));
        Store.Log(Status);
    }

    public static void End()
    {
        if (_active == null) return;
        if (_planBefore != null) { try { PowerPlan.SetActive(_planBefore); } catch { } }
        if (_timerSet) { try { Native.NtSetTimerResolution(5000, false, out _); } catch { } _timerSet = false; }
        try { WifiScan.Resume(); } catch { }
        try { LowEnd.End(); } catch { }
        try { ProBalance.Stop(); } catch { }
        try { TrafficShaper.End(); } catch { }
        try { using var me = Process.GetCurrentProcess(); me.PriorityClass = ProcessPriorityClass.Normal; } catch { }
        Store.Log($"Boost ended for {_active.Name}");
        _active = null; _planBefore = null;
        SetStatus("Idle");
    }

    static Process[] SafeGet(string name)
    {
        try { return Process.GetProcessesByName(name); } catch { return Array.Empty<Process>(); }
    }

    static void SetStatus(string s)
    {
        Status = s;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => StatusChanged?.Invoke(s));
    }

    /// <summary>Apps that are usually safe to close while gaming, if the user has them running.</summary>
    public static readonly (string Process, string Label)[] SuggestedKill =
    {
        ("chrome", "Google Chrome"), ("msedge", "Microsoft Edge"), ("firefox", "Firefox"), ("opera", "Opera / Opera GX"),
        ("OneDrive", "OneDrive"), ("Teams", "Microsoft Teams"), ("ms-teams", "Microsoft Teams (new)"),
        ("Spotify", "Spotify"), ("EpicGamesLauncher", "Epic Games Launcher (after the game starts)"),
        ("Dropbox", "Dropbox"), ("AdobeCollabSync", "Adobe sync"), ("Creative Cloud", "Adobe Creative Cloud"),
        ("PhoneExperienceHost", "Phone Link"), ("Widgets", "Windows Widgets"), ("CCleaner64", "CCleaner"),
    };
}

/// <summary>Pauses Windows' background Wi-Fi scanning (WLAN AutoConfig) during a game, and always turns it back on after.</summary>
public static class WifiScan
{
    public static void Pause()
    {
        if (!Store.Settings.WifiScanPauseWhileGaming) return;
        foreach (var name in Catalog.WifiInterfaces())
        {
            var r = Shell.Run("netsh", $"wlan set autoconfig enabled=no interface=\"{name}\"", 8000);
            if (r.Code == 0 && !Store.Settings.WifiScanPausedOn.Contains(name)) Store.Settings.WifiScanPausedOn.Add(name);
        }
        Store.SaveSettings();
    }

    /// <summary>Turn scanning back on for every interface we paused. Also runs at app start in case of a crash.</summary>
    public static void Resume()
    {
        if (Store.Settings.WifiScanPausedOn.Count == 0) return;
        foreach (var name in Store.Settings.WifiScanPausedOn.ToList())
            Shell.Run("netsh", $"wlan set autoconfig enabled=yes interface=\"{name}\"", 8000);
        Store.Settings.WifiScanPausedOn.Clear();
        Store.SaveSettings();
    }
}

public static class RestorePoint
{
    public static void Create(string description)
    {
        // Windows only allows one restore point per 24h by default; lift that for this call.
        Reg.Set(Microsoft.Win32.RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore",
            "SystemRestorePointCreationFrequency", 0, Microsoft.Win32.RegistryValueKind.DWord);
        // Make sure System Protection is on for C:
        Shell.Run("powershell", "-NoProfile -Command \"Enable-ComputerRestore -Drive ($env:SystemDrive + '\\')\"", 60000);

        using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
        using var inParams = cls.GetMethodParameters("CreateRestorePoint");
        inParams["Description"] = description;
        inParams["RestorePointType"] = 12; // MODIFY_SETTINGS
        inParams["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
        using var result = cls.InvokeMethod("CreateRestorePoint", inParams, null);
        var code = Convert.ToUInt32(result?["ReturnValue"] ?? 1u);
        if (code != 0) throw new InvalidOperationException($"Windows couldn't create a restore point (code {code}). System Protection may be turned off.");
        Store.Log("Restore point created: " + description);
    }
}
