using System.Diagnostics;
using System.IO;

namespace Overdrive.Core;

/// <summary>
/// Installs/updates itself: whatever exe you open (Downloads, a zip folder...), it copies itself to one fixed place,
/// keeps a Desktop + Start menu shortcut pointing there, and launches that. Opening a newer download = update.
/// </summary>
public static class SelfInstall
{
    public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PC Optimizer");
    public static readonly string InstallExe = Path.Combine(InstallDir, "PC Optimizer.exe");

    static string Me => Environment.ProcessPath;

    /// <summary>Any copy of this app, whatever the exe is called ("PC Optimizer v1.4.1.exe", old "Overdrive.exe"...).</summary>
    public static bool IsOurProcess(string name) =>
        name.StartsWith("PC Optimizer", StringComparison.OrdinalIgnoreCase) || name.StartsWith("PC-Optimizer", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Overdrive", StringComparison.OrdinalIgnoreCase);

    public static bool RunningInstalled => string.Equals(Path.GetFullPath(Me ?? ""), Path.GetFullPath(InstallExe), StringComparison.OrdinalIgnoreCase);

    static string VersionFile => Path.Combine(InstallDir, "version.txt");

    /// <summary>Build stamp like "1.4.1+20261105230012" of the installed copy.</summary>
    static string InstalledBuild()
    {
        try { return File.Exists(InstallExe) && File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim() : null; } catch { return null; }
    }

    static string Stamp(string build) => build?.Split('+').LastOrDefault() ?? "";

    /// <summary>Returns true if this process should exit because the installed copy was launched instead.</summary>
    public static bool HandleStartup(string myBuild, Action closeOthers)
    {
        if (Me == null || RunningInstalled) return false;
        try
        {
            var installed = InstalledBuild();
            bool newer = installed == null || string.CompareOrdinal(Stamp(myBuild), Stamp(installed)) > 0;
            if (newer)
            {
                closeOthers(); // the old copy has to stop before its file can be replaced
                Directory.CreateDirectory(InstallDir);
                CopyWithRetry(Me, InstallExe);
                File.WriteAllText(VersionFile, myBuild);
                Store.Log($"Installed {myBuild} to {InstallExe} (was {installed ?? "not installed"})");
            }
            MakeShortcuts();
            Process.Start(new ProcessStartInfo(InstallExe, newer ? "--updated" : "") { UseShellExecute = true, WorkingDirectory = InstallDir });
            return true;
        }
        catch (Exception ex)
        {
            Store.Log("Self-install failed, running from download location: " + ex.Message);
            return false; // just run from wherever it is
        }
    }

    static void CopyWithRetry(string from, string to)
    {
        for (int i = 0; ; i++)
        {
            try { File.Copy(from, to, true); return; }
            catch (IOException) when (i < 15) { Thread.Sleep(400); }
            catch (UnauthorizedAccessException) when (i < 15) { Thread.Sleep(400); }
        }
    }

    public static void MakeShortcuts()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        foreach (var dir in new[] { desktop, start })
        {
            try
            {
                var lnk = Path.Combine(dir, "PC Optimizer.lnk");
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) continue;
                dynamic shell = Activator.CreateInstance(t);
                dynamic sc = shell.CreateShortcut(lnk);
                sc.TargetPath = InstallExe;
                sc.WorkingDirectory = InstallDir;
                sc.IconLocation = InstallExe + ",0";
                sc.Description = "PC Optimizer";
                sc.Save();
            }
            catch (Exception ex) { Store.Log($"Shortcut in {dir} failed: {ex.Message}"); }
        }
    }
}
