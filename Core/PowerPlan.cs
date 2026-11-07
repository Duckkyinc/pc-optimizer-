using System.Text.RegularExpressions;

namespace Overdrive.Core;

public static class PowerPlan
{
    public const string UltimateTemplate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    public record Plan(string Guid, string Name, bool Active);

    public static List<Plan> List()
    {
        var r = Shell.Run("powercfg", "/list");
        var plans = new List<Plan>();
        foreach (var line in r.Output.Split('\n'))
        {
            var m = GuidRx.Match(line);
            if (!m.Success) continue;
            var nm = Regex.Match(line, @"\((.*)\)");
            plans.Add(new Plan(m.Value.ToLowerInvariant(), nm.Success ? nm.Groups[1].Value : m.Value, line.TrimEnd().EndsWith("*")));
        }
        return plans;
    }

    static string _activeCache;
    static DateTime _activeAt;

    public static string ActiveGuid()
    {
        if (_activeCache != null && (DateTime.Now - _activeAt).TotalSeconds < 3) return _activeCache;
        var m = GuidRx.Match(Shell.Run("powercfg", "/getactivescheme").Output);
        _activeCache = m.Success ? m.Value.ToLowerInvariant() : null;
        _activeAt = DateTime.Now;
        return _activeCache;
    }

    public static string ActiveName()
    {
        var o = Shell.Run("powercfg", "/getactivescheme").Output;
        var nm = Regex.Match(o, @"\((.*)\)");
        return nm.Success ? nm.Groups[1].Value : "Unknown";
    }

    public static bool SetActive(string guid)
    {
        _activeCache = null;
        return Shell.Run("powercfg", $"/setactive {guid}").Code == 0;
    }

    /// <summary>The best plan available on this PC: Ultimate if Windows allows it, otherwise High Performance.</summary>
    public static string EnsureBestPlan()
    {
        var plans = List();
        var saved = Store.Settings.UltimatePlanGuid;
        if (saved != null && plans.Any(p => p.Guid == saved)) return saved;

        var r = Shell.Run("powercfg", $"-duplicatescheme {UltimateTemplate}");
        var m = GuidRx.Match(r.Output);
        if (r.Code == 0 && m.Success && !m.Value.Equals(UltimateTemplate, StringComparison.OrdinalIgnoreCase))
        {
            Store.Settings.UltimatePlanGuid = m.Value.ToLowerInvariant();
            Store.SaveSettings();
            return Store.Settings.UltimatePlanGuid;
        }
        if (plans.Any(p => p.Guid == HighPerformance)) return HighPerformance;
        // Some laptops (Modern Standby) hide High Performance; try restoring it.
        r = Shell.Run("powercfg", $"-duplicatescheme {HighPerformance}");
        m = GuidRx.Match(r.Output);
        if (r.Code == 0 && m.Success) return m.Value.ToLowerInvariant();
        return null;
    }

    public static bool IsBestPlanActive()
    {
        var a = ActiveGuid();
        if (a == null) return false;
        return a == Store.Settings.UltimatePlanGuid || a == HighPerformance || a == UltimateTemplate;
    }

    public static Tweak CreateTweak() => new()
    {
        Id = "win.powerplan",
        Title = "Ultimate Performance power plan",
        Description = "Stops Windows from parking CPU cores and slowing your CPU down between frames. Fixes a lot of random stutter and FPS dips, especially on desktops.",
        Impact = Impact.Medium,
        Recommended = true,
        Warning = "Uses more power. On a laptop, keep it plugged in while gaming.",
        IsApplied = IsBestPlanActive,
        Apply = () =>
        {
            var prev = ActiveGuid();
            var best = EnsureBestPlan() ?? throw new InvalidOperationException("This PC doesn't allow high-performance power plans (common on Modern Standby laptops). Set Windows Settings > System > Power > Power mode to 'Best performance' instead.");
            if (!Store.Backups.ContainsKey("win.powerplan"))
            {
                Store.Backups["win.powerplan"] = new TweakBackup { Extra = { ["previous"] = prev ?? Balanced } };
                Store.SaveBackups();
            }
            if (!SetActive(best)) throw new InvalidOperationException("Windows refused to switch power plans.");
        },
        Revert = () =>
        {
            var prev = Store.Backups.TryGetValue("win.powerplan", out var b) && b.Extra.TryGetValue("previous", out var g) ? g : Balanced;
            if (prev == Store.Settings.UltimatePlanGuid || prev == HighPerformance) prev = Balanced;
            if (!SetActive(prev)) SetActive(Balanced);
            Store.Backups.Remove("win.powerplan");
            Store.SaveBackups();
        }
    };
}
