using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;

namespace Overdrive.Core;

/// <summary>
/// Reads Windows' own crash records (Event Viewer) from the last 30 days and explains in plain words what
/// crashed, how often, the most likely cause and the fix.
/// </summary>
public static class CrashFinder
{
    public enum Kind { App, Hang, BlueScreen, GpuReset, Shutdown, Hardware }

    public record Crash(Kind Kind, string Name, DateTime At, string Module, string Code);
    public record Group(Kind Kind, string Name, int Count, DateTime Last, string Cause, string Fix, string Action, bool IsGame);
    public record Report(List<Group> Groups, int AppCrashes, int BlueScreens, int GpuResets, int Shutdowns, int HardwareErrors);

    static IEnumerable<EventRecord> Read(string log, string xpath)
    {
        var q = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
        using var reader = new EventLogReader(q);
        for (int i = 0; i < 2000; i++)
        {
            EventRecord e;
            try { e = reader.ReadEvent(); } catch { yield break; }
            if (e == null) yield break;
            yield return e;
        }
    }

    static string Prop(EventRecord e, int i) { try { return e.Properties.Count > i ? e.Properties[i].Value?.ToString() : null; } catch { return null; } }

    public static Report Scan(int days = 30)
    {
        long ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;
        string since = $"TimeCreated[timediff(@SystemTime) <= {ms}]";
        var crashes = new List<Crash>();

        try
        {
            foreach (var e in Read("Application", $"*[System[Provider[@Name='Application Error'] and (EventID=1000) and {since}]]"))
                crashes.Add(new(Kind.App, Strip(Prop(e, 0)), e.TimeCreated ?? DateTime.Now, Prop(e, 3), Prop(e, 6)));
            foreach (var e in Read("Application", $"*[System[Provider[@Name='Application Hang'] and (EventID=1002) and {since}]]"))
                crashes.Add(new(Kind.Hang, Strip(Prop(e, 0)), e.TimeCreated ?? DateTime.Now, null, null));
        }
        catch (Exception ex) { Store.Log("Crash finder (app log): " + ex.Message); }

        try
        {
            foreach (var e in Read("System", $"*[System[(EventID=1001) and {since}]]"))
            {
                if (e.ProviderName?.Contains("WER-SystemErrorReporting") != true && e.ProviderName != "BugCheck") continue;
                string text = ""; try { text = e.FormatDescription() ?? ""; } catch { }
                var m = Regex.Match(text + " " + Prop(e, 0), @"0x0*([0-9a-fA-F]{1,8})");
                crashes.Add(new(Kind.BlueScreen, "Blue screen", e.TimeCreated ?? DateTime.Now, null, m.Success ? m.Groups[1].Value.ToUpperInvariant() : null));
            }
            foreach (var e in Read("System", $"*[System[Provider[@Name='Display'] and (EventID=4101) and {since}]]"))
                crashes.Add(new(Kind.GpuReset, "Graphics driver", e.TimeCreated ?? DateTime.Now, null, null));
            foreach (var e in Read("System", $"*[System[(EventID=13 or EventID=14 or EventID=153) and {since}]]"))
                if (e.ProviderName?.StartsWith("nvlddmkm", StringComparison.OrdinalIgnoreCase) == true)
                    crashes.Add(new(Kind.GpuReset, "Graphics driver", e.TimeCreated ?? DateTime.Now, null, null));
            foreach (var e in Read("System", $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) and {since}]]"))
                crashes.Add(new(Kind.Shutdown, "Unexpected shutdown", e.TimeCreated ?? DateTime.Now, null, null));
            foreach (var e in Read("System", $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=1 or EventID=17 or EventID=18 or EventID=19) and {since}]]"))
                crashes.Add(new(Kind.Hardware, "Hardware error", e.TimeCreated ?? DateTime.Now, null, null));
        }
        catch (Exception ex) { Store.Log("Crash finder (system log): " + ex.Message); }

        // One driver reset logs several events (Display 4101 + nvlddmkm 13/14/153) within seconds: count it once.
        var resets = crashes.Where(c => c.Kind == Kind.GpuReset).OrderBy(c => c.At).ToList();
        crashes.RemoveAll(c => c.Kind == Kind.GpuReset);
        DateTime lastReset = DateTime.MinValue;
        foreach (var r in resets) { if ((r.At - lastReset).TotalSeconds > 60) crashes.Add(r); lastReset = r.At; }

        var games = new HashSet<string>(Games.All.SelectMany(g => g.ProcessNames), StringComparer.OrdinalIgnoreCase);
        var groups = new List<Group>();
        foreach (var g in crashes.GroupBy(c => (c.Kind, Name: c.Kind is Kind.App or Kind.Hang ? c.Name.ToLowerInvariant() : c.Kind.ToString())))
        {
            var list = g.ToList();
            var first = list[0];
            string name = first.Name;
            bool isGame = games.Contains(Path.GetFileNameWithoutExtension(name ?? ""));
            var (cause, fix, action) = Explain(first.Kind, list, isGame);
            groups.Add(new(first.Kind, Pretty(name), list.Count, list.Max(c => c.At), cause, fix, action, isGame));
        }
        // games and system crashes first, then by count
        groups = groups.OrderByDescending(x => x.Kind is Kind.BlueScreen or Kind.Hardware or Kind.GpuReset or Kind.Shutdown ? 2 : x.IsGame ? 1 : 0)
                       .ThenByDescending(x => x.Count).ToList();
        return new Report(groups, crashes.Count(c => c.Kind is Kind.App or Kind.Hang), crashes.Count(c => c.Kind == Kind.BlueScreen),
            crashes.Count(c => c.Kind == Kind.GpuReset), crashes.Count(c => c.Kind == Kind.Shutdown), crashes.Count(c => c.Kind == Kind.Hardware));
    }

    static string Strip(string exe) => string.IsNullOrEmpty(exe) ? "Unknown app" : exe;
    static string Pretty(string exe) => exe?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true ? exe[..^4] : exe;

    static (string Cause, string Fix, string Action) Explain(Kind kind, List<Crash> list, bool isGame)
    {
        switch (kind)
        {
            case Kind.GpuReset:
                return ("Your graphics driver stopped responding and Windows restarted it. Games freeze, go black or crash when this happens.",
                    "Update or clean-reinstall your GPU driver. If you overclock your GPU, remove the overclock. Check GPU temperatures in FPS Monitor.", "gpu");
            case Kind.Shutdown:
                return ("Your PC turned off or restarted without shutting down properly (no blue screen was saved).",
                    "Usually power or heat: check that the PC isn't overheating, the power supply is strong enough for your GPU, and cables are seated. On laptops, check the battery and charger.", null);
            case Kind.Hardware:
                return ("Windows logged hardware errors (WHEA). This usually means an unstable CPU or RAM overclock, or failing hardware.",
                    "Remove any CPU/RAM overclock or undervolt, set BIOS to defaults, update the BIOS, and run Windows Memory Diagnostic.", "memtest");
            case Kind.BlueScreen:
            {
                var code = list.GroupBy(c => c.Code).OrderByDescending(g => g.Count()).First().Key ?? "";
                var (what, fix) = BugCheck(code);
                return ($"Windows crashed with a blue screen{(code.Length > 0 ? $" (code 0x{code})" : "")}: {what}", fix, code is "124" or "1A" ? "memtest" : null);
            }
            case Kind.Hang:
                return ("Stopped responding (froze) and was closed.",
                    isGame ? "Often a GPU driver or shader-compile freeze. Update the GPU driver, let shaders finish compiling, and keep the game on an SSD." : "Update the app. If it keeps freezing, reinstall it.", isGame ? "gpu" : null);
        }
        var module = list.Where(c => !string.IsNullOrEmpty(c.Module)).GroupBy(c => c.Module, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "";
        var exc = list.Where(c => !string.IsNullOrEmpty(c.Code)).GroupBy(c => c.Code).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "";
        string m = module.ToLowerInvariant();
        if (Regex.IsMatch(m, @"^nv(wgf2um|oglv|gpucomp|warp|api|d3dum|cuda)"))
            return ($"Crashed inside the NVIDIA driver ({module}).", "Update or clean-reinstall the NVIDIA driver. If it started after a driver update, roll back to the previous one. Remove any GPU overclock.", "gpu");
        if (Regex.IsMatch(m, @"^(amdx|atiu|atio|aticfx|amdvlk|amdihk)"))
            return ($"Crashed inside the AMD graphics driver ({module}).", "Update or clean-reinstall the AMD driver (AMD Cleanup Utility, then the newest Adrenalin). Remove any GPU overclock.", "gpu");
        if (Regex.IsMatch(m, @"^(igd|igxx|ig\d)"))
            return ($"Crashed inside the Intel graphics driver ({module}).", "Update the Intel graphics driver.", "gpu");
        if (Regex.IsMatch(m, @"^(d3d1[12]|dxgi|d3d9|dxcore)"))
            return ($"Crashed in DirectX ({module}), usually caused by the GPU driver.", "Update your GPU driver and Windows. Remove any GPU overclock.", "gpu");
        if (Regex.IsMatch(m, @"^(vcruntime|msvcp|ucrtbase|vcomp|mfc|concrt)"))
            return ($"Crashed in the Visual C++ runtime ({module}).", "Reinstall the Microsoft Visual C++ Redistributable (both x64 and x86), then restart.", "vcredist");
        if (m.StartsWith("jvm") || m.StartsWith("java") || m.StartsWith("lwjgl") || m.StartsWith("opengl32"))
            return ($"Crashed in Java / Minecraft ({module}).", "Give Minecraft 4-6 GB of RAM in the launcher, update Java and your mods, and update the GPU driver.", "gpu");
        if (m.StartsWith("easyanticheat") || m.StartsWith("beclient") || m.StartsWith("vgk") || m.StartsWith("eac"))
            return ($"Crashed in the anti-cheat ({module}).", "Repair the game's anti-cheat from its install folder or launcher, and make sure Windows is up to date.", null);
        if (exc == "c0000374" || exc == "c0000005" && list.Count >= 5 && m is "ntdll.dll" or "kernelbase.dll")
            return ($"Memory errors inside {(module.Length > 0 ? module : "Windows")} (exception {exc}). If several different apps crash like this, your RAM may be unstable.",
                "Turn off any RAM overclock/XMP to test, then run Windows Memory Diagnostic. If only this app crashes, reinstall or verify its files.", "memtest");
        if (exc == "c0000409" || exc == "e06d7363" || m is "ntdll.dll" or "kernelbase.dll" or "")
            return ($"Crashed inside the {(isGame ? "game" : "app")} itself{(module.Length > 0 ? $" ({module})" : "")}.",
                isGame ? "Verify the game files in its launcher, update the game and GPU driver, and turn off overlays (Discord, Steam, etc.)." : "Update or reinstall the app.", isGame ? "verify" : null);
        return ($"Crashed in {module}{(exc.Length > 0 ? $" (exception {exc})" : "")}.",
            isGame ? "Verify the game files in its launcher and update the game and GPU driver." : "Update or reinstall the app.", isGame ? "verify" : null);
    }

    static (string What, string Fix) BugCheck(string code) => code switch
    {
        "116" or "117" => ("the graphics driver stopped responding.", "Update or clean-reinstall your GPU driver and remove any GPU overclock."),
        "133" => ("a driver took too long (DPC watchdog).", "Run Driver Latency Check in Pro Lab to find the slow driver, and update storage/chipset/Wi-Fi drivers."),
        "124" => ("a hardware error (usually unstable CPU/RAM overclock or voltage).", "Set BIOS to defaults, remove overclocks/undervolts, update the BIOS."),
        "1A" => ("a memory management error, often bad or unstable RAM.", "Turn off XMP/EXPO to test, then run Windows Memory Diagnostic."),
        "A" or "D1" => ("a driver accessed memory it shouldn't.", "Update your drivers (GPU, Wi-Fi, chipset). If it started after installing something, uninstall it."),
        "3B" or "7E" or "1E" => ("a driver or system file crashed.", "Update GPU and chipset drivers and Windows. Run 'sfc /scannow' in an admin terminal."),
        "50" => ("bad memory access (driver, RAM or antivirus).", "Update drivers, remove third-party antivirus to test, and run Windows Memory Diagnostic."),
        "9F" => ("a driver didn't handle sleep/wake properly.", "Update chipset, Wi-Fi and GPU drivers."),
        "EF" => ("a critical Windows process stopped.", "Run 'sfc /scannow' and check your SSD's health below."),
        "139" => ("a driver corrupted memory.", "Update your drivers, especially GPU and anti-cheat/antivirus drivers."),
        "7A" or "77" or "F4" => ("Windows couldn't read from the drive.", "Check the drive's health below and reseat or replace the drive/cable."),
        _ => ("a system or driver error.", "Update Windows and your drivers. If it keeps happening, run Windows Memory Diagnostic."),
    };
}

/// <summary>Full PC health check: crashes, drives, RAM speed, monitor, drivers, battery, uptime and more.</summary>
public static class HealthCheck
{
    public enum Level { Good, Warn, Bad }
    public record Item(string Area, string Title, string Detail, Level Level, string Fix, string Action);
    public record Result(int Score, List<Item> Items, CrashFinder.Report Crashes);

    public static Result Run()
    {
        var items = new List<Item>();
        void Add(string area, string title, string detail, Level lv, string fix = null, string action = null) => items.Add(new(area, title, detail, lv, fix, action));

        // ---- crashes
        CrashFinder.Report crashes = null;
        try
        {
            crashes = CrashFinder.Scan();
            int sys = crashes.BlueScreens + crashes.GpuResets + crashes.HardwareErrors;
            int gameCrashes = crashes.Groups.Where(g => g.IsGame).Sum(g => g.Count);
            if (sys + gameCrashes == 0 && crashes.Shutdowns == 0)
                Add("Crashes", "No crashes in the last 30 days", "No blue screens, driver resets or game crashes.", Level.Good);
            else
                Add("Crashes", $"{crashes.BlueScreens} blue screens, {crashes.GpuResets} driver resets, {gameCrashes} game crashes", "See Crash Finder below for the cause of each.",
                    crashes.BlueScreens + crashes.HardwareErrors > 0 || crashes.GpuResets > 2 ? Level.Bad : Level.Warn);
        }
        catch (Exception ex) { Store.Log("Health: crashes: " + ex.Message); }

        // ---- drives
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage"); scope.Connect();
            var rel = new Dictionary<string, ManagementObject>();
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_StorageReliabilityCounter")))
                foreach (ManagementObject o in s.Get()) rel[o["DeviceId"]?.ToString() ?? ""] = o;
            using var ps = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject d in ps.Get())
            {
                string name = d["FriendlyName"]?.ToString() ?? "Drive";
                int health = Convert.ToInt32(d["HealthStatus"] ?? 0); // 0 healthy, 1 warning, 2 unhealthy
                int media = Convert.ToInt32(d["MediaType"] ?? 0); // 3 HDD, 4 SSD
                string type = media == 3 ? "hard drive" : media == 4 ? "SSD" : "drive";
                rel.TryGetValue(d["DeviceId"]?.ToString() ?? "", out var r);
                int? wear = r?["Wear"] is object w ? Convert.ToInt32(w) : null;
                int? temp = r?["Temperature"] is object t ? Convert.ToInt32(t) : null;
                long? hours = r?["PowerOnHours"] is object h ? Convert.ToInt64(h) : null;
                var parts = new List<string>();
                if (wear is int wv && wv > 0) parts.Add($"{100 - wv}% life left");
                if (temp is int tv && tv > 0) parts.Add($"{tv}°C");
                if (hours is long hv && hv > 0) parts.Add($"{hv / 24:N0} days powered on");
                var lv = health == 2 ? Level.Bad : health == 1 || wear >= 90 || temp >= 70 ? Level.Warn : Level.Good;
                Add("Drives", $"{name} ({type}): {(health == 0 ? "healthy" : health == 1 ? "warning" : "failing")}", parts.Count > 0 ? string.Join("  ·  ", parts) : "Health reported by Windows.",
                    lv, lv == Level.Good ? null : "Back up your files now. A drive in warning/failing state can stop working without notice." + (temp >= 70 ? " It's also running hot: improve airflow." : ""));
            }
        }
        catch (Exception ex) { Store.Log("Health: drives: " + ex.Message); }
        try
        {
            foreach (var di in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
            {
                double freePct = 100.0 * di.AvailableFreeSpace / Math.Max(1, di.TotalSize);
                double freeGb = di.AvailableFreeSpace / 1073741824.0;
                if (freePct < 10 || freeGb < 20)
                    Add("Drives", $"Drive {di.Name.TrimEnd('\\')} is almost full ({freeGb:0} GB free)", "Under 10-15% free space slows Windows down and can make games stutter while loading.",
                        freePct < 5 ? Level.Bad : Level.Warn, "Use Cleanup to remove junk files, and uninstall games you don't play.", "cleanup");
            }
        }
        catch { }

        // ---- RAM speed (XMP/EXPO)
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            var sticks = s.Get().Cast<ManagementObject>().ToList();
            if (sticks.Count > 0)
            {
                int cfg = sticks.Max(m => Convert.ToInt32(m["ConfiguredClockSpeed"] ?? 0));
                int type = sticks.Max(m => Convert.ToInt32(m["SMBIOSMemoryType"] ?? 0));
                string kind = type == 34 ? "DDR5" : type == 26 ? "DDR4" : "RAM";
                bool slow = type == 26 && cfg > 0 && cfg <= 2666 || type == 34 && cfg > 0 && cfg <= 4800;
                bool single = sticks.Count == 1 && !SysInfo.HasBattery;
                if (slow && !SysInfo.HasBattery)
                    Add("Memory", $"{kind} running at {cfg} MT/s: XMP/EXPO is probably off", "Most gaming RAM is sold to run faster, but only does after you turn on XMP (Intel) or EXPO (AMD) in the BIOS. It's often +5-15% FPS in CPU-heavy games.",
                        Level.Warn, "Restart into the BIOS, find XMP / EXPO / DOCP and turn it on (Profile 1). If the PC becomes unstable, turn it back off.");
                else Add("Memory", $"{SysInfo.RamGb:0} GB {kind} at {cfg} MT/s", single ? "One RAM stick: two matching sticks (dual channel) can add 10-20% FPS in some games." : "Running at a good speed.", single ? Level.Warn : Level.Good,
                        single ? "Add a matching second stick so the memory runs in dual channel." : null);
            }
        }
        catch (Exception ex) { Store.Log("Health: ram: " + ex.Message); }

        // ---- monitor refresh rate
        try
        {
            var d = Display.GetPrimary();
            if (d != null && d.MaxHz > d.CurrentHz + 5)
                Add("Display", $"Your monitor is running at {d.CurrentHz} Hz but supports {d.MaxHz} Hz", "You're missing out on smoothness: higher refresh rate means smoother motion and less input delay.", Level.Bad, $"Switch to {d.MaxHz} Hz.", "refresh");
            else if (d != null) Add("Display", $"Monitor at its max refresh rate ({d.CurrentHz} Hz)", $"{d.Width} x {d.Height}", Level.Good);
        }
        catch { }

        // ---- GPU driver age
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DriverDate, DriverVersion FROM Win32_VideoController");
            foreach (ManagementObject v in s.Get())
            {
                string name = v["Name"]?.ToString() ?? "";
                if (!Regex.IsMatch(name, "NVIDIA|AMD|Radeon|Intel", RegexOptions.IgnoreCase) || name.Contains("Basic", StringComparison.OrdinalIgnoreCase)) continue;
                var raw = v["DriverDate"]?.ToString();
                if (raw == null || raw.Length < 8) continue;
                var date = ManagementDateTimeConverter.ToDateTime(raw);
                int months = (int)((DateTime.Now - date).TotalDays / 30);
                Add("Drivers", $"{name} driver from {date:MMM yyyy}", months >= 6 ? $"{months} months old. New drivers often fix crashes and add FPS in new games." : "Recent enough.",
                    months >= 12 ? Level.Bad : months >= 6 ? Level.Warn : Level.Good, months >= 6 ? "Update it from the NVIDIA App, AMD Software or Intel's site." : null, months >= 6 ? "gpu" : null);
            }
            using var basic = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController WHERE Name LIKE '%Basic Display%'");
            if (basic.Get().Count > 0) Add("Drivers", "No real graphics driver installed", "Windows is using 'Microsoft Basic Display Adapter', so games run terribly or not at all.", Level.Bad, "Install the driver for your graphics card.", "gpu");
        }
        catch (Exception ex) { Store.Log("Health: gpu driver: " + ex.Message); }

        // ---- GPU temperature right now
        try
        {
            var hw = HwMonitor.Read();
            if (hw?.GpuTemp is double gt && gt > 0)
                Add("Temperature", $"GPU at {gt:0}°C right now", gt >= 85 ? "Very hot. The GPU will slow itself down to cool off, costing FPS." : gt >= 75 ? "Warm (normal under heavy load)." : "Cool.",
                    gt >= 85 ? Level.Bad : Level.Good, gt >= 85 ? "Clean dust from the fans and case, improve airflow, or set a more aggressive fan curve." : null);
        }
        catch { }

        // ---- battery (laptops)
        try
        {
            if (SysInfo.HasBattery)
            {
                var scope = new ManagementScope(@"\\.\root\wmi"); scope.Connect();
                double full = 0, design = 0;
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity")))
                    foreach (ManagementObject o in s.Get()) full += Convert.ToDouble(o["FullChargedCapacity"]);
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DesignedCapacity FROM BatteryStaticData")))
                    foreach (ManagementObject o in s.Get()) design += Convert.ToDouble(o["DesignedCapacity"]);
                if (design > 0 && full > 0)
                {
                    double pct = Math.Min(100, full / design * 100);
                    Add("Battery", $"Battery health {pct:0}%", $"It holds {pct:0}% of the charge it did when new.", pct < 60 ? Level.Bad : pct < 80 ? Level.Warn : Level.Good,
                        pct < 80 ? "Normal wear. Below 60% a replacement battery is worth it. Play plugged in for full performance." : null);
                }
            }
        }
        catch { }

        // ---- Windows: uptime, power plan, startup apps
        try
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            if (up.TotalDays >= 7)
                Add("Windows", $"Your PC hasn't restarted in {up.TotalDays:0} days", "Fast Startup means 'Shut down' doesn't fully reset Windows. Leaks and stuck updates build up.", Level.Warn, "Restart your PC (Restart, not Shut down) once in a while.");
        }
        catch { }
        try
        {
            if (!PowerPlan.IsBestPlanActive() && !SysInfo.HasBattery)
                Add("Windows", $"Power plan: {PowerPlan.ActiveName()}", "Your CPU may be slowing down to save power.", Level.Warn, "Game Boost switches to max power while you play. Turn on 'Ultimate power plan' in the Windows tab to keep it always.", "windows");
        }
        catch { }
        try
        {
            int on = StartupApps.List().Count(a => a.Enabled);
            if (on >= 12) Add("Windows", $"{on} apps start with Windows", "Every one of them uses RAM and CPU in the background, even while you game.", on >= 20 ? Level.Bad : Level.Warn, "Turn off the ones you don't need in Cleanup › Startup apps.", "cleanup");
        }
        catch { }

        int score = 100 - items.Sum(i => i.Level == Level.Bad ? 15 : i.Level == Level.Warn ? 6 : 0);
        return new Result(Math.Clamp(score, 0, 100), items.OrderByDescending(i => i.Level).ToList(), crashes);
    }
}
