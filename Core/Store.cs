using System.IO;
using System.Text.Json;

namespace Overdrive.Core;

/// <summary>One registry value as it was before Overdrive touched it.</summary>
public class RegBackup
{
    public string Hive { get; set; }
    public string Path { get; set; }
    public string Name { get; set; }
    public bool Existed { get; set; }
    public string Kind { get; set; }
    public string Data { get; set; }
}

/// <summary>Everything needed to undo one tweak.</summary>
public class TweakBackup
{
    public DateTime AppliedAt { get; set; } = DateTime.Now;
    public List<RegBackup> Registry { get; set; } = new();
    public Dictionary<string, string> Extra { get; set; } = new();
}

public class CustomGame
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string ExePath { get; set; }
}

public class SpeedRecord
{
    public DateTime At { get; set; }
    public double Down { get; set; }
    public double Up { get; set; }
    public double? Ping { get; set; }
    public string Grade { get; set; }
    public string Via { get; set; }
}

public class SimStage
{
    public string Name { get; set; }
    public double Avg { get; set; }
    public double Low1 { get; set; }
    public double Low01 { get; set; }
    public double FrameMs { get; set; }
    public int Frames { get; set; }
}

public class SimRun
{
    public DateTime At { get; set; }
    public string Mode { get; set; }
    public string Gpu { get; set; }
    public string Resolution { get; set; }
    public double Score { get; set; }
    public double GpuScore { get; set; }
    public double CpuScore { get; set; }
    public double CappedAt { get; set; }
    /// <summary>Bumped whenever the test scenes change, so old and new scores are never compared.</summary>
    public int TestVersion { get; set; }
    public List<SimStage> Stages { get; set; } = new();
}

public class BenchResult
{
    public string Game { get; set; }
    public string Label { get; set; }
    public double AvgFps { get; set; }
    public double Low1 { get; set; }
    public double FrameTime { get; set; }
    public DateTime At { get; set; }
}

public class AppSettings
{
    public string UltimatePlanGuid { get; set; }
    public Dictionary<string, string> GameExePaths { get; set; } = new();
    public List<CustomGame> CustomGames { get; set; } = new();
    public bool AutoBoost { get; set; } = true;
    public bool AutoBoostDefaulted { get; set; }
    public List<string> BoostKillList { get; set; } = new();
    public bool StreamerMode { get; set; }
    public bool AutoUpdateCheck { get; set; } = true;
    public long InstalledReleaseId { get; set; }
    public bool AutoInstallUpdates { get; set; } = true;
    /// <summary>null = decide automatically (on for low-end PCs).</summary>
    public bool? LowEndMode { get; set; } = true;
    public bool FpsOverlay { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool TrayTipShown { get; set; }
    public List<BenchResult> Bench { get; set; } = new();
    public List<SimRun> SimRuns { get; set; } = new();
    public string TweakView { get; set; } = "grid";
    public bool GridView { get; set; } = true;
    public bool WifiScanPauseWhileGaming { get; set; }
    public List<string> WifiScanPausedOn { get; set; } = new(); // interfaces we paused (so a crash can't leave scanning off)
    public List<SpeedRecord> SpeedHistory { get; set; } = new();
    public string AimGame { get; set; } = "valorant";
    public double AimDpi { get; set; } = 800;
    public double AimSens { get; set; } = 0.35;
}

public static class Store
{
    public static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCOptimizer");

    static string BackupFile => System.IO.Path.Combine(Dir, "backup.json");
    static string SettingsFile => System.IO.Path.Combine(Dir, "settings.json");
    public static string LogFile => System.IO.Path.Combine(Dir, "pc-optimizer.log");

    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Dictionary<string, TweakBackup> Backups { get; private set; } = new();
    public static AppSettings Settings { get; private set; } = new();

    public static void Load()
    {
        Directory.CreateDirectory(Dir);
        Backups = Read<Dictionary<string, TweakBackup>>(BackupFile) ?? new();
        Settings = Read<AppSettings>(SettingsFile) ?? new();
        if (!Settings.AutoBoostDefaulted) { Settings.AutoBoost = true; Settings.AutoBoostDefaulted = true; SaveSettings(); } // Game Boost is on by default
    }

    static T Read<T>(string file) where T : class
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : null; }
        catch (Exception ex) { Log($"Could not read {file}: {ex.Message}"); return null; }
    }

    static void Write(string file, object value)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Dir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, file, true);
        }
    }

    public static void SaveBackups() => Write(BackupFile, Backups);
    public static void SaveSettings() => Write(SettingsFile, Settings);

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
